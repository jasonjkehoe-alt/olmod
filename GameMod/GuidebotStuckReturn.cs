using HarmonyLib;
using Overload;
using System;
using System.Globalization;
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
    // LIMITLESS (Nov 19): the recoveries above have no per-goal cap. The
    // 3-per-goal budget was removed — it bit hardest in the worst stretches
    // (three wedges close together and the bot went dead). The trigger paces
    // recovery, every recovery is logged, and the bot only recovers when the
    // player is on the goal route, so it is catching up, not cheating.
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
        // LIMITLESS (Nov 19): there is deliberately no cap on recoveries or
        // teleports per goal. The 3-per-goal budget was removed because it bit
        // hardest in the worst stretches (the slot, the pipe and 42/807 are all
        // on the way to the key, close together — three wedges and the bot went
        // dead). The trigger itself paces recovery (~3-5 s wedged per firing),
        // every recovery is logged, and the bot only recovers when the player
        // is on the goal route (behind -> return, ahead -> teleport), so it is
        // catching up, not cheating. The per-goal counters below are now just
        // for the log lines (they reset on a goal change); nothing gates on them.

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
        private static int s_recoveries;         // return recoveries on the current goal (log only, no cap)
        private static AIGuidebotSubmodeType s_recoveryGoal = AIGuidebotSubmodeType.NONE;
        private static bool s_accessorsLogged;
        // Teleport counter: SEPARATE from the return counter (the key route has
        // three blocks; a shared counter would conflate them). Both are log-only
        // now — there is no budget, so no exhausted state and no time window.
        private static int s_teleports;          // teleports on the current goal (log only, no cap)
        private static AIGuidebotSubmodeType s_teleportGoal = AIGuidebotSubmodeType.NONE;
        private static bool s_teleportAccessorsLogged;

        // Free-space stall sensor (Nov 23 flight): the stock
        // recovery only counts wall-contact resets (stay > 50), and a
        // bot stalled in OPEN SPACE never trips it -- stay peaked at 23
        // in 150 s while the bot sat 13.4 m from the portal, 492
        // stuck-watch lines, zero recoveries. The stillness test is NET
        // DISPLACEMENT, not instantaneous speed (hardened Nov 24, model
        // review): a bot that has moved under 0.5 m in 12 s averages
        // under 0.042 m/s, which is a stall by any measure. There is
        // deliberately NO per-frame |v| threshold -- the old |v| < 0.05
        // hard reset meant a single physics tick at |v| = 0.052 (the
        // actual stall log reads |v| = 0.05, right on the boundary)
        // reset the whole 12 s window, so the sensor could silently
        // never fire. Velocity is still read for the fire log line only.
        private const float kFreeStallDestDist = 1.0f;    // destination farther than this counts as unreached (same as the stuck-watch)
        private const float kFreeStallMove = 0.5f;        // the bot must not have moved this far from the window-open position
        private const float kFreeStallWindow = 12.0f;     // continuous still window before the sensor fires
        private const float kForcefieldWaitRadius = 6.0f; // an active forcefield within this of the bot = a legitimate field wait, not a stall (measured to the field's closest point, not its pivot -- see ClosestFieldDistance)
        private static float s_freeStallSince = -1f;      // game time the current still window opened (-1 = none)
        private static Vector3 s_freeStallRefPos;         // bot position when the current still window opened

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

            float gameTime = (float)GameplayManager.m_game_time;

            // Free-space stall sensor (Nov 23 flight): a second,
            // independent trigger -- the stock counter above only fires on
            // wall contact, so a bot stalled in open space never reaches
            // it. See CheckFreeSpaceStall: 12 s still, destination far,
            // position unchanged. Either trigger runs the same recovery
            // below (return / teleport-forward, limitless, logged).
            bool freeSpaceStall = CheckFreeSpaceStall(__instance, gameTime);

            if (s_resetFirings < kResetFiringsToTrigger && !freeSpaceStall)
                return;
            s_resetFirings = 0;

            // Only interrupt a goal submode. Never while already returning,
            // wandering, deactivated, or idle.
            AIGuidebotSubmodeType submode = Robot.AI_guidebot_submode;
            if (!IsGoalSubmode(submode))
                return;

            // Late-fire guard (Nov 23, c0472d38 flight): the stock
            // counter can stay over 50 while the bot is sliding along
            // a slope it already freed itself from (lines 13687-13691:
            // |v| 2.29-2.54, stay 9-17, StuckReturn fired at 13690
            // anyway and teleported a moving bot). A bot that is
            // actually moving is not wedged: defer (the episode
            // counter was already zeroed above, so the count rebuilds
            // only if it wedges again) instead of firing. Threshold
            // 1.0 m/s: the freed bot was at 2.3+, a wedged bot sits
            // near 0. Fail-open: a missing rigidbody (or any read
            // failure) proceeds with the recovery as before.
            if (s_rigidbodyField != null)
            {
                try
                {
                    Rigidbody rb = (Rigidbody)s_rigidbodyField.GetValue(__instance);
                    if (rb != null && rb.velocity.magnitude > 1.0f)
                    {
                        Debug.Log("[GuidebotStuckReturn] deferring recovery: bot moving |v|=" +
                            rb.velocity.magnitude.ToString("F2", CultureInfo.InvariantCulture) +
                            " -- freed itself, not wedged");
                        return;
                    }
                }
                catch (Exception) { /* fail-open: proceed with recovery */ }
            }

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

            // Player BEHIND the bot on the route: the return branch. LIMITLESS:
            // the per-goal counter below is log-only (reset on a goal change so
            // the log count reads per goal). There is no cap, no give-up and no
            // time window — a wedged bot always comes back while the player is
            // behind it on the route.
            if (submode != s_recoveryGoal)
            {
                s_recoveryGoal = submode;
                s_recoveries = 0;
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

        // The goal submodes the recovery interrupts (shared by the
        // stock-reset trigger above and the free-space sensor below):
        // everything except the return / wander / idle set. The sensor
        // uses the same list, so a window can only accumulate while the
        // bot is actually guiding toward a goal -- it can never fire on
        // resume from a return.
        static bool IsGoalSubmode(AIGuidebotSubmodeType submode)
        {
            return submode != AIGuidebotSubmodeType.GO_TO_PLAYER &&
                   submode != AIGuidebotSubmodeType.WANDER_NEAR_PLAYER &&
                   submode != AIGuidebotSubmodeType.NONE &&
                   submode != AIGuidebotSubmodeType.GO_AWAY &&
                   submode != AIGuidebotSubmodeType.NAPPING &&
                   submode != AIGuidebotSubmodeType.START;
        }

        static void ResetFreeSpaceWindow()
        {
            s_freeStallSince = -1f;
        }

        // Free-space stall sensor (Nov 23 flight). Returns true
        // when the bot has a destination > 1.0 m away (the stuck-watch
        // threshold) and has not moved 0.5 m from its window-open
        // position for a continuous 12 s. Stillness is NET DISPLACEMENT
        // (hardened Nov 24, model review): under 0.5 m in 12 s is under
        // 0.042 m/s average, which is a stall by any measure. There is
        // deliberately NO per-frame |v| threshold -- a single physics
        // tick at |v| = 0.052 used to reset the whole window. The
        // stuck-watch condition in LogDestination (GuidebotSteering)
        // detects exactly this stall, but nothing acted on it: the stock
        // recovery counts consecutive stock stuck-resets, which fire only
        // on wall contact (stay > 50), and a bot stalled in open space
        // never trips them (stay peaked at 23 in 150 s). When this
        // returns true the caller runs the EXISTING recovery (return /
        // teleport-forward) -- this is a second trigger, not a second
        // recovery. Self-consuming: a true return resets the window, so
        // the recovery is attempted at most once per 12 s still episode;
        // a declined recovery (player off-route) re-arms the same way.
        // Normal slow flight cannot trip it: 12 s with an unreached
        // destination and under 0.5 m of movement is not flight, it is
        // a stall. Fail-open: any read failure resets the window and
        // returns false -- the stock wall-contact trigger is unaffected.
        static bool CheckFreeSpaceStall(Robot bot, float gameTime)
        {
            try
            {
                // Only while guiding toward a goal (see IsGoalSubmode).
                if (!IsGoalSubmode(Robot.AI_guidebot_submode))
                {
                    ResetFreeSpaceWindow();
                    return false;
                }
                Vector3 vel = Vector3.zero;
                if (s_rigidbodyField != null)
                {
                    try
                    {
                        Rigidbody rb = (Rigidbody)s_rigidbodyField.GetValue(bot);
                        if (rb != null)
                            vel = rb.velocity;
                    }
                    catch (Exception) { /* fail-open: vel stays zero, the position check below still guards */ }
                }
                Vector3 botPos = bot.transform.position;
                Vector3 dest = bot.m_next_destination_position;
                // No per-frame |v| test (see the constants comment): the
                // window's stillness test is the 0.5 m net-displacement
                // check below. Velocity is read above for the fire log
                // line only.
                bool still = Vector3.Distance(dest, botPos) > kFreeStallDestDist;
                if (!still)
                {
                    ResetFreeSpaceWindow();
                    return false;
                }
                if (s_freeStallSince < 0f || gameTime < s_freeStallSince)
                {
                    // No window open, or the game clock went backward
                    // (level restart): open a fresh window here.
                    s_freeStallSince = gameTime;
                    s_freeStallRefPos = botPos;
                    return false;
                }
                if (Vector3.Distance(botPos, s_freeStallRefPos) >= kFreeStallMove)
                {
                    // The bot moved: the stall broke (or it is drifting).
                    // Re-arm from the new position.
                    s_freeStallSince = gameTime;
                    s_freeStallRefPos = botPos;
                    return false;
                }
                if (gameTime - s_freeStallSince < kFreeStallWindow)
                    return false;

                // Window complete: a 12 s still stall. First, the
                // forcefield guard -- a bot legitimately waiting at an UP
                // forcefield is still, far from its destination, and
                // unmoved, i.e. it looks exactly like a free-space stall.
                // See IsWaitingAtActiveForcefield: an active forcefield
                // near the bot suppresses the sensor (re-arm, so it
                // re-checks after another 12 s if the field is still up).
                if (IsWaitingAtActiveForcefield(botPos))
                {
                    Debug.Log("[GuidebotStuckReturn] free-space sensor suppressed: bot still 12 s but an active forcefield is near (waiting for the field, not wedged); re-arming");
                    ResetFreeSpaceWindow();
                    return false;
                }

                ResetFreeSpaceWindow(); // consume the trigger
                Debug.Log("[GuidebotStuckReturn] free-space stall: bot still 12 s, |v|=" +
                    vel.magnitude.ToString("F2", CultureInfo.InvariantCulture) +
                    ", dest " + dest.ToString() + " " +
                    Vector3.Distance(dest, botPos).ToString("F1", CultureInfo.InvariantCulture) +
                    " m away -- running recovery");
                return true;
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotStuckReturn] free-space sensor failed: " + ex.ToString());
                ResetFreeSpaceWindow();
                return false;
            }
        }

        // True when the bot is pressing against an ACTIVE forcefield: a
        // Forcefield object is near the bot's position. An active
        // forcefield is a Forcefield whose GameObject is still active --
        // Deactivate() calls c_go.SetActive(false), and
        // FindObjectsOfType only returns active objects, so any
        // Forcefield found is up (the same API the game itself uses in
        // DeactivateAllForcefields / UpdateSegmentsForForcefields); the
        // activeSelf check is a second, explicit guard in case a future
        // Unity version returns inactive objects too. Distance is measured
        // to the CLOSEST point on the field (see ClosestFieldDistance),
        // not to the prop pivot (hardened Nov 24, model review): a D3
        // portal can be 12 m x 28 m, so a bot pressing the field near one
        // edge can be 8-14 m from transform.position while still touching
        // the field. The Nov 23 stall sat 23+ m from the field, so the
        // guard cannot confuse the two. Called only when the sensor
        // window completes (at most once per 12 s), never every frame,
        // so the scene-wide search costs nothing in steady flight.
        // Fail-open toward the recovery: a lookup failure returns false
        // (the player-position rule in the recovery itself is the second
        // guard -- a teleport past an UP field needs the player already
        // ahead of the bot on the route, i.e. already past the field --
        // and a return only ever brings the bot BACK to the player).
        static bool IsWaitingAtActiveForcefield(Vector3 botPos)
        {
            try
            {
                Forcefield[] fields = UnityEngine.Object.FindObjectsOfType<Forcefield>();
                if (fields == null)
                    return false;
                foreach (Forcefield ff in fields)
                {
                    if (ff == null || ff.gameObject == null || !ff.gameObject.activeSelf)
                        continue;
                    if (ClosestFieldDistance(ff, botPos) < kForcefieldWaitRadius)
                        return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotStuckReturn] forcefield check failed: " + ex.ToString());
                return false;
            }
        }

        // Distance from the bot to the closest point of a forcefield prop.
        // Collider.ClosestPoint is exact for the collider shape (a box/plane
        // collider on the field prop). Renderer bounds (world-space AABB) is
        // the fallback. The prop pivot is the last resort. Each step is
        // wrapped so a missing component degrades gracefully instead of
        // failing the guard -- a failed lookup must not let the sensor fire
        // past an UP field, so the fallback chain prefers the closest
        // available measure over giving up.
        static float ClosestFieldDistance(Forcefield ff, Vector3 botPos)
        {
            try
            {
                Collider col = ff.GetComponent<Collider>();
                if (col == null)
                    col = ff.GetComponentInChildren<Collider>();
                if (col != null)
                    return Vector3.Distance(botPos, col.ClosestPoint(botPos));
            }
            catch (Exception) { /* fall through to bounds */ }
            try
            {
                Renderer rend = ff.GetComponent<Renderer>();
                if (rend == null)
                    rend = ff.GetComponentInChildren<Renderer>();
                if (rend != null)
                    return Mathf.Sqrt(rend.bounds.SqrDistance(botPos));
            }
            catch (Exception) { /* fall through to pivot */ }
            return Vector3.Distance(botPos, ff.transform.position);
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
            // Separate teleport counter, log-only (reset on a goal change so the
            // log count reads per goal). LIMITLESS: no cap, no give-up, no time
            // window — a wedged bot always teleports forward while the player is
            // ahead of it on the route.
            if (submode != s_teleportGoal)
            {
                s_teleportGoal = submode;
                s_teleports = 0;
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
