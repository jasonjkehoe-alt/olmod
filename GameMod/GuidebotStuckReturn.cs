using HarmonyLib;
using Overload;
using System;
using System.Reflection;
using UnityEngine;

namespace GameMod
{
    // GuideBot stuck recovery: when the GuideBot wedges into terrain it cannot
    // pass (e.g. the terrain ridge at the south end of the outdoor lattice slot
    // in D3 L1, where the bot sits nose-up), the stock stuck-reset only re-aims
    // it at a centre on the same blocked route, so it loops forever. This patch
    // flips a genuinely wedged bot into return-to-player mode (GO_TO_PLAYER) so
    // it comes back to the player, and relies on the stock WANDER_NEAR_PLAYER
    // resume branch to pick guiding back up when it gets within 10 m of the
    // player.
    //
    // The auto-resume half needs no new code: WANDER_NEAR_PLAYER already calls
    // GuidebotResumePreviousSubmode() when the bot is within 10 m, but the field
    // it reads (AI_guidebot_previous_submode) is never assigned anywhere in the
    // stock game, so the branch can never fire. This patch stores the interrupted
    // goal submode in that existing field when it flips, and stock code does the
    // rest. The field is serialized, so a save/load mid-recovery still resumes.
    //
    // Decompile references (Assembly-CSharp, game 1.1.1886.0):
    //   stuck sensor: Robot.OnCollisionStay, counter m_collided_stay_count
    //   stock reset: Robot.DoPathFollowPreDefined, `if (m_collided_stay_count > 50)`
    //   return: Robot.GuidebotGoToPlayer() -> CreatePathToPlayer()
    //   resume: WANDER_NEAR_PLAYER branch + GuidebotResumePreviousSubmode()
    //   deactivate timer: Robot.DoModeGuidebot (30 s, not refreshed in GO_TO_PLAYER/WANDER)
    [HarmonyPatch(typeof(Robot), "DoPathFollowPreDefined")]
    public class Robot_DoPathFollowPreDefined_GuidebotStuckReturn
    {
        // How many consecutive stock stuck-resets (~1 s of continuous wall contact
        // each) with no path-index progress before the wedge counts as genuine.
        // The stock reset zeroes the counter every time it fires, so the raw
        // counter can never climb past 51 while wedged; counting reset firings
        // is the equivalent of a ~3 s threshold on the raw counter.
        private const int kResetFiringsToTrigger = 3;
        // Max recoveries for one goal before we stop flipping (anti yo-yo).
        private const int kMaxRecoveriesPerGoal = 3;
        // If the bot goes this long without wedging, the recovery budget resets.
        private const float kRecoveryBudgetWindow = 60f;

        // Private Robot members, resolved lazily-safe: if any lookup returns null
        // (a future game build renamed a member) the prefix no-ops and logs once
        // instead of breaking PatchAll for the whole mod. All names were verified
        // against the shipped Assembly-CSharp.dll (game 1.1.1886.0).
        private static readonly FieldInfo s_collidedStayCountField = GetField("m_collided_stay_count");
        private static readonly FieldInfo s_previousSubmodeField = GetField("AI_guidebot_previous_submode");
        private static readonly FieldInfo s_deactivateTimerField = GetField("m_guidebot_deactivate_timer");
        private static readonly MethodInfo s_guidebotGoToPlayerMethod = GetMethod("GuidebotGoToPlayer");

        private static int s_resetFirings;       // consecutive stock-reset firings, no index progress
        private static int s_lastPathIndex = -1; // Global_path_index at last observation
        private static int s_recoveries;         // recoveries spent on the current goal
        private static float s_lastRecoveryTime = -1000f;
        private static AIGuidebotSubmodeType s_recoveryGoal = AIGuidebotSubmodeType.NONE;
        private static AIGuidebotSubmodeType s_exhaustedGoal = AIGuidebotSubmodeType.NONE;
        private static bool s_accessorsLogged;

