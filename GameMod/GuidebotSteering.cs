using HarmonyLib;
using Overload;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GameMod
{
    // GuideBot steering Step 1 (Nov 22): minimal steering patch for the
    // outdoor-slot stall (design doc ~/workspace/guidebot-pathing-design.md
    // section 5). The GuideBot's body FITS the slot (0.60 m collider,
    // doorway TIGHT +0.02, ridge clears at y>=50) -- the stall is two
    // steering failures, not a body-doesn't-fit failure:
    //   (1) the stock code aims the bot at the portal CENTRE
    //       (GetTargetPointFromPortal returns the average of the 4 portal
    //       vertices); for 42<->807 that is (582.96, 48.23, 443.98), in the
    //       blocked region (the clear opening is at z 434-442, low in the
    //       doorway). The bot never gets within 1 m of it, so
    //       m_target_is_portal stays true and the destination never
    //       advances. This patch overrides that aim with the validated
    //       crossing point (582.96, 46.36, 442.20).
    //   (2) the bot follows the low path (y~46) into the terrain ridge
    //       instead of climbing. This patch routes it through a climb
    //       waypoint at y=50 over the ridge band (z 454-461).
    //
    // The patch is a Harmony PREFIX on Robot.DoPathfindMovement -- the
    // single choke point covering all destination-setting paths. That
    // method reads m_next_destination_position for aiming and movement
    // (AddForce toward it) but does NOT set the field; callers set it.
    // A prefix rewrites the field after the caller's assignment and
    // before the body, so it steers the bot every frame. FixedUpdate
    // calls DoAIModes (which calls DoPathfindMovement) BEFORE DoPathFollow
    // (which sets the next frame's destination), so the prefix steers on
    // the destination set last frame -- no frame of lag that matters.
    //
    // The leg is the leg the bot is TRAVERSING -- its own route
    // position to the next route segment -- not (index-1 -> index):
    // ResyncPathIndex pins the index to the bot's position (Nov 22
    // pull-back fix), so the index-based leg would be the leg INTO
    // the bot's segment, one leg too late for the doorway/ridge
    // rules, which must fire while the bot is still in the FROM
    // segment. The climb to y=50 takes the bot out of the route
    // segments (808/43/804 top at y=48.9) and into segment 1470's
    // volume, which is not on the route and not portal-connected
    // to it, so the bot's physical segment may be off-route while
    // it climbs; when it is not on the route at all, the leg falls
    // back to the index-based read. The ridge rule is additionally
    // position-based (bot inside the band AND near the ridge -- see
    // the proximity guard), so it applies even if the path index
    // is stuck.
    //
    // The ridge is a waypoint SEQUENCE, not a single fixed point: the
    // direct line from the doorway crossing to the lifted 808 centre is
    // BLOCKED (-0.594), but the line to the climb waypoint is CLEAR, and
    // the lines climb-wp -> lifted 43 -> lifted 804 are CLEAR. A literal
    // fixed waypoint alone would stall/oscillate (the stock segment-
    // advance logic has no waypoint-consumption step), so the patch aims
    // at the next unpassed waypoint in the sequence, and once past all
    // of them it stops overriding and normal pathing resumes. A forward
    // guard keeps the bot from turning back.
    //
    // Table (baked by the converter from the D3 source, shipped as a
    // mission data file "<level>-guidebot-crossings.txt", loose + in the
    // mission zip, following the texturemotion.txt pattern):
    //   GUIDEBOT_CROSSINGS
    //   1
    //   DOORWAY <from> <to> <cx> <cy> <cz> <near> <far>
    //       [<n_appr> <ax> <ay> <az> ...]   (optional approach
    //       waypoints, staging points on the FROM side in from->to
    //       order, played before the crossing; no suffix = the
    //       original single-point doorway)
    //   RIDGE <z_min> <z_max> <climb_y> <n_wp> <n_legs> <leg_from> <leg_to> ...
    //   <wp_x> <wp_y> <wp_z>        (n_wp lines, in route order)
    //
    // Spawn-exit fix (Nov 22) -- the e3e42ff9 flight shows the bot
    // stalls IN the spawn room even with a stable goal (166 kept-path
    // events in seg 217, index 0->1, never past 1; 87 in seg 216,
    // index never leaves 0). Three parts, all in this file:
    //   (A) Doorway waypoint SEQUENCE: the 217->213 portal-centre aim
    //       is blocked from ~60% of seg 217 by a slanted D3 wall face
    //       (centre line only +0.108 m clear at the 0.60 m collider);
    //       the validated crossing is 2.67 m north of the portal
    //       centre (392.63, 54.81, 444.42, +0.784), reached via the
    //       approach waypoint 217 centre (399.62, 54.81, 441.75,
    //       +1.745). The table carries the approach on the DOORWAY
    //       line (see above); TryDoorwayDestination plays the
    //       sequence (approach, then crossing; reversed for the return
    //       leg) reusing the TryRidgeDestination / IsPassed /
    //       forward-guard pattern.
    //   (B) Stale m_target_is_portal reset: InitPath sets the
    //       destination to the bot's own segment centre but never
    //       resets the flag, so after a goal flip the stock advance
    //       can never fire (the 216 stall). A postfix on
    //       Robot.CreatePathToSegment resets it -- but ONLY when the
    //       original actually ran (see the guard note on the postfix
    //       class: Harmony postfixes run even when the original was
    //       skipped, and clearing the flag mid-path would defeat the
    //       stock portal-advance gate).
    //   (C) Off-route resync target best+1 -> best (defense-in-depth):
    //       the stock match fires as soon as the bot enters
    //       Global_path[best] and sets the destination.
    //
    // TO_GOAL HOLD -- TEST-FLIGHT SETTING, NOT A HARD RULE (Jason,
    // Nov 22, for the next test flight ONLY): he agreed the bot
    // should hold TO_GOAL en route for the next test flight -- go for
    // the goal, don't worry about him -- so Fixes A-C can be judged
    // on a clean run. He explicitly said this is NOT a hard rule --
    // he is just talking it through, not making permanent rules, and
    // the follow behavior is to be revisited later. So the hold is a
    // clearly-marked, reversible test configuration (the
    // kHoldGuidebotToGoal_TestFlight flag on the GuideBotLeadPlayer
    // patch class, default ON for this test flight): it only
    // neutralises the TO_GOAL -> TO_PLAYER flip condition for one
    // call and restores the inputs afterwards. The TO_PLAYER path is
    // NOT deleted -- flip the flag to false (and rebuild) and the
    // stock following runs unchanged.
    //
    // Step 2 (Nov 22) -- route-position resync: the Step 1 flight log
    // proves Global_path_index NEVER advances (stuck at 0/1 the whole
    // flight, 9 StuckReturn teleports, ridge waypoints oscillating
    // 604.7 -> 605.9 -> 622.3 -> ...): DoPathFollowPreDefined advances
    // the index only when m_current_segment == Global_path[index], and
    // the bot's segment never matches -- it climbs out of the route
    // segments into 1470, and the stock portal-centre aim never gets it
    // within 1 m of the next segment. The Step 1 patch overrides the
    // destination but never the index, so the bot is pulled two ways and
    // circles. ResyncPathIndex (below) re-syncs the index to the bot's
    // actual route position every frame, before the leg is read: if the
    // bot's segment is on the route ahead of the index, jump the index
    // to the next segment after it; if the bot is off-route (1470), fall
    // back to the nearest route segment by centre distance (from
    // GameManager.m_level_data.Segments[].Center, the same source the
    // stock follower uses) and aim at the next one after it. In the
    // ridge band that lands on seg 804 (the route segment after the
    // ridge). On-route the index now also moves BACKWARD to the bot's
    // position (Nov 22 pull-back fix -- see the ridge-guard + index
    // pull-back section below), so a visibility-based advance past
    // the bot cannot stick; the off-route branch stays forward-only.
    // The crossings table and the crossing points are UNCHANGED --
    // they were correct; the index was the missing piece.
    //
    // Fix (Nov 22, route-resync fix) -- the resync alone was NOT enough:
    // the route-resync flight log shows the index keeps resetting to 0
    // (143 of 189 resyncs went to index 1; max 55 only after StuckReturn
    // teleports). The reset source, from the decompile (Assembly-CSharp,
    // game 1.1.1886.0): GuideBotLeadPlayer re-creates the path to the
    // goal every 3 s (sub_move_timer = 3f, L92942 TO_GOAL /
    // CreatePathToPlayer L92912) via CreatePathToSegment (L101898),
    // which calls InitPath (L101919), which sets Global_path_index = 0
    // (L101676) and m_next_destination_position to
    // Segments[Global_path[0]].Center (the bot's OWN segment centre).
    // Every 3 s the route is rebuilt from the bot's current segment, so
    // the index can never accumulate along a fixed route -- the resync
    // just sets it to 1 again. Two-part fix, both in this file:
    //   (1) Robot_CreatePathToSegment_GuidebotSteering (prefix): skip
    //       the re-creation when the GuideBot already has a valid path
    //       to the SAME goal and is ON that path. The caller sees
    //       success (__result = true), so GuideBotLeadPlayer keeps its
    //       goal and FindSegmentContainingKey still returns 1; the
    //       destination reset to Global_path[0].Center is overwritten
    //       the same frame by the DoPathfindMovement prefix. When the
    //       goal differs, the bot is off the path, or there is no path,
    //       the original runs and the path is (re)built -- the reset is
    //       legitimate then, and the resync re-syncs onto the fresh path.
    //       Logs the skip / re-create decision, naming InitPath as the
    //       reset source, so the next flight log proves the reset is gone.
    //   (2) Robot_DoPathFollow_GuidebotSteering (postfix on
    //       Robot.DoPathFollow): re-run ResyncPathIndex AFTER the stock
    //       follower, so a reset anywhere in the stock code
    //       (CreatePathToSegment/InitPath,
    //       CreatePathToBelievedPlayerPosition, GuidebotChooseWander-
    //       Segment) cannot stick past the frame it happened in.
    //       ResyncPathIndex pulls the index back to the bot's
    //       position on-route (Nov 22 pull-back fix), so a stock
    //       advance past the bot cannot stick past the frame it
    //       happened in either; the off-route branch stays
    //       forward-only.
    //
    // Ridge-guard + index pull-back (Nov 22) -- the d4491740 flight:
    // the spawn exit WORKED (doorway-approach + doorway-crossing fired,
    // bot in 213) and the TO_GOAL hold WORKED (goal 752, zero flips),
    // but the bot then went east off-route to 227/231 and stalled at a
    // wall in 231, 22 m from the spawn -- never leaving Jason's sight.
    // One mechanism, two instances: the destination is set to a far
    // point whose straight line is blocked, and nothing pulls it back.
    //   (1) Ridge hijack: the ridge rule fired on the z-band [450, 462]
    //       only, with NO x-proximity check, so it fired at the spawn
    //       room ~214 m west of the actual ridge -- and the route north
    //       from 213 runs through 850/849, both inside the band, so the
    //       route itself walks into it. The ridge destination (604.7,
    //       50.0, 456.0) is unreachable in a straight line from 213
    //       (-0.595 m). Fix: the ridge rule now fires only when the bot
    //       is within kRidgeProximity (30 m) of a ridge waypoint.
    //   (2) Index runaway: the stock MaybeAdvanceSegment advances the
    //       index every 0.2 s whenever the NEXT segment is merely
    //       visible -- visibility is not flyability -- so the index ran
    //       to 7 in 213 (destination seg 110, line blocked -0.346 m)
    //       and to 6 in 231 (destination seg 161, blocked -0.521 m),
    //       while the next route segments 850/858 sat clear (+0.108 /
    //       +0.147 m), never aimed at. Fix: ResyncPathIndex now pulls
    //       the index BACK to the bot's route position on-route, so
    //       the stock match (m_current_segment == Global_path[index])
    //       fires every frame and re-derives the destination to the
    //       NEXT route segment. The leg read in PrefixInner now uses
    //       the bot's route position (pos -> pos+1) for the same
    //       reason -- with the index pinned to the bot's position,
    //       the old (index-1 -> index) leg would be the leg INTO the
    //       bot's segment, and the doorway rule would only fire after
    //       the bot had already crossed. The doorway waypoints, the
    //       table, and TryDoorwayDestination are UNTOUCHED.
    //   The off-route branch of ResyncPathIndex stays forward-only
    //   (best). A routine 1-step pull-back (undoing the stock advance)
    //   is silent; a pull-back of 2+ is logged, so the flight log
    //   shows the runaway being corrected without spamming.
    //
    // Fail-open: no table, unreadable table, or no matching entry -> the
    // stock destination is used unchanged. Only the GuideBot is affected
    // (m_is_guide_bot gate); every other robot is untouched, as are the
    // aim rules, force, teleport fallback, return, auto-exit, and all
    // existing patches.
    //
    // Decompile references (Assembly-CSharp, game 1.1.1886.0):
    //   Robot.DoPathfindMovement (L91810): aims at
    //     m_next_destination_position, moves the rigidbody with AddForce.
    //   Robot.DoPathFollowPreDefined (L101683): GuideBot destination
    //     logic; GetTargetPointFromPortal (L101753) returns the portal
    //     centre; ValidateDestinationPos (L101646) is skipped for the
    //     GuideBot. MaybeAdvanceSegment (L101781) advances the index
    //     every 0.2 s if the NEXT segment is visible.
    //   Robot.FixedUpdate (L92016): DoAIModes before DoPathFollow.
    //   Fields (all public on Robot): m_next_destination_position
    //     (Vector3, L89398), m_is_guide_bot (bool, L89600),
    //     m_current_segment (int, L90056), Global_path (int[1000],
    //     L90086), Global_path_length (int, L90088),
    //     Global_path_index (int, L90090).
    [HarmonyPatch(typeof(Robot), "DoPathfindMovement")]
    public class Robot_DoPathfindMovement_GuidebotSteering
    {
        private class DoorwayEntry
        {
            public int From;
            public int To;
            public Vector3 Crossing;
            // Optional approach waypoints: staging points on the FROM
            // side, in from->to order, played before the crossing
            // (spawn-exit fix, Nov 22). Empty = the original
            // single-point doorway (42<->807).
            public List<Vector3> Approach = new List<Vector3>();
        }

        private class RidgeLeg
        {
            public int From;
            public int To;
        }

        private class RidgeEntry
        {
            public float ZMin;
            public float ZMax;
            public float ClimbY;
            public List<Vector3> Waypoints = new List<Vector3>();
            public List<RidgeLeg> Legs = new List<RidgeLeg>();
        }

        private class CrossingsTable
        {
            public List<DoorwayEntry> Doorways = new List<DoorwayEntry>();
            public List<RidgeEntry> Ridges = new List<RidgeEntry>();
        }

        // Cached table (per level). A failed load is remembered so the
        // prefix does not retry -- and log-spam -- every frame; the cache
        // is reset when the level changes.
        private static CrossingsTable s_table;
        private static string s_tableLevel;
        private static bool s_tableFailed;
        private static string s_lastDest;

        private const float kWaypointReach = 2.0f;

        // Ridge proximity guard (Nov 22, d4491740 flight): the ridge
        // rule used to fire on the z-band alone, anywhere in the level
        // at that z -- including the spawn room, ~214 m west of the
        // actual ridge. The ridge waypoints span x 604.69-622.32; the
        // spawn complex is x 387-434. A 30 m 3D distance to the nearest
        // ridge waypoint keeps the rule live for the real climb
        // (approach segments 42/807/808/43/804 are 27.1/19.6/15.9/4.3/
        // 3.8 m from a waypoint, measured from segment centres) and
        // dead everywhere else (nearest spawn segment is 171 m away).
        private const float kRidgeProximity = 30f;

        static void Prefix(Robot __instance)
        {
            try { PrefixInner(__instance); }
            catch (Exception ex) { Debug.Log("[GuidebotSteering] prefix failed: " + ex.ToString()); }
        }

        static void PrefixInner(Robot __instance)
        {
            // GuideBot only. DoPathfindMovement runs for every robot; the
            // gate keeps combat robots (and every other patch) untouched.
            if (!__instance.m_is_guide_bot)
                return;

            // ---- route-position resync (Step 2, Nov 22): re-sync
            // Global_path_index to the bot's actual route position
            // BEFORE the leg is read below (the doorway/ridge rules key
            // off the leg). Runs even with no crossings table -- the
            // stall is in the stock path follower, not the table.
            ResyncPathIndex(__instance);

            CrossingsTable table = GetTable();
            if (table == null)
                return; // fail-open: no table -> stock destination

            Vector3 botPos = __instance.transform.position;

            // Current leg: the leg the bot is TRAVERSING -- its own
            // route position to the next route segment -- not
            // (index-1 -> index). ResyncPathIndex pins the index to
            // the bot's position, so the index-based leg would be the
            // leg INTO the bot's segment, one leg too late: the
            // doorway/ridge rules must fire while the bot is still in
            // the FROM segment. (Ridge-guard + index pull-back fix,
            // Nov 22.) The bot's physical segment may be off-route
            // while climbing (see the header comment on segment
            // 1470), so when it is not on the route at all, fall back
            // to the index-based leg.
            int fromSeg = -1, toSeg = -1;
            if (__instance.Global_path != null && __instance.Global_path_length > 0)
            {
                int len = __instance.Global_path_length;
                int pos = -1;
                int cur = __instance.m_current_segment;
                for (int i = 0; i < len; i++)
                {
                    if (__instance.Global_path[i] == cur) { pos = i; break; }
                }
                if (pos >= 0 && pos + 1 < len)
                {
                    fromSeg = __instance.Global_path[pos];
                    toSeg = __instance.Global_path[pos + 1];
                }
                else
                {
                    int idx = __instance.Global_path_index;
                    if (idx >= 0 && idx < len)
                    {
                        toSeg = __instance.Global_path[idx];
                        fromSeg = (idx > 0)
                            ? __instance.Global_path[idx - 1]
                            : cur;
                    }
                }
            }

            // ---- doorway: leg match, either direction ----
            // Spawn-exit fix (Nov 22): a waypoint SEQUENCE, not a
            // single fixed point -- aim at the next unpassed waypoint
            // (approach, then crossing; reversed for the return leg),
            // reusing the TryRidgeDestination / IsPassed /
            // forward-guard pattern. A leg match with all waypoints
            // passed falls through to the ridge check, then to stock.
            foreach (var dw in table.Doorways)
            {
                bool fwd = (fromSeg == dw.From && toSeg == dw.To);
                bool rev = (fromSeg == dw.To && toSeg == dw.From);
                if (!fwd && !rev)
                    continue;
                Vector3 dest;
                string kind;
                if (TryDoorwayDestination(dw, fwd, botPos, out dest, out kind))
                {
                    SetDestination(__instance, dest, kind + " " + fromSeg + "->" + toSeg);
                    return;
                }
                break; // leg matched, waypoints exhausted: stock resumes
            }

            // ---- ridge: position-based band (applies even if the path
            // index is stuck off-route) ----
            foreach (var rg in table.Ridges)
            {
                if (botPos.z < rg.ZMin || botPos.z > rg.ZMax)
                    continue;
                // Ridge-guard (Nov 22): the z-band alone is not a
                // location -- it fired at the spawn room, ~214 m west
                // of the ridge. The rule fires only when the bot is
                // actually near the climb.
                if (!IsNearRidge(rg, botPos))
                    continue;
                Vector3 dest;
                if (TryRidgeDestination(rg, fromSeg, toSeg, botPos, out dest))
                {
                    SetDestination(__instance, dest, "ridge");
                    return;
                }
            }
            // No matching entry: stock destination (fail-open).
        }

        // Re-sync Global_path_index to the bot's actual route position.
        // On-route, the index moves BOTH ways: forward to the bot's
        // position (accelerating an advance the stock code would make
        // when the bot enters the segment, or skipping a segment the
        // bot is already physically beyond) AND back down to it -- the
        // pull-back (Nov 22, d4491740 flight): the stock
        // MaybeAdvanceSegment advances the index every 0.2 s whenever
        // the next segment is merely visible, so the index runs far
        // ahead of the bot and the destination sticks at a far
        // segment's blocked centre. Pulling it back to the bot's
        // position makes the stock match fire every frame and
        // re-derive the destination to the next route segment. The
        // off-route branch stays forward-only. One log line per actual
        // resync, so the next flight log shows whether the index
        // advances (a routine 1-step pull-back is silent; a pull-back
        // of 2+ is logged).
        // Internal (not private): the DoPathFollow postfix
        // (Robot_DoPathFollow_GuidebotSteering, route-resync fix) calls
        // it after the stock follower runs, to make the resync persistent.
        internal static void ResyncPathIndex(Robot bot)
        {
            try
            {
                if (bot.Global_path == null || bot.Global_path_length <= 0)
                    return;
                int idx = bot.Global_path_index;
                int len = bot.Global_path_length;
                int cur = bot.m_current_segment;
                if (cur < 0)
                    return; // no containing segment (e.g. mid-teleport): fail-open
                Vector3 botPos = bot.transform.position;

                // (a) Bot's segment IS on the route: pin the index TO
                // the bot's position, not past it. The stock
                // DoPathFollowPreDefined advance fires only on
                // m_current_segment == Global_path[index], and that
                // advance is what sets the destination (next segment
                // centre, or portal centre). Setting pos+1 skips the
                // match and leaves the destination stale -- the bot
                // hovers at its own segment centre (resync-deadlock
                // fix, Nov 22: the old pos+1 target stalled the bot
                // in seg 217 with index 1 for a whole flight). And
                // letting the index sit AHEAD of the bot is worse:
                // MaybeAdvanceSegment runs it ahead on mere
                // visibility, and the destination sticks at a far
                // segment's blocked centre (pull-back fix, Nov 22:
                // index 7 in seg 213, 6 in seg 231, destinations
                // 110/161 blocked while the next route segments
                // 850/858 were clear). Pinning the index to pos makes
                // the match fire every frame and re-derive the
                // destination to the next route segment.
                int pos = -1;
                for (int i = 0; i < len; i++)
                {
                    if (bot.Global_path[i] == cur) { pos = i; break; }
                }
                if (pos >= 0)
                {
                    int target = pos;
                    if (target > len - 1)
                        target = len - 1;
                    if (target != idx)
                    {
                        bot.Global_path_index = target;
                        // Pull-back (Nov 22): a routine 1-step undo of
                        // the stock advance happens every frame and is
                        // silent; a pull-back of 2+ is the runaway
                        // being corrected and is logged.
                        if (target < idx && idx - target >= 2)
                        {
                            Debug.Log("[GuidebotSteering] resync path index " + idx + " -> " + target +
                                " (pull-back: bot segment " + cur + " at path pos " + pos + ")");
                        }
                        else if (target > idx)
                        {
                            Debug.Log("[GuidebotSteering] resync path index " + idx + " -> " + target +
                                " (bot segment " + cur + " at path pos " + pos + ")");
                        }
                    }
                    return;
                }

                // (b) Bot's segment is NOT on the route (off-route, e.g.
                // seg 1470 in the ridge climb -- not on the route, not
                // portal-connected): fall back to the NEAREST route
                // segment by centre distance, and aim AT it (spawn-exit
                // fix, Nov 22: the old best+1 target skipped the
                // nearest route segment the bot has not entered -- the
                // same structural flaw the on-route branch had before
                // e3e42ff9. With best, the stock match fires as soon
                // as the bot enters Global_path[best] and the
                // destination is set). In the ridge band this lands on
                // seg 804 (the route segment after the ridge): the bot
                // at the climb waypoint is nearest to 43's centre, so
                // the index jumps to 804. Segment centres come from the
                // level data (GameManager.m_level_data.Segments[].Center,
                // the same source DoPathFollowPreDefined uses via
                // m_levelData) -- no crossings-table data is involved.
                LevelData ld = null;
                try { ld = GameManager.m_level_data; } catch (Exception) { }
                if (ld == null || ld.Segments == null)
                    return;
                int best = -1;
                float bestD2 = float.MaxValue;
                for (int i = 0; i < len; i++)
                {
                    int seg = bot.Global_path[i];
                    if (seg < 0 || seg >= ld.Segments.Length)
                        continue;
                    Vector3 c = ld.Segments[seg].Center;
                    float d2 = (botPos - c).sqrMagnitude;
                    if (d2 < bestD2) { bestD2 = d2; best = i; }
                }
                if (best < 0)
                    return;
                // Spawn-exit fix (Nov 22): target best, NOT best+1.
                int target2 = best;
                if (target2 > len - 1)
                    target2 = len - 1;
                if (target2 > idx)
                {
                    bot.Global_path_index = target2;
                    Debug.Log("[GuidebotSteering] resync path index " + idx + " -> " + target2 +
                        " (off-route seg " + cur + ", nearest route seg " + bot.Global_path[best] +
                        " at path pos " + best + ")");
                }
            }
            catch (Exception ex)
            {
                // Fail-open: a resync bug must never break pathing --
                // the stock destination is used unchanged.
                Debug.Log("[GuidebotSteering] resync failed: " + ex.ToString());
            }
        }

        // True when the bot is within kRidgeProximity of any waypoint
        // of the ridge entry (3D distance). The ridge waypoints ARE the
        // climb, so this is the location the z-band was standing in for.
        static bool IsNearRidge(RidgeEntry rg, Vector3 botPos)
        {
            if (rg.Waypoints == null || rg.Waypoints.Count == 0)
                return false;
            float d2 = kRidgeProximity * kRidgeProximity;
            foreach (Vector3 wp in rg.Waypoints)
            {
                if ((botPos - wp).sqrMagnitude <= d2)
                    return true;
            }
            return false;
        }

        static bool TryRidgeDestination(RidgeEntry rg, int fromSeg, int toSeg,
            Vector3 botPos, out Vector3 dest)
        {
            dest = Vector3.zero;
            if (rg.Waypoints == null || rg.Waypoints.Count == 0)
                return false;

            // Travel direction: from the leg if it matches a ridge leg
            // (forward or reverse); otherwise default to forward
            // (spawn->key). The return leg (key->exit) always has a valid
            // path, so the leg signal covers it.
            bool forward = true;
            if (fromSeg >= 0 && toSeg >= 0)
            {
                bool isForward = false, isReverse = false;
                foreach (var leg in rg.Legs)
                {
                    if (leg.From == fromSeg && leg.To == toSeg) isForward = true;
                    if (leg.From == toSeg && leg.To == fromSeg) isReverse = true;
                }
                if (isReverse && !isForward) forward = false;
            }

            // Route direction (horizontal) from first to last waypoint.
            Vector3 first = rg.Waypoints[0];
            Vector3 last = rg.Waypoints[rg.Waypoints.Count - 1];
            Vector3 dir = new Vector3(last.x - first.x, 0f, last.z - first.z);
            if (dir.sqrMagnitude < 1e-6f)
                return false;
            dir.Normalize();

            // Next unpassed waypoint in travel order. A waypoint is
            // passed when the bot is within reach of it or beyond it
            // along the route direction.
            for (int i = 0; i < rg.Waypoints.Count; i++)
            {
                int k = forward ? i : (rg.Waypoints.Count - 1 - i);
                Vector3 wp = rg.Waypoints[k];
                if (IsPassed(wp, botPos, dir, forward))
                    continue;
                dest = wp;
                // Forward guard: never aim behind the bot along the
                // route direction (prevents turning back / hovering).
                float ahead = Vector3.Dot(dest - botPos, dir) * (forward ? 1f : -1f);
                if (ahead <= 0f)
                {
                    float s = forward ? 1f : -1f;
                    dest = botPos + dir * (8f * s);
                    dest.y = rg.ClimbY;
                }
                return true;
            }
            // Past all waypoints: resume stock pathing.
            return false;
        }

        // Doorway waypoint sequence (spawn-exit fix, Nov 22): the same
        // pattern as TryRidgeDestination, over the doorway's waypoint
        // list (approach waypoints in from->to order, crossing last).
        // Forward (leg From->To) plays the list in order; reverse
        // (leg To->From) plays it reversed, so the crossing comes
        // first and the approach points trail behind it. No approach
        // waypoints = the original single-point doorway (the list is
        // just [crossing]). The forward guard pushes 8 m ahead along
        // the route direction at the waypoint's own height (the ridge
        // guard pushes at ClimbY; doorway waypoints carry their own
        // y). Returns false once the bot is past every waypoint, so
        // normal pathing resumes. `kind` names the waypoint for the
        // log: "doorway-approach" or "doorway-crossing".
        static bool TryDoorwayDestination(DoorwayEntry dw, bool forward,
            Vector3 botPos, out Vector3 dest, out string kind)
        {
            dest = Vector3.zero;
            kind = null;
            // Ordered waypoint list, from->to (approach, then crossing).
            var seq = new List<Vector3>(dw.Approach);
            seq.Add(dw.Crossing);
            if (seq.Count == 0)
                return false;

            // Route direction (horizontal) from first to last waypoint.
            Vector3 first = seq[0];
            Vector3 last = seq[seq.Count - 1];
            Vector3 dir = new Vector3(last.x - first.x, 0f, last.z - first.z);
            if (dir.sqrMagnitude < 1e-6f)
            {
                // Degenerate (single point, or approach == crossing):
                // aim at the crossing directly.
                dest = dw.Crossing;
                kind = "doorway-crossing";
                return true;
            }
            dir.Normalize();

            // Next unpassed waypoint in travel order.
            for (int i = 0; i < seq.Count; i++)
            {
                int k = forward ? i : (seq.Count - 1 - i);
                Vector3 wp = seq[k];
                if (IsPassed(wp, botPos, dir, forward))
                    continue;
                dest = wp;
                kind = (wp == dw.Crossing) ? "doorway-crossing" : "doorway-approach";
                // Forward guard: never aim behind the bot along the
                // route direction (prevents turning back / hovering).
                float ahead = Vector3.Dot(dest - botPos, dir) * (forward ? 1f : -1f);
                if (ahead <= 0f)
                {
                    float s = forward ? 1f : -1f;
                    dest = botPos + dir * (8f * s);
                    dest.y = wp.y;
                }
                return true;
            }
            // Past all waypoints: resume stock pathing.
            return false;
        }

        static bool IsPassed(Vector3 wp, Vector3 botPos, Vector3 dir, bool forward)
        {
            if (Vector3.Distance(botPos, wp) < kWaypointReach)
                return true;
            float along = Vector3.Dot(botPos - wp, dir);
            return forward ? (along > 0f) : (along < 0f);
        }

        static void SetDestination(Robot bot, Vector3 dest, string what)
        {
            bot.m_next_destination_position = dest;
            // Log only when the destination changes (one line per leg /
            // waypoint, not one per frame).
            string key = dest.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                dest.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                dest.z.ToString("F2", CultureInfo.InvariantCulture);
            if (s_lastDest != key)
            {
                s_lastDest = key;
                Debug.Log("[GuidebotSteering] overriding destination -> " +
                    dest.ToString() + " (" + what + ")");
            }
        }

        static CrossingsTable GetTable()
        {
            string level = null;
            try
            {
                if (GameplayManager.Level != null)
                    level = GameplayManager.Level.FileName;
            }
            catch (Exception) { }
            if (s_table != null && s_tableLevel == level)
                return s_table;
            if (s_tableFailed && s_tableLevel == level)
                return null;

            // (Re)load for this level.
            s_table = null;
            s_tableFailed = false;
            s_tableLevel = level;
            s_lastDest = null;
            string text = null;
            try { text = DataReader.GetGuidebotCrossingsData(); }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] loader failed: " + ex.ToString());
            }
            if (string.IsNullOrEmpty(text))
            {
                s_tableFailed = true;
                return null;
            }
            s_table = Parse(text);
            if (s_table == null)
            {
                s_tableFailed = true;
                Debug.Log("[GuidebotSteering] could not parse crossings table for level " + level);
                return null;
            }
            Debug.Log("[GuidebotSteering] loaded crossings table for level " + level +
                " (" + s_table.Doorways.Count + " doorway, " +
                s_table.Ridges.Count + " ridge)");
            return s_table;
        }

        static string[] Tokens(string line)
        {
            return line.Split(new char[] { ' ', '\t', ';' },
                StringSplitOptions.RemoveEmptyEntries);
        }

        static bool TryFloat(string s, out float v)
        {
            return float.TryParse(s, NumberStyles.Float,
                CultureInfo.InvariantCulture, out v);
        }

        static bool TryInt(string s, out int v)
        {
            return int.TryParse(s, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out v);
        }

        static CrossingsTable Parse(string text)
        {
            string[] lines = text.Split(new char[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                Debug.Log("[GuidebotSteering] bad crossings table (too few lines).");
                return null;
            }
            if (lines[0].Trim() != "GUIDEBOT_CROSSINGS")
            {
                Debug.Log("[GuidebotSteering] bad crossings header (expected GUIDEBOT_CROSSINGS).");
                return null;
            }
            int version;
            if (!TryInt(lines[1].Trim(), out version) || version != 1)
            {
                Debug.Log("[GuidebotSteering] bad crossings version (expected 1).");
                return null;
            }
            var table = new CrossingsTable();
            int i = 2;
            while (i < lines.Length)
            {
                string[] t = Tokens(lines[i].Trim());
                if (t.Length == 0) { i++; continue; }
                if (t[0] == "DOORWAY")
                {
                    // DOORWAY <from> <to> <cx> <cy> <cz> <near> <far>
                    //   [<n_appr> <ax> <ay> <az> ...]
                    // The optional approach suffix (spawn-exit fix,
                    // Nov 22) lists staging points on the FROM side,
                    // in from->to order. No suffix = the original
                    // single-point doorway (42<->807).
                    if (t.Length < 8)
                    {
                        Debug.Log("[GuidebotSteering] bad DOORWAY line: " + lines[i]);
                        return null;
                    }
                    var dw = new DoorwayEntry();
                    float cx, cy, cz;
                    if (!TryInt(t[1], out dw.From) || !TryInt(t[2], out dw.To) ||
                        !TryFloat(t[3], out cx) || !TryFloat(t[4], out cy) ||
                        !TryFloat(t[5], out cz))
                    {
                        Debug.Log("[GuidebotSteering] bad DOORWAY numbers: " + lines[i]);
                        return null;
                    }
                    dw.Crossing = new Vector3(cx, cy, cz);
                    if (t.Length >= 9)
                    {
                        int nAppr;
                        if (!TryInt(t[8], out nAppr) || nAppr < 0 ||
                            t.Length < 9 + 3 * nAppr)
                        {
                            Debug.Log("[GuidebotSteering] bad DOORWAY approach: " + lines[i]);
                            return null;
                        }
                        for (int k = 0; k < nAppr; k++)
                        {
                            float ax, ay, az;
                            if (!TryFloat(t[9 + 3 * k], out ax) ||
                                !TryFloat(t[10 + 3 * k], out ay) ||
                                !TryFloat(t[11 + 3 * k], out az))
                            {
                                Debug.Log("[GuidebotSteering] bad DOORWAY approach numbers: " + lines[i]);
                                return null;
                            }
                            dw.Approach.Add(new Vector3(ax, ay, az));
                        }
                    }
                    table.Doorways.Add(dw);
                    i++;
                }
                else if (t[0] == "RIDGE")
                {
                    // RIDGE <z_min> <z_max> <climb_y> <n_wp> <n_legs>
                    //       <leg_from> <leg_to> ...
                    if (t.Length < 6)
                    {
                        Debug.Log("[GuidebotSteering] bad RIDGE line: " + lines[i]);
                        return null;
                    }
                    var rg = new RidgeEntry();
                    int nWp, nLegs;
                    if (!TryFloat(t[1], out rg.ZMin) || !TryFloat(t[2], out rg.ZMax) ||
                        !TryFloat(t[3], out rg.ClimbY) ||
                        !TryInt(t[4], out nWp) || !TryInt(t[5], out nLegs))
                    {
                        Debug.Log("[GuidebotSteering] bad RIDGE numbers: " + lines[i]);
                        return null;
                    }
                    if (t.Length < 6 + 2 * nLegs)
                    {
                        Debug.Log("[GuidebotSteering] RIDGE line missing legs: " + lines[i]);
                        return null;
                    }
                    for (int l = 0; l < nLegs; l++)
                    {
                        int lf, lt;
                        if (!TryInt(t[6 + 2 * l], out lf) || !TryInt(t[7 + 2 * l], out lt))
                        {
                            Debug.Log("[GuidebotSteering] bad RIDGE leg: " + lines[i]);
                            return null;
                        }
                        rg.Legs.Add(new RidgeLeg { From = lf, To = lt });
                    }
                    i++;
                    for (int w = 0; w < nWp; w++, i++)
                    {
                        if (i >= lines.Length)
                        {
                            Debug.Log("[GuidebotSteering] RIDGE missing waypoint lines.");
                            return null;
                        }
                        string[] wt = Tokens(lines[i].Trim());
                        float wx, wy, wz;
                        if (wt.Length < 3 || !TryFloat(wt[0], out wx) ||
                            !TryFloat(wt[1], out wy) || !TryFloat(wt[2], out wz))
                        {
                            Debug.Log("[GuidebotSteering] bad RIDGE waypoint: " + lines[i]);
                            return null;
                        }
                        rg.Waypoints.Add(new Vector3(wx, wy, wz));
                    }
                    table.Ridges.Add(rg);
                }
                else
                {
                    Debug.Log("[GuidebotSteering] unknown crossings line: " + lines[i]);
                    return null;
                }
            }
            return table;
        }
    }

    // Route-resync fix, part 1 (Nov 22): stop the index reset at its
    // source. GuideBotLeadPlayer re-creates the path every 3 s via
    // Robot.CreatePathToSegment -> InitPath -> Global_path_index = 0
    // (decompile L92942 / L101898 / L101919 / L101676). This prefix
    // skips that re-creation when the GuideBot already has a valid path
    // to the SAME goal and is ON it -- the path is still good, so
    // rebuilding it only throws the accumulated index away. The caller
    // sees success (__result = true): GuideBotLeadPlayer keeps its goal
    // (and does not reset the destination to Global_path[0].Center in
    // a way that survives -- the DoPathfindMovement prefix overwrites
    // the destination the same frame), and FindSegmentContainingKey
    // still returns 1 for a key the bot is already routed to. When the
    // goal differs, the bot is off the path, or there is no path, the
    // original runs and the path is (re)built -- the reset is legitimate
    // then, and the resync re-syncs onto the fresh path. Fail-open: any
    // exception runs the original (stock behaviour). Only the GuideBot
    // is affected; every other robot returns true immediately.
    [HarmonyPatch(typeof(Robot), "CreatePathToSegment")]
    public class Robot_CreatePathToSegment_GuidebotSteering
    {
        // Set by the prefix on every call: true ONLY when the prefix
        // skipped the re-create (same goal, bot on path), false when
        // the original ran (or will run). Read by the Fix B postfix
        // (Robot_CreatePathToSegment_GuidebotSteering_Postfix, below):
        // the postfix must reset m_target_is_portal ONLY when a new
        // path was actually built. Single-threaded game loop, so no
        // race: prefix -> original-or-skip -> postfix, in that order,
        // within one call.
        internal static bool s_createPathSkipped = false;

        static bool Prefix(Robot __instance, int end_seg,
            out float actual_distance, ref bool __result)
        {
            // An out parameter must be assigned on every path: the
            // original overwrites it when it runs; 0 is the value the
            // caller sees when the original is skipped (callers ignore
            // it -- GuideBotLeadPlayer uses `out var _`).
            actual_distance = 0f;
            s_createPathSkipped = false;
            try { return PrefixInner(__instance, end_seg, ref __result); }
            catch (System.Exception ex)
            {
                Debug.Log("[GuidebotSteering] CreatePathToSegment prefix failed: " + ex.ToString());
                return true;
            }
        }

        static bool PrefixInner(Robot __instance, int end_seg, ref bool __result)
        {
            if (__instance == null || !__instance.m_is_guide_bot)
                return true;
            if (__instance.Global_path == null || __instance.Global_path_length <= 0)
                return true; // No path yet: build it.
            int len = __instance.Global_path_length;
            int goal = __instance.Global_path[len - 1];
            if (end_seg != goal)
            {
                Debug.Log("[GuidebotSteering] re-creating path to " + end_seg +
                    " (goal changed from " + goal + ") -- InitPath will reset index to 0");
                return true;
            }
            int cur = __instance.SegmentIndex;
            if (cur < 0)
                return true; // Bot outside the world: let the original handle it.
            int pos = -1;
            for (int i = 0; i < len; i++)
            {
                if (__instance.Global_path[i] == cur) { pos = i; break; }
            }
            if (pos < 0)
            {
                Debug.Log("[GuidebotSteering] re-creating path to " + end_seg +
                    " (bot seg " + cur + " off path) -- InitPath will reset index to 0");
                return true;
            }
            // Valid path to the same goal, bot on it: keep it. The index
            // is preserved (the resync already points it at/after the
            // bot's position); the caller sees success.
            __result = true;
            s_createPathSkipped = true;
            Debug.Log("[GuidebotSteering] kept path to " + end_seg +
                " (bot seg " + cur + " at path pos " + pos +
                ", index " + __instance.Global_path_index +
                " preserved) -- skipped CreatePathToSegment re-create (InitPath would reset index to 0)");
            return false;
        }
    }

    // Spawn-exit fix, part B (Nov 22): reset the stale
    // m_target_is_portal flag when a NEW path is built. InitPath
    // (decompile L101676) sets the destination to the bot's own
    // segment centre but never resets the flag (it is only set false
    // at L101706/L101723 and true at L101731, all inside
    // DoPathFollowPreDefined). If the flag was true from the previous
    // path -- the bot was aiming at a portal centre when the goal
    // flipped -- it stays true after the re-create: the bot aims at
    // its own segment centre *as a portal* and must get within 1 m of
    // it before the flag clears and the advance can fire. That is the
    // 216 stall (index never leaves 0 for 87 events in the e3e42ff9
    // flight). After InitPath the destination IS a segment centre, so
    // the flag is incoherent -- resetting it corrects an
    // inconsistency, it does not change intended behaviour.
    //
    // WHY THE GUARD (s_createPathSkipped): Harmony postfixes run EVEN
    // WHEN the original was skipped -- a prefix returning false skips
    // the original body only; postfixes (and finalizers) are not
    // affected (Harmony docs: "Postfixes and Finalizers are not
    // affected"). GuideBotLeadPlayer calls CreatePathToSegment every
    // FixedUpdate and our prefix skips the re-create most of the time,
    // so a bare postfix would clear the flag every frame -- including
    // while the bot is legitimately aiming at a portal centre
    // mid-path. That would defeat the stock 1 m portal-proximity
    // gate: DoPathFollowPreDefined would treat the portal destination
    // as a segment centre and advance the index (and the doorway leg)
    // before the bot has crossed the portal. So the postfix resets
    // ONLY when the prefix did NOT skip the re-create, via the flag
    // the prefix sets. When the prefix skips, accumulated state is
    // preserved. Fail-open: any exception (including the private
    // field ever moving) leaves the flag alone and logs.
    // GuideBot only.
    [HarmonyPatch(typeof(Robot), "CreatePathToSegment")]
    public class Robot_CreatePathToSegment_GuidebotSteering_Postfix
    {
        // Cached reflection handle for the private flag (decompile
        // L90080: private bool m_target_is_portal). No public accessor
        // exists, so reflection with a cached FieldInfo; resolved once.
        private static System.Reflection.FieldInfo s_portalFlag;
        private static bool s_portalFlagLookedUp;

        static void Postfix(Robot __instance)
        {
            try
            {
                if (__instance == null || !__instance.m_is_guide_bot)
                    return;
                if (Robot_CreatePathToSegment_GuidebotSteering.s_createPathSkipped)
                    return; // re-create skipped: preserve accumulated state
                if (!s_portalFlagLookedUp)
                {
                    s_portalFlagLookedUp = true;
                    s_portalFlag = typeof(Robot).GetField("m_target_is_portal",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    if (s_portalFlag == null)
                    {
                        Debug.Log("[GuidebotSteering] CreatePathToSegment postfix: " +
                            "m_target_is_portal field not found -- reset disabled");
                    }
                }
                if (s_portalFlag == null)
                    return;
                s_portalFlag.SetValue(__instance, false);
                Debug.Log("[GuidebotSteering] CreatePathToSegment postfix: " +
                    "m_target_is_portal reset (new path built, InitPath " +
                    "destination is the bot's own segment centre)");
            }
            catch (System.Exception ex)
            {
                Debug.Log("[GuidebotSteering] CreatePathToSegment postfix failed: " + ex.ToString());
            }
        }
    }

    // Route-resync fix, part 2 (Nov 22): persistent resync. Re-run
    // ResyncPathIndex AFTER the stock follower (DoPathFollowPreDefined)
    // runs, so a Global_path_index reset anywhere in the stock code --
    // CreatePathToSegment/InitPath, CreatePathToBelievedPlayerPosition,
    // GuidebotChooseWanderSegment -- cannot stick past the frame it
    // happened in. ResyncPathIndex pulls the index back to the bot's
    // position on-route (Nov 22 pull-back fix), so a stock advance
    // past the bot cannot stick past the frame it happened in either;
    // the off-route branch stays forward-only.
    // Fail-open: any exception is logged and the stock destination
    // stands. GuideBot only.
    [HarmonyPatch(typeof(Robot), "DoPathFollow")]
    public class Robot_DoPathFollow_GuidebotSteering
    {
        static void Postfix(Robot __instance)
        {
            try
            {
                if (__instance == null || !__instance.m_is_guide_bot)
                    return;
                Robot_DoPathfindMovement_GuidebotSteering.ResyncPathIndex(__instance);
            }
            catch (System.Exception ex)
            {
                Debug.Log("[GuidebotSteering] DoPathFollow postfix failed: " + ex.ToString());
            }
        }
    }

    // TO_GOAL HOLD -- TEST-FLIGHT SETTING, NOT A HARD RULE (Jason,
    // Nov 22, for the next test flight ONLY -- he explicitly said this
    // is NOT a hard rule, he is just talking it through, and the
    // follow behavior is to be revisited later).
    //
    // What it does: while the flag below is true, the GuideBot holds
    // TO_GOAL en route -- it goes for the key regardless of where the
    // player is -- so Fixes A-C can be judged on a clean run. The
    // stock flip (decompile GuideBotLeadPlayer L92889, TO_GOAL case)
    // fires when the player is > 30 m away (m_target_dist > 30f) or
    // invisible for > 0.7 s; this patch neutralises that flip
    // condition for ONE call at a time and restores the inputs
    // afterwards. The prefix presents the in-range + visible inputs
    // the stock code already uses to STAY on TO_GOAL
    // (m_target_dist = 0, target_visible = 2 -- both public fields,
    // saved first); the postfix restores the real values before
    // anything else reads them. The original GuideBotLeadPlayer body
    // always runs (the prefix returns true on every path) and the
    // TO_PLAYER path is NOT deleted: flip the flag to false (and
    // rebuild) and the stock TO_GOAL <-> TO_PLAYER flipping runs
    // unchanged, including following the player. Single-threaded game
    // loop: prefix -> original -> postfix, in that order, within one
    // call, so the static save slots cannot interleave. Fail-open:
    // any exception runs the original unmodified (and the postfix
    // restores the saved values, so a fake can never leak past the
    // call; m_target_dist / target_visible are recomputed every
    // FixedUpdate anyway).
    // GuideBot only.
    [HarmonyPatch(typeof(Robot), "GuideBotLeadPlayer")]
    public class Robot_GuideBotLeadPlayer_GuidebotSteering
    {
        // TEST-FLIGHT SETTING (Nov 22): true = hold TO_GOAL en route
        // for the next test flight; false = stock follow behavior.
        // This is a reversible test configuration, NOT a permanent
        // behavior change. Revisit with Jason before any later flight.
        private const bool kHoldGuidebotToGoal_TestFlight = true;

        // Save slots for the one-call input neutralisation (see above).
        private static float s_savedTargetDist;
        private static int s_savedTargetVisible;
        private static bool s_holdActive;
        private static int s_lastHoldGoal = -1;

        static bool Prefix(Robot __instance, int goal_seg)
        {
            if (!kHoldGuidebotToGoal_TestFlight || __instance == null ||
                !__instance.m_is_guide_bot)
                return true;
            try
            {
                s_savedTargetDist = __instance.m_target_dist;
                s_savedTargetVisible = __instance.target_visible;
                s_holdActive = true;
                __instance.m_target_dist = 0f;
                __instance.target_visible = 2;
                if (s_lastHoldGoal != goal_seg)
                {
                    s_lastHoldGoal = goal_seg;
                    Debug.Log("[GuidebotSteering] TO_GOAL hold (test flight): " +
                        "holding goal " + goal_seg +
                        ", TO_PLAYER flip suppressed -- reversible test " +
                        "setting, not a hard rule");
                }
                return true;
            }
            catch (System.Exception ex)
            {
                s_holdActive = false;
                Debug.Log("[GuidebotSteering] GuideBotLeadPlayer hold prefix failed: " + ex.ToString());
                return true;
            }
        }

        static void Postfix(Robot __instance)
        {
            if (!s_holdActive)
                return;
            s_holdActive = false;
            try
            {
                if (__instance == null)
                    return;
                __instance.m_target_dist = s_savedTargetDist;
                __instance.target_visible = s_savedTargetVisible;
            }
            catch (System.Exception ex)
            {
                Debug.Log("[GuidebotSteering] GuideBotLeadPlayer hold postfix failed: " + ex.ToString());
            }
        }
    }
}
