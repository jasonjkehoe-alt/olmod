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
    // gives a genuinely wedged bot one of two recoveries, chosen by where the
    // player is on the goal route:
    //
    //   Player BEHIND the bot -> flip into return-to-player mode (GO_TO_PLAYER)
    //   so it comes back to the player, relying on the stock WANDER_NEAR_PLAYER
    //   resume branch to pick guiding back up within 10 m of the player.
    //
    //   Player AHEAD of the bot (past the block) -> TELEPORT-FORWARD: move the
    //   bot to the spawn position in front of the player, force the segment
    //   update, and rebuild the path from the new segment so guiding resumes
    //   forward. The bot is a hologram; re-materializing past a block it cannot
    //   fly through is the fiction-consistent recovery, and it is the only one
    //   that works when the block is between the bot and the player.
    //
    // The auto-resume half of the return needs no new code: WANDER_NEAR_PLAYER
    // already calls GuidebotResumePreviousSubmode() when the bot is within 10 m,
    // but the field it reads (AI_guidebot_previous_submode) is never assigned
    // anywhere in the stock game, so the branch can never fire. This patch
    // stores the interrupted goal submode in that existing field when it flips,
    // and stock code does the rest. The field is serialized, so a save/load
    // mid-recovery still resumes.
    //
    // The teleport half needs no new code for the re-path either: it moves the
    // bot instance (it does NOT destroy/respawn it) and calls the game's own
    // public CreatePathToSegment(goal) after forcing the segment update, so the
    // stock lead logic rebuilds Global_path from the bot's new segment.
    //
    // Decompile references (Assembly-CSharp, game 1.1.1886.0):
    //   stuck sensor: Robot.OnCollisionStay, counter m_collided_stay_count
    //   stock reset: Robot.DoPathFollowPreDefined, `if (m_collided_stay_count > 50)`
    //   return: Robot.GuidebotGoToPlayer() -> CreatePathToPlayer()
    //   resume: WANDER_NEAR_PLAYER branch + GuidebotResumePreviousSubmode()
    //   deactivate timer: Robot.DoModeGuidebot (30 s, not refreshed in GO_TO_PLAYER/WANDER)
    //   spawn position: Robot.SpawnGuideBot() (2 m in front, FindConnectedDistance
    //     1-2 hops, else 0.1 m fallback)
    //   segment update: MovingObject.LateUpdate -> UpdateCurrSegment() (position
    //     change triggers a re-derive; -1 falls back to last valid segment)
    //   re-path: Robot.CreatePathToSegment(end_seg, max_distance, out actual_distance)
    //     reads SegmentIndex, so the segment must be forced current first
    //   movement: Robot.DoPathfindMovement() drives the bot by Rigidbody.AddForce
    //     (velocity accumulates; drag damps it) — hence the velocity reset on move
    //   START case: DoModeGuidebot START calls GuidebotChooseWanderSegment()
    //     (it WANDERS — a respawned bot would not resume guiding on its own,
    //     which is why the teleport moves the instance instead of respawning)
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
        // Velocity reset on teleport only: a null here degrades to "no reset"
        // (drag damps the residual) instead of disabling the patch.
        private static readonly FieldInfo s_rigidbodyField = GetField("c_rigidbody");

        private static int s_resetFirings;       // consecutive stock-reset firings, no index progress
        private static int s_lastPathIndex = -1; // Global_path_index at last observation
        private static int s_recoveries;         // return recoveries spent on the current goal
        private static float s_lastRecoveryTime = -1000f;
        private static AIGuidebotSubmodeType s_recoveryGoal = AIGuidebotSubmodeType.NONE;
        private static AIGuidebotSubmodeType s_exhaustedGoal = AIGuidebotSubmodeType.NONE;
        private static bool s_accessorsLogged;
        // Teleport budget: SEPARATE from the return budget (the key route has
        // three blocks; a shared budget could be exhausted by one flaky block
        // and leave none for the others). Same rules, independent counters.
        private static int s_teleports;          // teleports spent on the current goal
        private static float s_lastTeleportTime = -1000f;
        private static AIGuidebotSubmodeType s_teleportGoal = AIGuidebotSubmodeType.NONE;
        private static AIGuidebotSubmodeType s_teleportExhaustedGoal = AIGuidebotSubmodeType.NONE;
        private static bool s_teleportAccessorsLogged;

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

            // Where is the player on the current goal route? Both recoveries
            // need this, so it is computed once, before either budget block.
            // (Previously this ran after the return budget block; moving it up
            // means the return budget block is skipped when the player is ahead
            // or off-route. That does not change the return branch's observable
            // behaviour: its goal-change reset and give-up only matter when a
            // return flip is actually attempted, i.e. with the player behind,
            // at which point the block runs with the correct goal state.)
            PlayerShip ship = GameManager.m_player_ship;
            if (ship == null)
                return;
            int playerSeg = ship.SegmentIndex;
            int playerPos = -1;
            for (int i = 0; i < __instance.Global_path_length; i++)
            {
                if (__instance.Global_path[i] == playerSeg) { playerPos = i; break; }
            }
            // Off-route, or the player is in the bot's own segment: stay put.
            if (playerPos < 0 || playerPos == idx)
                return;
            // Player AHEAD of the bot on the route (past the block): teleport
            // forward. The return branch below is not reached in this case.
            if (playerPos > idx)
            {
                TeleportForward(__instance, ship, playerSeg, playerPos, idx, submode, gameTime);
                return;
            }

            // Player BEHIND the bot on the route: the return branch, unchanged.
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

        // Teleport-forward: the bot is wedged and the player is AHEAD of it on
        // the goal route, past the block. Move the bot instance to the spawn
        // position in front of the player, force the segment update, and rebuild
        // the path from the new segment so guiding resumes forward. Returns true
        // if the bot was moved. The submode is NOT changed (it stays a goal
        // submode), so the stock lead logic keeps guiding; m_guidebot_lead_mode,
        // m_guidebot_goal and m_guidebot_special_goal_segment are not touched.
        static bool TeleportForward(Robot bot, PlayerShip ship, int playerSeg, int playerPos, int botIndex, AIGuidebotSubmodeType submode, float gameTime)
        {
            // Separate teleport budget: 3 per goal, independent of the return
            // budget, same reset rules (goal change, or 60 s without wedging).
            if (submode != s_teleportGoal)
            {
                s_teleportGoal = submode;
                s_teleports = 0;
                s_teleportExhaustedGoal = AIGuidebotSubmodeType.NONE;
            }
            if (submode == s_teleportExhaustedGoal)
            {
                if (gameTime - s_lastTeleportTime > kRecoveryBudgetWindow)
                {
                    s_teleportExhaustedGoal = AIGuidebotSubmodeType.NONE;
                    s_teleports = 0;
                }
                else
                {
                    Debug.Log("[GuidebotStuckReturn] teleport budget exhausted for goal " + submode + "; staying put.");
                    return false;
                }
            }
            if (gameTime - s_lastTeleportTime > kRecoveryBudgetWindow)
                s_teleports = 0;
            if (s_teleports >= kMaxRecoveriesPerGoal)
            {
                s_teleportExhaustedGoal = submode;
                Debug.Log("[GuidebotStuckReturn] giving up teleporting for goal " + submode + " after " + s_teleports + " teleports; leaving the bot wandering.");
                return false;
            }

            // Spawn position, mirroring SpawnGuideBot: 2 m in front of the
            // player when that spot is 1-2 hops from the player's segment,
            // else 0.1 m in front. Unlike SpawnGuideBot, the fallback spot's
            // segment is also validated: if even that has no containing
            // segment, abort instead of moving the bot into the void.
            Vector3 playerPos3 = ship.transform.position;
            Vector3 playerFwd = ship.transform.forward;
            Vector3 vector = playerPos3 + playerFwd * 2f;
            int newSeg = GameManager.m_level_data.FindSegmentContainingWorldPosition(vector, playerSeg);
            if (newSeg != -1)
            {
                int pathLength;
                Pathfinding.FindConnectedDistance(playerSeg, newSeg, out pathLength);
                if (pathLength < 1 || pathLength > 2)
                {
                    vector = playerPos3 + playerFwd * 0.1f;
                    newSeg = GameManager.m_level_data.FindSegmentContainingWorldPosition(vector, playerSeg);
                }
            }
            else
            {
                vector = playerPos3 + playerFwd * 0.1f;
                newSeg = GameManager.m_level_data.FindSegmentContainingWorldPosition(vector, playerSeg);
            }
            if (newSeg == -1)
            {
                Debug.Log("[GuidebotStuckReturn] teleport aborted: no containing segment at the spawn position.");
                return false;
            }

            int fromSeg = bot.Global_path[botIndex];
            s_teleports++;
            s_lastTeleportTime = gameTime;

            // Move the bot instance. Do NOT destroy/respawn: a respawned bot
            // lands in submode START, whose DoModeGuidebot case only calls
            // GuidebotChooseWanderSegment() — it wanders and would not resume
            // guiding on its own (the player would have to re-command it).
            // Rotation matches the player's, as SpawnGuideBot does.
            bot.transform.position = vector;
            bot.transform.rotation = ship.transform.rotation;

            // The bot moves by Rigidbody.AddForce, so stale momentum would
            // carry over (drag only damps it gradually). A teleported object
            // should not keep the velocity it had while wedged against a wall.
            // (A wedged bot's velocity is near zero, so this is hygiene, not
            // the fix itself; it is skipped, with one log line, if the field
            // is not found in a future game build.)
            if (s_rigidbodyField != null)
            {
                Rigidbody rb = (Rigidbody)s_rigidbodyField.GetValue(bot);
                if (rb != null)
                    rb.velocity = Vector3.zero;
            }
            else if (!s_teleportAccessorsLogged)
            {
                s_teleportAccessorsLogged = true;
                Debug.Log("[GuidebotStuckReturn] teleport: c_rigidbody not found; velocity not reset.");
            }

            // Force the segment update now. It normally runs in
            // MovingObject.LateUpdate, i.e. after this frame's AI tick, so
            // without this CreatePathToSegment below would read the OLD
            // SegmentIndex and rebuild the path from the blocked segment.
            // (UpdateCurrSegment falls back to the last valid segment if the
            // new position had no containing segment; the position was
            // validated above, so that cannot happen here.)
            MovingObject mo = bot.gameObject.GetMovingObject();
            if (mo != null)
                mo.UpdateCurrSegment();

            // Rebuild the path from the new segment to the goal, so the bot
            // guides forward instead of steering on the stale path (which
            // points back through the block). m_guidebot_special_goal_segment
            // is the goal's segment, whatever the goal is — the same value
            // GuideBotLeadPlayer passes. On failure CreatePathToSegment
            // empties the path and the stock lead logic retries within 3 s.
            float actualDistance;
            bool pathOk = bot.CreatePathToSegment(Robot.m_guidebot_special_goal_segment, 9999f, out actualDistance);
            if (!pathOk)
                Debug.Log("[GuidebotStuckReturn] teleport: no path from seg " + newSeg + " to goal seg " + Robot.m_guidebot_special_goal_segment + "; lead logic will retry.");

            // Spawn effect: the same appear cue + particle SpawnGuideBot uses
            // (particle 66). The move itself is instant (a pop); the cue and
            // particle make it read as a re-materialize rather than a glitch.
            ParticleManager.psm[0].StartParticle(66, vector, bot.transform.rotation, bot.transform);
            SFXCueManager.PlayCue2D(SFXCue.guidebot_response_appear);

            // One-shot deactivate refresh. (The submode is unchanged, so stock
            // code refreshes the timer every tick anyway; this covers the
            // transition.)
            s_deactivateTimerField.SetValue(null, gameTime + 30f);

            Debug.Log("[GuidebotStuckReturn] bot teleported forward (goal " + submode + ", seg " + fromSeg + " -> " + newSeg + ", player idx " + playerPos + " > bot idx " + botIndex + ", teleport " + s_teleports + "); resuming guide.");
            return true;
        }
    }
}