        private static FieldInfo GetField(string name)
        {
            try { return AccessTools.Field(typeof(Robot), name); }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotStuckReturn] field lookup failed for " + name + ": " + ex.Message);
                return null;
            }
        }

        private static MethodInfo GetMethod(string name)
        {
            try { return AccessTools.Method(typeof(Robot), name); }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotStuckReturn] method lookup failed for " + name + ": " + ex.Message);
                return null;
            }
        }

        static void Prefix(Robot __instance)
        {
            try { PrefixInner(__instance); }
            catch (Exception ex) { Debug.Log("[GuidebotStuckReturn] prefix failed: " + ex.ToString()); }
        }

        static void PrefixInner(Robot __instance)
        {
            // GuideBot only. Combat robots share DoPathFollowPreDefined and must
            // never get the return behaviour.
            if (!__instance.m_is_guide_bot)
                return;
            if (s_collidedStayCountField == null || s_previousSubmodeField == null ||
                s_deactivateTimerField == null || s_guidebotGoToPlayerMethod == null)
            {
                if (!s_accessorsLogged)
                {
                    s_accessorsLogged = true;
                    Debug.Log("[GuidebotStuckReturn] disabled: a required Robot member was not found in this game build.");
                }
                return;
            }
            if (__instance.Global_path_length <= 0 || __instance.Global_path == null)
                return;

            int idx = __instance.Global_path_index;

            // No onward route: the bot is at (or past) the final segment, so
            // there is nothing to be wedged against. (The stock code clamps the
            // index back to length-1 below; do not read Global_path[idx] here.)
            if (idx >= __instance.Global_path_length)
                return;

            // Path progress ends the stuck episode: entering a new path segment
            // zeroes the stock counter too, so a climbing counter already implies
            // no progress. (This resets the episode state only, not the recovery
            // budget — see the trigger below.)
            if (idx != s_lastPathIndex)
            {
                s_lastPathIndex = idx;
                s_resetFirings = 0;
            }

            // A stock stuck-reset is about to fire this call: the counter is over
            // 50 and the original method zeroes it below. One observation per firing.
            int count = (int)s_collidedStayCountField.GetValue(__instance);
            if (count > 50)
                s_resetFirings++;

            if (s_resetFirings < kResetFiringsToTrigger)
                return;
            s_resetFirings = 0;

            // Only interrupt a goal submode. Never while already returning,
            // wandering, deactivated, or idle.
            AIGuidebotSubmodeType submode = Robot.AI_guidebot_submode;
            if (submode == AIGuidebotSubmodeType.GO_TO_PLAYER ||
                submode == AIGuidebotSubmodeType.WANDER_NEAR_PLAYER ||
                submode == AIGuidebotSubmodeType.NONE ||
                submode == AIGuidebotSubmodeType.GO_AWAY ||
                submode == AIGuidebotSubmodeType.NAPPING ||
                submode == AIGuidebotSubmodeType.START)
                return;

            float gameTime = (float)GameplayManager.m_game_time;

            // Retry bound: after 3 recoveries for this goal without the bot going
            // 60 s without wedging, stop flipping and leave it wandering. A new
            // goal, or a quiet 60 s, lifts the give-up.
            if (submode != s_recoveryGoal)
            {
                s_recoveryGoal = submode;
                s_recoveries = 0;
                s_exhaustedGoal = AIGuidebotSubmodeType.NONE;
            }
            if (submode == s_exhaustedGoal)
            {
                if (gameTime - s_lastRecoveryTime > kRecoveryBudgetWindow)
                {
                    s_exhaustedGoal = AIGuidebotSubmodeType.NONE;
                    s_recoveries = 0;
                }
                else
                {
                    return;
                }
            }
            if (gameTime - s_lastRecoveryTime > kRecoveryBudgetWindow)
                s_recoveries = 0;
            if (s_recoveries >= kMaxRecoveriesPerGoal)
            {
                s_exhaustedGoal = submode;
                Debug.Log("[GuidebotStuckReturn] giving up on goal " + submode + " after " + s_recoveries + " recoveries; leaving the bot wandering.");
                return;
            }

            // Player-side guard: only flip when the player's segment is BEHIND the
            // bot on the current goal route (an earlier index in Global_path).
            // Return mode pathfinds through the same graph with the same lookahead,
            // so if the player is beyond the block the shortest path runs forward
            // through the blocked descent and the bot re-wedges. If the player is
            // not on the route behind the bot, stay put and wait for the player
            // (the stock lead-mode distance handling already covers separation).
            PlayerShip ship = GameManager.m_player_ship;
            if (ship == null)
                return;
            int playerSeg = ship.SegmentIndex;
            int playerPos = -1;
            for (int i = 0; i < __instance.Global_path_length; i++)
            {
                if (__instance.Global_path[i] == playerSeg) { playerPos = i; break; }
            }
            if (playerPos < 0 || playerPos >= idx)
                return;

            // Flip to return. Order matters: save the interrupted goal FIRST
            // (GuidebotGoToPlayer overwrites AI_guidebot_submode), then refresh
            // the deactivate timer (it is not refreshed in GO_TO_PLAYER/WANDER
            // and the bot would deactivate after 30 s), then flip.
            // m_guidebot_lead_mode is left alone (stays TO_GOAL) so leading
            // continues when the goal submode resumes. m_guidebot_goal and
            // m_guidebot_special_goal_segment are not touched by the flip, and
            // the goal case re-derives m_guidebot_goal_segment on resume, so
            // nothing else needs saving.
            s_recoveries++;
            s_lastRecoveryTime = gameTime;
            s_previousSubmodeField.SetValue(null, submode);
            s_deactivateTimerField.SetValue(null, gameTime + 30f);
            try
            {
                s_guidebotGoToPlayerMethod.Invoke(__instance, null);
            }
            catch (Exception ex)
            {
                // Roll the saved submode back: the flip did not happen, so the
                // stock resume branch must not later fire on a stale value.
                s_previousSubmodeField.SetValue(null, AIGuidebotSubmodeType.NONE);
                Debug.Log("[GuidebotStuckReturn] GuidebotGoToPlayer failed: " + ex.ToString());
                return;
            }
            Debug.Log("[GuidebotStuckReturn] bot wedged (goal " + submode + ", seg " + __instance.Global_path[idx] + ", recovery " + s_recoveries + "); returning to player.");
        }
    }
}
