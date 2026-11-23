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
    //       forward-only. (Portal-aware since Nov 22, e5a796ff
    //       flight: while a portal destination is active the target
    //       is pos+1 -- see the portal-aware pull-back section
    //       below.)
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
    // Portal-aware pull-back (Nov 22) -- the e5a796ff flight: the bot
    // got to path pos 40/109 (seg 8, the tubes) -- the farthest any
    // steering build has got it -- then looped 8 -> 789 -> 790 -> 16 ->
    // 14/16 -> 790 -> 789 -> 8, entering seg 8 nine times and reversing
    // out every time, never entering 791. Ridge guard worked (0
    // overrides), the index pull-back worked (max gap 2, pinned to
    // pos), goal held 752 with zero flips. The stall is a NEW
    // mechanism, a steering-logic interaction introduced by the
    // pull-back itself: the pull-back pins Global_path_index to the
    // bot's own segment (pos), but the stock portal branch
    // (DoPathFollowPreDefined, decompile L101683-101753) and the
    // stock stuck-recovery (m_collided_stay_count > 50) both resolve
    // the destination via Global_path[Global_path_index] ASSUMING the
    // index points at the NEXT segment. When the bot reaches a portal
    // point, the destination becomes the bot's OWN segment centre --
    // 32.77 m BEHIND the 8->791 portal (seg 8's centre is (487.35,
    // 48.98, 553.61); the portal is at x=520.12) -- instead of 791's
    // centre, so the bot turns around. Fix: when m_target_is_portal
    // is true, the pull-back target is pos+1, not pos, preserving
    // the stock invariant while a portal destination is active. This
    // is safe against reintroducing the resync-deadlock: in the
    // portal case the destination is already set (to the portal
    // centre) before the resync runs, so setting the index to pos+1
    // does not leave the destination stale -- the bot keeps flying
    // to the portal centre, and on arrival the portal branch sets
    // the destination to Global_path[pos+1] centre (forward). When
    // the flag is false, the target stays pos (the deadlock lesson
    // still holds: with no portal destination active, pos+1 skips
    // the match and leaves the destination stale). The flag is
    // private on Robot (decompile L90080), so it is read via
    // reflection with a cached FieldInfo, the same pattern as the
    // Fix B postfix; any reflection failure fails open to the old
    // behaviour (target pos). Destination logging: the destination
    // was NOT in the e5a796ff log, so the mechanism had to be
    // inferred from the decompile + the segment trace + the
    // geometry. The DoPathFollow postfix now logs the destination
    // (bot position, m_next_destination_position, m_target_is_portal,
    // index) whenever it changes, so the next flight's log shows the
    // destination directly.
    //
    // Fixes A-D (Nov 23) -- the c0472d38 flight diagnosis
    // (docs/reports/guidebot-c0472d38-flight-diagnosis.md). Four
    // code-proven bugs, each fixed where it lives:
    //   (A) HoldPortalLine exits on every m_target_is_portal==true
    //       frame because ResyncPathIndex pins the index to pos+1
    //       first, so the hold reads the NEXT leg (cur != fromSeg)
    //       and returns early. Fix: ResyncPathIndex clears a STALE
    //       portal flag -- one whose active portal destination does
    //       not belong to the leg the bot is actually traversing --
    //       so the pin lands on pos and the hold can fire.
    //   (B) HoldPortalLine reads the leg from Global_path_index.
    //       Fix: it scans Global_path for the bot's segment (the
    //       same duplicate-aware scan ResyncPathIndex uses, shared
    //       as FindRoutePosition), and Case B pushes along the
    //       PORTAL NORMAL, not (toCentre - pc) -- the old direction
    //       pushed sideways into the jamb at 28->1706 (mostly up,
    //       barely past the plane). Crossings are hand-out geometry
    //       only: no waypoint is ever seeded at a crossing point in
    //       the route network. Single-point doorways parked the bot
    //       ON the plane and multi-waypoint doorways aborted 2.0 m
    //       early (kWaypointReach): the crossing now has its own
    //       tighter reach (kCrossingReach, 0.75 m) and, once the bot
    //       is within 1.5 m of it, the aim becomes crossing +
    //       portal-normal * 1.5 (push-through), in both the movement
    //       prefix and the Case A hold.
    //   (C) Outdoor aims under the terrain: the 1437 z=512 stall is
    //       a portal centre 3.78 m under the hill with no baked
    //       crossing. Fix: a general terrain-clearance lift -- when
    //       the destination sits in a terrain cube, a downward
    //       raycast lifts it to ground + 2.5 m (never down, capped
    //       at +20 m) -- plus off-route scoring by XZ distance when
    //       the bot is in a terrain cube, so a high cube is not
    //       skipped for a lower nearer-centre cube. The lift runs in
    //       the DoPathFollow postfix (after HoldPortalLine, before
    //       LogDestination), so it covers stock destinations too.
    //       New crossings are baked with a TERH-aware validator
    //       (the unsigned sweep alone cannot see "under the hill").
    //   (D) GuideBotLeadPlayer stomps the destination to the
    //       route-origin centre on kept-path skips (decompile
    //       GuideBotLeadPlayer TO_GOAL case: after CreatePathToSegment
    //       returns true it sets dest = Segments[Global_path[0]].
    //       Center -- including when our prefix skipped the
    //       re-create). Fix: the LeadPlayer patch saves the
    //       destination on entry and restores it in the postfix when
    //       the path was kept and the dest was stomped. Two recovery
    //       defects from the same diagnosis ride along:
    //       RepairStaleDestination now repairs at pos 0 too (the old
    //       pos > 0 guard stranded the bot aiming at its own centre),
    //       and GuidebotStuckReturn defers when the bot is actually
    //       moving (|v| > 1.0 m/s) instead of teleporting a bot that
    //       already freed itself.
    //   Matcen (Nov 23) press diagnostic: the stuck-watch line now
    //   names the goal source, the live steer target, and the
    //   nearest doorway / portal state, so a press names its cause.
    //   The Matcen rung numbers (6.7 / 6.42 / 5.36, depths 24/16/8/4,
    //   tight penalty 40) are D3 ship-scale references only -- our
    //   bot is a 0.60 m sphere, so no constant was copied; the
    //   crossings are validated at r=0.60 by sweep (+ TERH outdoors).
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

        // Glass-route fix (Fix list #5, Nov 22): a BLOCKED leg is a
        // route leg the GuideBot cannot fly -- solid glass -- with a
        // baked detour around it. The converter bakes one detour per
        // glass leg (15-40 segments, no glass legs inside it) and
        // emits one BLOCKED line per leg; the splice (below) swaps
        // the detour in for the leg at path-build time. Via holds
        // the detour's INTERIOR segments in A->B traversal order;
        // the full detour path is [A, Via..., B].
        private class BlockedLeg
        {
            public int A;
            public int B;
            public List<int> Via = new List<int>();
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
            public List<BlockedLeg> BlockedLegs = new List<BlockedLeg>();
        }

        // Cached table (per level). A failed load is remembered so the
        // prefix does not retry -- and log-spam -- every frame; the cache
        // is reset when the level changes.
        private static CrossingsTable s_table;
        private static string s_tableLevel;
        private static bool s_tableFailed;
        private static string s_lastDest;

        private const float kWaypointReach = 2.0f;

        // Fixes A-D (Nov 23), bug (2): a waypoint is "passed" at
        // kWaypointReach, so a multi-waypoint doorway aborted 2.0 m
        // early -- the crossing was "passed" before the bot crossed
        // the plane, the sequence ended, and the stock flip resumed.
        // The crossing itself needs a tighter reach (just past the
        // bot's 0.60 m radius): 0.75 m. Approach waypoints keep 2.0 m.
        private const float kCrossingReach = 0.75f;

        // Fixes A-D (Nov 23), bug (2): once the bot is this close to a
        // crossing, the aim becomes crossing + portal-normal * 1.5 --
        // a sustained push THROUGH the plane instead of parking on it
        // (single-point doorways) or handing back to stock early.
        private const float kPushThroughRange = 1.5f;
        private const float kPushThroughDistance = 1.5f;

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

        // Cached reflection handle for the private portal flag
        // (decompile L90080: private bool m_target_is_portal). No
        // public accessor exists, so reflection with a cached
        // FieldInfo, resolved once -- the same pattern as the Fix B
        // postfix (Robot_CreatePathToSegment_GuidebotSteering_Postfix).
        private static System.Reflection.FieldInfo s_portalFlag;
        private static bool s_portalFlagLookedUp;

        // Last destination-log key (change-only throttle for
        // LogDestination, below).
        private static string s_lastDestLog;

        // Portal-crossing fix (Nov 22), part 4 -- the 803->41 velocity
        // question. m_collided_robot_stay_count (decompile L89394)
        // counts frames in contact with another robot (layer 11);
        // m_collided_stay_count (L89396) counts frames in contact with
        // level geometry (layer 0/14). Both are private ints, incremented
        // by OnCollisionStay and reset after 5 contact-free frames
        // (L97847-97870). Cached FieldInfo, fail-open to -1 (unknown, so
        // it is distinguishable from 0), same pattern as the portal flag.
        private static System.Reflection.FieldInfo s_robotStayCount;
        private static System.Reflection.FieldInfo s_stayCount;
        private static bool s_stayCountsLookedUp;

        // Stuck-watch throttle (part 4): LogDestination logs on
        // destination change only, so a bot that is NOT moving but whose
        // destination is NOT changing (the hold re-asserts the same
        // point every frame) would go silent. When the bot is nearly
        // still (< 0.05 m/s) with a destination > 1 m away, log a watch
        // line at most once per 50 frames (~1 s) with velocity + stay
        // counts, so a silent stall still leaves evidence.
        private static int s_lastStuckWatchFrame = -1000000;

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
                    // Fixes A-D (Nov 23), bug (2): a single-point
                    // doorway parked the bot ON the portal plane -- the
                    // aim was the crossing itself, and the stock
                    // advance needs the bot through the plane, not on
                    // it. Within push-through range of the crossing,
                    // aim 1.5 m beyond the plane along the portal
                    // normal (the same point HoldPortalLine Case A
                    // asserts), so the bot gets a sustained push
                    // through instead of sitting on the crossing.
                    // Fail-open: no portal normal -> keep the crossing.
                    if (kind == "doorway-crossing" &&
                        Vector3.Distance(botPos, dest) < kPushThroughRange)
                    {
                        Vector3 normal;
                        if (GetPortalNormal(fromSeg, toSeg, out normal))
                        {
                            dest = dest + normal * kPushThroughDistance;
                            kind = "doorway-push-through";
                        }
                    }
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
        // re-derive the destination to the next route segment.
        // Portal-aware (Nov 22, e5a796ff flight): while the bot is
        // aiming at a portal centre (m_target_is_portal true) the
        // target is pos+1, not pos -- the stock portal branch and
        // stuck-recovery assume the index points at the NEXT segment,
        // and pinning it to pos made the destination the bot's own
        // segment centre (the tube loop at 8->791). The off-route
        // branch stays forward-only. One log line per actual
        // resync, so the next flight log shows whether the index
        // advances (a routine 1-step pull-back is silent; a pull-back
        // of 2+ is logged).
        // Read the stock portal flag (decompile L90080: private bool
        // m_target_is_portal). True = the bot is aiming at a portal
        // centre (the destination is a portal point, not a segment
        // centre). Any reflection failure fails open to false -- the
        // old pull-back behaviour (target pos) -- so a lookup problem
        // can never break pathing.
        internal static bool GetTargetIsPortal(Robot bot)
        {
            try
            {
                if (!s_portalFlagLookedUp)
                {
                    s_portalFlagLookedUp = true;
                    s_portalFlag = typeof(Robot).GetField("m_target_is_portal",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    if (s_portalFlag == null)
                    {
                        Debug.Log("[GuidebotSteering] m_target_is_portal field not found -- portal-aware pull-back disabled");
                    }
                }
                if (s_portalFlag == null)
                    return false;
                return (bool)s_portalFlag.GetValue(bot);
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] m_target_is_portal read failed: " + ex.ToString());
                return false;
            }
        }

        // Write the stock portal flag (decompile L90080: private bool
        // m_target_is_portal). Mirrors GetTargetIsPortal: the same
        // cached FieldInfo, the same fail-open (a lookup problem
        // leaves the flag alone and logs, so a reflection failure can
        // never break pathing). Used by the stale-destination repair
        // (RepairStaleDestination, below), which sets a segment centre
        // -- not a portal point -- as the destination and must clear
        // the flag, or the portal branch would resolve the next
        // destination to the bot's own segment centre (the tube loop).
        internal static void SetTargetIsPortal(Robot bot, bool value)
        {
            try
            {
                if (!s_portalFlagLookedUp)
                {
                    s_portalFlagLookedUp = true;
                    s_portalFlag = typeof(Robot).GetField("m_target_is_portal",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    if (s_portalFlag == null)
                    {
                        Debug.Log("[GuidebotSteering] m_target_is_portal field not found -- portal-flag write disabled");
                    }
                }
                if (s_portalFlag == null)
                    return;
                s_portalFlag.SetValue(bot, value);
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] m_target_is_portal write failed: " + ex.ToString());
            }
        }

        // Velocity + stay-count readers (portal-crossing fix, Nov 22,
        // part 4). c_rigidbody is public (decompile L89291); the two
        // stay counts are private (L89394/L89396) and read by
        // reflection, fail-open to -1 so "unknown" is distinguishable
        // from 0. The 803->41 flight stall showed velocity ~0 with no
        // layer 0/14 contact and no StuckReturn; these fields are what
        // the next flight's log needs to tell a robot-collision stall
        // (robot_stay climbing) from a force/damping stall (velocity
        // ~0, both counts 0).
        internal static Vector3 GetBotVelocity(Robot bot)
        {
            try
            {
                if (bot == null || bot.c_rigidbody == null)
                    return Vector3.zero;
                return bot.c_rigidbody.velocity;
            }
            catch (Exception) { return Vector3.zero; }
        }

        static void LookupStayCounts()
        {
            if (s_stayCountsLookedUp)
                return;
            s_stayCountsLookedUp = true;
            try
            {
                s_robotStayCount = typeof(Robot).GetField("m_collided_robot_stay_count",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                s_stayCount = typeof(Robot).GetField("m_collided_stay_count",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (s_robotStayCount == null || s_stayCount == null)
                {
                    Debug.Log("[GuidebotSteering] stay-count field(s) not found -- velocity logging degraded");
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] stay-count lookup failed: " + ex.ToString());
            }
        }

        static int ReadStayCount(Robot bot, System.Reflection.FieldInfo field)
        {
            try
            {
                if (field == null)
                    return -1;
                return (int)field.GetValue(bot);
            }
            catch (Exception) { return -1; }
        }

        internal static int GetCollidedRobotStayCount(Robot bot)
        {
            LookupStayCounts();
            return ReadStayCount(bot, s_robotStayCount);
        }

        internal static int GetCollidedStayCount(Robot bot)
        {
            LookupStayCounts();
            return ReadStayCount(bot, s_stayCount);
        }

        // True when the segment number is a real segment in the loaded
        // level. A failed level-data read fails closed (false): if the
        // level data is not there, the stock code cannot use the
        // segment either (FindGoodNearbySegment returns false on a null
        // level data), so treating it as invalid is consistent.
        internal static bool IsValidSegment(int seg)
        {
            try
            {
                LevelData ld = null;
                try { ld = GameManager.m_level_data; } catch (Exception) { }
                if (ld == null || ld.Segments == null)
                    return false;
                return seg >= 0 && seg < ld.Segments.Length;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // True when the segment is an outdoor-terrain cube: a 16 x 8 x
        // 16 m axis-aligned box. Verified on the L1 intermediate
        // (d3_sp_level01.overload): all 687 terrain_region segments
        // are exactly (16.0, 8.0, 16.0) and no other segment matches
        // -- the AABB test is exact for this level, with no
        // false positives. Uses the segment's AABB corners
        // (SegmentData.MinCornerPos / MaxCornerPos), the same data
        // the game uses; no crossings-table or intermediate data is
        // involved, and no segment number is hardcoded. The GuideBot's
        // segment lookup is unreliable in these big open cubes
        // (SegmentIndex can report -1 -- the 7481ae07 flight's
        // deactivation threw with the bot in the terrain), and the
        // wander BFS through them is meaningless, so the wander guard
        // fails open here. A failed level-data read fails open to
        // false (not a terrain cube): the guard then relies on the
        // invalid-segment check alone.
        internal static bool IsTerrainCube(int seg)
        {
            try
            {
                LevelData ld = null;
                try { ld = GameManager.m_level_data; } catch (Exception) { }
                if (ld == null || ld.Segments == null)
                    return false;
                if (seg < 0 || seg >= ld.Segments.Length)
                    return false;
                Vector3 size = ld.Segments[seg].MaxCornerPos - ld.Segments[seg].MinCornerPos;
                return size.x >= 15f && size.x <= 17f &&
                       size.y >= 7f && size.y <= 9f &&
                       size.z >= 15f && size.z <= 17f;
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] terrain-cube check failed: " + ex.ToString());
                return false;
            }
        }

        // Terrain destination re-resolution (Nov 22, 7481ae07 flight).
        // The trigger: after a kept-path skip, the destination can be
        // the ROUTE-ORIGIN segment's centre (Global_path[0]).
        // GuideBotLeadPlayer's TO_GOAL case (decompile L92889) sets
        // m_next_destination_position =
        // Segments[Global_path[0]].Center after ANY successful
        // CreatePathToSegment -- including our kept-path skip, where
        // the prefix returns __result = true without rebuilding the
        // path, so InitPath never re-runs and the destination is never
        // re-derived. In the corridors the stock follower's portal
        // branch re-resolves it every frame (the bot is within 1 m of
        // a portal point); in the big open 16 m terrain cubes it is
        // never near a portal point, so the stale value persists and
        // the bot flies backward to the origin (7481ae07: seg 26's
        // centre, 55 m behind the bot at path pos 10, loop
        // 1437 -> 1442 -> 1381 -> 1387). The repair: if the
        // destination is the centre of any route segment BEHIND the
        // bot (Global_path[i], i < pos), re-resolve it forward to the
        // next route segment's centre (Global_path[pos+1]) and clear
        // the portal flag (a segment centre, not a portal point).
        // General: any route origin, any open area -- not hardcoded
        // to seg 26 or the terrain. The stock follower's next branch-6
        // match re-derives the destination forward from here, so the
        // repair only has to break the stale-origin lock for one
        // frame. Guarded to a 0.5 m match (the
        // destination is a point the stock code set, so an exact
        // centre match is expected; the tolerance covers float
        // rounding, not a different point). A portal centre that
        // merely coincides with an earlier segment centre is repaired
        // the same way -- the forward destination is what the stock
        // follower would set on the next match anyway. Fail-open: a
        // repair bug leaves the stock destination unchanged.
        // Recovery defect (a) (Nov 23, c0472d38 flight): the old
        // pos > 0 guard left the bot stranded at pos 0 aiming at its
        // own segment centre (the 28->1706 stall began with a stale
        // dest at pos 0). At the origin the destination IS
        // legitimately the origin centre -- but only while the bot is
        // near it. When the bot is more than 2.0 m from its own
        // centre, the destination is stale (a kept-path skip stomped
        // it there, or InitPath set it and the follower never
        // re-derived it): resolve forward to the next route segment's
        // centre. Within 2.0 m the stock follower owns it -- the
        // repair leaves it alone.
        // Internal (not private): ResyncPathIndex calls it from the
        // on-route branch, after the index pin.
        internal static void RepairStaleDestination(Robot bot, int pos, int len)
        {
            try
            {
                if (pos < 0)
                    return;
                LevelData ld = null;
                try { ld = GameManager.m_level_data; } catch (Exception) { }
                if (ld == null || ld.Segments == null)
                    return;
                Vector3 dest = bot.m_next_destination_position;
                Vector3 botPos = bot.transform.position;
                int behind = -1;
                for (int i = 0; i < pos; i++)
                {
                    int seg = bot.Global_path[i];
                    if (seg < 0 || seg >= ld.Segments.Length)
                        continue;
                    if ((dest - ld.Segments[seg].Center).sqrMagnitude <= 0.25f)
                    {
                        behind = i;
                        break;
                    }
                }
                if (behind < 0 && pos == 0 && len > 1)
                {
                    // Recovery defect (a): pos 0, destination is the
                    // bot's own segment centre, bot far from it.
                    int origin = bot.Global_path[0];
                    if (origin >= 0 && origin < ld.Segments.Length &&
                        (dest - ld.Segments[origin].Center).sqrMagnitude <= 0.25f &&
                        Vector3.Distance(botPos, dest) > 2.0f)
                    {
                        behind = 0;
                    }
                }
                if (behind < 0)
                    return; // destination is forward (or a portal point): leave it alone
                int next = pos + 1;
                if (next > len - 1)
                    next = len - 1;
                int nextSeg = bot.Global_path[next];
                if (nextSeg < 0 || nextSeg >= ld.Segments.Length)
                    return;
                bot.m_next_destination_position = ld.Segments[nextSeg].Center;
                SetTargetIsPortal(bot, false); // a segment centre, not a portal point
                Debug.Log("[GuidebotSteering] stale-origin destination repaired: dest was route seg " +
                    bot.Global_path[behind] + " centre (path pos " + behind + ", behind bot at pos " + pos +
                    "), re-resolved to seg " + nextSeg + " centre (path pos " + next + ")");
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] stale-destination repair failed: " + ex.ToString());
            }
        }

        // Duplicate-aware route-position scan (glass-route fix, Nov 22):
        // a spliced detour can backtrack through a segment the bot
        // already passed, so the path can contain the bot's segment
        // TWICE. Search FORWARD from the current index first (the
        // bot's second pass through the segment), then BACKWARD (the
        // index ran ahead of the bot -- the pull-back case) if not
        // found forward. Shared by ResyncPathIndex and HoldPortalLine
        // (Fix B, Nov 23): both must read the leg the bot is actually
        // traversing, not the leg the index happens to point at.
        // Returns the path position of `cur`, or -1 when the bot's
        // segment is not on the route at all.
        internal static int FindRoutePosition(Robot bot, int cur, int startIdx)
        {
            int len = bot.Global_path_length;
            int fwd = startIdx < 0 ? 0 : startIdx;
            for (int i = fwd; i < len; i++)
            {
                if (bot.Global_path[i] == cur) { return i; }
            }
            // Clamp: startIdx can sit past the end (stock advance);
            // the backward scan must stay inside the path.
            int back = startIdx - 1;
            if (back > len - 1)
                back = len - 1;
            for (int i = back; i >= 0; i--)
            {
                if (bot.Global_path[i] == cur) { return i; }
            }
            return -1;
        }

        // Fix A (Nov 23) -- clear a stale m_target_is_portal. The
        // portal-aware pull-back pins the index to pos+1 while the
        // flag is true, on the assumption that the active portal
        // destination belongs to the leg the bot is traversing. When
        // the flag survives a goal flip / path rebuild / index
        // runaway, the destination is a portal (or next-centre) point
        // from some OTHER leg, and the pin puts the index one leg
        // ahead -- so HoldPortalLine reads the next leg, finds
        // cur != fromSeg, and exits on every portal-aim frame (the
        // 1442->1437 and 42->807 holds fired 0x in the c0472d38
        // flight). The flag belongs to the traversing leg only when
        // the destination IS that leg's portal centre or its next
        // segment's centre (within 0.5 m, the same tolerance the hold
        // uses); otherwise it is stale and is cleared, so the pin
        // lands on pos and the hold can fire. Fail-open: when the
        // portal centre cannot be computed, the flag is left alone.
        // Internal (not private): ResyncPathIndex calls it.
        internal static void ClearStalePortalFlag(Robot bot, int pos)
        {
            try
            {
                if (!GetTargetIsPortal(bot))
                    return;
                int len = bot.Global_path_length;
                Vector3 dest = bot.m_next_destination_position;
                // No next leg: there is no portal destination to hold.
                if (pos + 1 >= len)
                {
                    SetTargetIsPortal(bot, false);
                    Debug.Log("[GuidebotSteering] stale portal flag cleared: bot at path pos " + pos + " (last segment), no next leg");
                    return;
                }
                int fromSeg = bot.Global_path[pos];
                int toSeg = bot.Global_path[pos + 1];
                Vector3 pc;
                if (!GetPortalCentre(bot, fromSeg, toSeg, out pc))
                    return; // cannot verify -- fail-open, leave the flag
                bool atPortal = Vector3.Distance(dest, pc) < 0.5f;
                bool atNext = false;
                Vector3 toCentre;
                if (TryGetSegmentCentre(toSeg, out toCentre))
                    atNext = Vector3.Distance(dest, toCentre) < 0.5f;
                if (atPortal || atNext)
                    return; // belongs to the traversing leg -- keep the flag
                SetTargetIsPortal(bot, false);
                Debug.Log("[GuidebotSteering] stale portal flag cleared: dest " + dest.ToString() +
                    " is not the " + fromSeg + "->" + toSeg + " portal/next centre (bot at path pos " + pos + ")");
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] stale portal flag check failed: " + ex.ToString());
            }
        }

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
                // the bot's position, not past it -- UNLESS the bot
                // is aiming at a portal centre (portal-aware
                // pull-back, Nov 22, e5a796ff flight: see the
                // target line below). The stock
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
                // destination to the next route segment -- except
                // while a portal destination is active, when the
                // index must point at the NEXT segment (pos+1), or
                // the portal branch and the stuck-recovery resolve
                // the destination to the bot's own segment centre
                // (the tube loop: nine reversals at the 8->791
                // portal).
                int pos = -1;
                // Duplicate-aware scan (glass-route fix, Nov 22) --
                // shared as FindRoutePosition (Fix B uses it too).
                pos = FindRoutePosition(bot, cur, idx);
                if (pos >= 0)
                {
                    // Fix A (Nov 23): clear a stale portal flag BEFORE
                    // the pin below decides its target -- a stale flag
                    // would pin the index to pos+1 and HoldPortalLine
                    // would exit on every portal-aim frame.
                    ClearStalePortalFlag(bot, pos);
                    // Portal-aware pull-back (Nov 22, e5a796ff
                    // flight): when the bot is aiming at a portal
                    // centre (m_target_is_portal true), the target
                    // is pos+1, not pos. The stock portal branch
                    // and the stock stuck-recovery resolve the
                    // destination via Global_path[Global_path_index]
                    // assuming the index points at the NEXT segment;
                    // pinning it to pos while a portal destination
                    // is active makes the destination the bot's OWN
                    // segment centre -- the tube loop (the bot turned
                    // around at the 8->791 portal, 32.77 m short of
                    // the portal, nine times). With the flag true
                    // the destination is already set (to the portal
                    // centre) before the resync runs, so pos+1 does
                    // not leave the destination stale (no
                    // resync-deadlock): the bot keeps flying to the
                    // portal centre, and on arrival the portal
                    // branch sets the destination to
                    // Global_path[pos+1] centre (forward). When the
                    // flag is false the target stays pos -- the
                    // deadlock lesson still holds there.
                    int target = GetTargetIsPortal(bot) ? pos + 1 : pos;
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
                    // Terrain destination re-resolution (Nov 22,
                    // 7481ae07 flight): after a kept-path skip the
                    // destination can be the route-origin segment's
                    // centre (Global_path[0]) and the stock follower
                    // never re-resolves it in open terrain, so the
                    // bot flies backward to the origin. Repair it
                    // forward to the next route segment's centre.
                    // Runs after the pin, so it sees the resynced
                    // index; the DoPathFollow postfix calls this
                    // after the stock follower, so LogDestination
                    // records the repaired destination.
                    RepairStaleDestination(bot, pos, len);
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
                // Fix C (Nov 23): when the bot is off-route in a
                // terrain cube, score route segments by HORIZONTAL
                // (XZ) distance, not 3D. Terrain-cube centres sit low
                // (y~36-40) while the bot flies high (y~50+); 3D
                // distance picks a lower nearer-centre cube over the
                // cube the bot is actually above, and the resync
                // pulls it down. XZ scoring asks "where am I on the
                // route", and the terrain lift (LiftDestinationFor-
                // Terrain) handles the height. Indoors the 3D score
                // stands (segment centres are the route there).
                bool botInTerrain = IsTerrainCube(cur);
                for (int i = 0; i < len; i++)
                {
                    int seg = bot.Global_path[i];
                    if (seg < 0 || seg >= ld.Segments.Length)
                        continue;
                    Vector3 c = ld.Segments[seg].Center;
                    float d2;
                    if (botInTerrain)
                    {
                        float dx = botPos.x - c.x;
                        float dz = botPos.z - c.z;
                        d2 = dx * dx + dz * dz;
                    }
                    else
                    {
                        d2 = (botPos - c).sqrMagnitude;
                    }
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

        // Press diagnostic (Matcen, Nov 23): while a bot presses (>1 s
        // at near-zero speed under thrust), D3 logs the goal source,
        // the live steer target, and the nearest locked door in the
        // room -- so an observed press names its cause. The stuck-watch
        // IS our press detector (nearly still, destination > 1 m away,
        // throttled to ~1 s). This names the same three things with
        // the data we have: the goal source (AI_guidebot_submode +
        // the path goal), the live steer target (the dest + portal
        // flag + index, already on the line -- here the traversing leg
        // and its portal state), and the nearest doorway entry (we
        // have no lock concept in GameMod -- the ring-door lock is a
        // separate job -- so the nearest baked doorway crossing is
        // the closest equivalent: a press AT a doorway names the
        // doorway, a press far from any doorway names geometry).
        // Fail-open: any lookup failure yields "?" fields, never a
        // throw (this runs inside the logging path).
        static string PressDiagnostic(Robot bot, Vector3 dest)
        {
            try
            {
                string goal = "?";
                try { goal = Robot.AI_guidebot_submode.ToString(); } catch (Exception) { }
                int pathGoal = -1;
                try
                {
                    if (bot.Global_path != null && bot.Global_path_length > 0)
                        pathGoal = bot.Global_path[bot.Global_path_length - 1];
                }
                catch (Exception) { }
                int cur = bot.m_current_segment;
                int idx = bot.Global_path_index;
                string leg = "off-route";
                string portalState = "n/a";
                try
                {
                    int pos = FindRoutePosition(bot, cur, idx);
                    if (pos >= 0 && pos + 1 < bot.Global_path_length)
                    {
                        int fromSeg = bot.Global_path[pos];
                        int toSeg = bot.Global_path[pos + 1];
                        leg = fromSeg + "->" + toSeg;
                        Vector3 pc;
                        if (GetPortalCentre(bot, fromSeg, toSeg, out pc))
                        {
                            portalState = Vector3.Distance(bot.transform.position, pc).ToString("F1", CultureInfo.InvariantCulture) + "m from portal";
                        }
                    }
                }
                catch (Exception) { }
                string doorway = "none";
                try
                {
                    CrossingsTable table = GetTable();
                    if (table != null && table.Doorways.Count > 0)
                    {
                        float best = float.MaxValue;
                        DoorwayEntry bestDw = null;
                        foreach (DoorwayEntry e in table.Doorways)
                        {
                            float d = Vector3.Distance(bot.transform.position, e.Crossing);
                            if (d < best) { best = d; bestDw = e; }
                        }
                        if (bestDw != null)
                        {
                            doorway = bestDw.From + "->" + bestDw.To + " " +
                                best.ToString("F1", CultureInfo.InvariantCulture) + "m";
                        }
                    }
                }
                catch (Exception) { }
                return "press goal=" + goal + "/" + pathGoal + " leg=" + leg +
                    " nearest-doorway=" + doorway + " portal=" + portalState;
            }
            catch (Exception)
            {
                return "press goal=? leg=? nearest-doorway=? portal=?";
            }
        }

        // Log the destination the bot is actually flying toward
        // (Nov 22, e5a796ff flight): the destination was NOT in the
        // log, so the tube-loop mechanism had to be inferred from the
        // decompile + the segment trace + the geometry. This logs the
        // bot's position, its destination (m_next_destination_position),
        // m_target_is_portal, and the index -- whenever any of them
        // changes (change-only throttle, like SetDestination: a steady
        // flight toward a fixed destination is one line, a flip
        // between two destinations is a line per flip). Called from
        // the DoPathFollow postfix (Robot_DoPathFollow_GuidebotSteering),
        // after ResyncPathIndex, so it records the destination the
        // stock follower set for the next frame, with the resynced
        // index. The bot's position is printed for context but is NOT
        // part of the change key (it moves every frame).
        // Internal (not private): the DoPathFollow postfix calls it.
        internal static void LogDestination(Robot bot)
        {
            try
            {
                Vector3 dest = bot.m_next_destination_position;
                bool portal = GetTargetIsPortal(bot);
                int idx = bot.Global_path_index;
                int cur = bot.m_current_segment;
                string key = dest.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                    dest.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                    dest.z.ToString("F2", CultureInfo.InvariantCulture) + "|" +
                    (portal ? "portal" : "seg") + "|" + idx + "|" + cur;
                if (s_lastDestLog == key)
                {
                    // Stuck watch (portal-crossing fix, part 4): the
                    // destination did not change, so no dest line is
                    // due -- but if the bot is nearly still with a
                    // destination > 1 m away, it is stalled on a
                    // constant aim (the hold re-asserting the same
                    // point), and the log would otherwise go silent.
                    // Log at most once per 50 frames (~1 s).
                    Vector3 vel = GetBotVelocity(bot);
                    if (vel.sqrMagnitude < 0.05f * 0.05f &&
                        Vector3.Distance(dest, bot.transform.position) > 1.0f &&
                        Time.frameCount - s_lastStuckWatchFrame >= 50)
                    {
                        s_lastStuckWatchFrame = Time.frameCount;
                        int robotStay = GetCollidedRobotStayCount(bot);
                        int stay = GetCollidedStayCount(bot);
                        Debug.Log("[GuidebotSteering] stuck-watch dest " + dest.ToString() +
                            " (portal=" + portal + ", index=" + idx +
                            ", seg=" + cur + ") from " + bot.transform.position.ToString() +
                            " vel " + vel.ToString() + " |v|=" +
                            vel.magnitude.ToString("F2", CultureInfo.InvariantCulture) +
                            " robot_stay=" + robotStay + " stay=" + stay +
                            " " + PressDiagnostic(bot, dest));
                    }
                    return;
                }
                s_lastDestLog = key;
                Vector3 vel2 = GetBotVelocity(bot);
                int robotStay2 = GetCollidedRobotStayCount(bot);
                int stay2 = GetCollidedStayCount(bot);
                Debug.Log("[GuidebotSteering] dest " + dest.ToString() +
                    " (portal=" + portal + ", index=" + idx +
                    ", seg=" + cur + ") from " + bot.transform.position.ToString() +
                    " vel " + vel2.ToString() + " |v|=" +
                    vel2.magnitude.ToString("F2", CultureInfo.InvariantCulture) +
                    " robot_stay=" + robotStay2 + " stay=" + stay2);
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] dest log failed: " + ex.ToString());
            }
        }

        // PORTAL-CROSSING FIX (Nov 22): break the portal-centre <->
        // next-segment-centre limit cycle (E2D820BC flight diagnosis).
        //
        // The stock cycle, decoded from the decompile: DoPathFollowPreDefined
        // (L101683) branch 6 fires when the bot's segment == Global_path[index]
        // and aims at the portal centre (GetTargetPointFromPortal, L101753)
        // when the next centre is not visible, flag=true; when the bot comes
        // within 1.0 m of the portal centre it re-aims at Global_path[index]'s
        // centre and clears the flag; MaybeAdvanceSegment (L101781) advances
        // the index every 0.2 s when Global_path[index+1] is merely visible
        // and sets dest to that segment's centre. The resync (above) pins the
        // index back, so the destination flips portal centre <-> next centre
        // every frame and the bot never moves -- and because the two aim
        // points are ~59 degrees apart at 803->41, the constant-magnitude
        // AddForce (DoPathfindMovement, L91810) never builds velocity in
        // either direction. All four E2D820BC stalls share this signature;
        // the blocker differs per stall (buried centre, doorway jamb,
        // glass), the cycle is common.
        //
        // This runs in the DoPathFollow postfix, after ResyncPathIndex and
        // before LogDestination, so the log records the overridden
        // destination. DoPathfindMovement runs before DoPathFollow each
        // FixedUpdate, so the override set here is the destination the
        // NEXT movement frame flies toward -- the stock re-aim still fires
        // inside DoPathFollow, but its destination never survives to a
        // movement frame. Three cases, all fail-open (any missing data
        // leaves the stock destination alone):
        //   A. Doorway hold: the traversing leg is a crossings-table
        //      DOORWAY entry (1442->1437, 1706->1707, 42->807, 217->213)
        //      and the stock destination is that leg's portal centre or
        //      its next segment's centre -- hold the baked crossing
        //      point until the bot's segment changes. The portal centre
        //      is buried (1442->1437) or jamb-blocked (1706->1707); the
        //      next centre is the same buried point. Does NOT fight the
        //      movement prefix: while the prefix is staging an approach
        //      waypoint its destination is the approach/crossing, never
        //      the portal/next centre, so this case only fires once the
        //      prefix has handed control back.
        //   B. Cycle-break (non-doorway legs): the destination is the
        //      next segment's centre, the portal flag is false (the
        //      <1.0 m re-aim just cleared it), and the bot is within
        //      1.0 m of the portal centre -- replace the re-aim target
        //      with a point 1.5 m BEYOND the portal plane along the
        //      route (portal centre + normalised next-centre-minus-
        //      portal-centre * 1.5), re-asserted every frame until the
        //      segment changes. The bot gets a sustained push through
        //      the plane instead of oscillating. (803->41, and the
        //      glass-stall signature -- the glass itself is fix #5's
        //      scope; this only breaks the cycle, it does not make the
        //      bot pass the pane.)
        //   C. Far-centre cycle-break (any leg, any level): the
        //      destination is a route segment at pos+2 or beyond (a
        //      visibility advance the bot has not earned by crossing
        //      the current leg's portal) -- redirect it to the
        //      push-through point (the current leg's portal centre,
        //      or its baked crossing on a doorway leg, pushed 1.5 m
        //      beyond the plane along the portal normal), so the bot
        //      stays on the portal line until it is through instead
        //      of being pulled to a far segment's centre 14 m away
        //      (1707: 1322's centre). Generalised Nov 23: the old
        //      clamp only fired within 3.0 m of a
        //      baked doorway crossing, so a far-centre destination on
        //      a plain leg flipped against the portal-centre re-aim
        //      every steering cycle and the bot coasted to a stop in
        //      open space 13.4 m from the portal. The gate is now the
        //      destination itself, not the doorway table, and the
        //      target is the push-through point, not the portal
        //      centre -- the portal centre and the push-through point
        //      are ~1.5 m apart, so the force builds in one
        //      direction where the far centre and the portal centre
        //      were far apart and cancelled.
        internal static void HoldPortalLine(Robot bot)
        {
            try
            {
                if (bot.Global_path == null || bot.Global_path_length <= 0)
                    return;
                int len = bot.Global_path_length;
                int idx = bot.Global_path_index;
                int cur = bot.m_current_segment;
                if (cur < 0 || idx < 0 || idx >= len)
                    return;
                // Fix B (Nov 23): the leg is the leg the bot is
                // TRAVERSING -- its own route position to the next
                // route segment -- read by scanning Global_path for
                // the bot's segment (FindRoutePosition), NOT from
                // Global_path_index. The old index read plus the
                // cur != fromSeg early return meant the hold exited
                // on every frame where ResyncPathIndex had pinned the
                // index to pos+1 (every m_target_is_portal==true
                // frame): the 1442->1437 and 42->807 holds fired 0x
                // in the c0472d38 flight. The scan is duplicate-aware
                // (same as the resync), so a spliced detour's second
                // pass through a segment reads the right leg.
                int pos = FindRoutePosition(bot, cur, idx);
                if (pos < 0 || pos + 1 >= len)
                    return;
                int fromSeg = bot.Global_path[pos];
                int toSeg = bot.Global_path[pos + 1];
                Vector3 botPos = bot.transform.position;
                Vector3 dest = bot.m_next_destination_position;
                Vector3 pc;
                if (!GetPortalCentre(bot, fromSeg, toSeg, out pc))
                    return;
                CrossingsTable table = GetTable();
                DoorwayEntry dw = null;
                if (table != null)
                {
                    foreach (DoorwayEntry e in table.Doorways)
                    {
                        if ((e.From == fromSeg && e.To == toSeg) ||
                            (e.From == toSeg && e.To == fromSeg))
                        { dw = e; break; }
                    }
                }
                Vector3 toCentre;
                bool haveToCentre = TryGetSegmentCentre(toSeg, out toCentre);
                // Portal normal (Fix B): the direction through the
                // plane toward the next segment, from the shared
                // portal quad. Fail-open to "unknown" -- the cases
                // below fall back to their old behaviour without it.
                Vector3 portalNormal;
                bool haveNormal = GetPortalNormal(fromSeg, toSeg, out portalNormal);
                // Case A -- doorway hold.
                if (dw != null)
                {
                    bool atPortal = Vector3.Distance(dest, pc) < 0.5f;
                    bool atNext = haveToCentre &&
                        Vector3.Distance(dest, toCentre) < 0.5f;
                    if (atPortal || atNext)
                    {
                        // Fix B, bug (2): once the bot is within
                        // push-through range of the crossing, aim
                        // 1.5 m beyond the plane along the portal
                        // normal instead of parking on the crossing.
                        // (The movement prefix does the same when it
                        // stages the crossing; this covers the frames
                        // where the prefix has handed control back.)
                        if (haveNormal &&
                            Vector3.Distance(botPos, dw.Crossing) < kPushThroughRange)
                        {
                            SetDestination(bot, dw.Crossing + portalNormal * kPushThroughDistance,
                                "portal-crossing doorway-push-through " + fromSeg + "->" + toSeg);
                        }
                        else
                        {
                            SetDestination(bot, dw.Crossing,
                                "portal-crossing doorway-hold " + fromSeg + "->" + toSeg);
                        }
                        return;
                    }
                    // Else fall through to case C (MaybeAdvance clamp).
                }
                else
                {
                    // Case B -- cycle-break (non-doorway legs only).
                    if (haveToCentre &&
                        Vector3.Distance(dest, toCentre) < 0.5f &&
                        !GetTargetIsPortal(bot) &&
                        Vector3.Distance(botPos, pc) < 1.0f)
                    {
                        // Fix B, bug (3): push along the PORTAL
                        // NORMAL, not (toCentre - pc). The old
                        // direction is the line from the portal
                        // centre to the next segment's centre, which
                        // at 28->1706 pointed mostly UP (+1.41 m in
                        // y, only -0.51 m in z) -- sideways into the
                        // jamb, barely 0.1 m past the plane -- while
                        // Case C pulled down to the portal centre and
                        // the stock dest pulled at 1706's centre: a
                        // three-way limit cycle. The normal is the
                        // direction through the plane. Fall back to
                        // the old direction only when the quad cannot
                        // be read.
                        Vector3 dir;
                        if (haveNormal)
                        {
                            dir = portalNormal;
                        }
                        else
                        {
                            dir = toCentre - pc;
                            if (dir.sqrMagnitude < 1e-6f)
                                dir = toCentre - botPos;
                            if (dir.sqrMagnitude < 1e-6f)
                                return;
                            dir.Normalize();
                        }
                        SetDestination(bot, pc + dir * 1.5f,
                            "portal-crossing " + fromSeg + "->" + toSeg);
                        return;
                    }
                }
                // Case C -- far-centre cycle-break (any leg, any
                // level). Generalised Nov 23: the old
                // clamp only fired within 3.0 m of a baked doorway
                // crossing, so a far-centre destination on a plain leg
                // flipped against the portal-centre re-aim every
                // steering cycle and the constant-magnitude AddForce
                // cancelled -- the bot coasted to a stop in OPEN SPACE
                // 13.4 m from the portal (492 stuck-watch lines, zero
                // recovery: the stock reset never fired, stay peaked
                // at 23). The gate is now the destination itself, not
                // the doorway table: when the bot is still in the
                // traversing leg's fromSeg and its destination is the
                // centre of a route segment at pos+2 or beyond (a
                // visibility advance the bot has not earned by crossing
                // the current leg's portal -- MaybeAdvanceSegment
                // advances the index on mere visibility), redirect it
                // to the push-through point the other cases already
                // use (the current leg's portal centre, or its baked
                // crossing on a doorway leg, pushed 1.5 m beyond the
                // plane along the portal normal). The two aims the
                // cycle alternated between (portal centre, far centre)
                // are far apart, so the force cancelled; the portal
                // centre and the push-through point are ~1.5 m apart,
                // so the force builds in one direction and the bot
                // crosses. Does NOT fire when the destination is
                // already the portal centre, the next (pos+1) centre,
                // or a staged crossing/approach from the movement
                // prefix: those are never a pos+2-or-beyond centre
                // (the loop starts at pos+2), and the prefix always
                // gets the first word each movement frame, so a staged
                // waypoint is never overridden here -- this only
                // corrects the stock follower's far-centre re-aim.
                // No crossings-table dependency: the clamp reads only
                // the route, the portal quad, and segment centres, so
                // it applies on any leg of any level, table or not.
                for (int i = pos + 2; i < len; i++)
                {
                    Vector3 far;
                    if (!TryGetSegmentCentre(bot.Global_path[i], out far))
                        continue;
                    if (Vector3.Distance(dest, far) < 0.5f)
                    {
                        Vector3 clampTarget;
                        if (dw != null)
                        {
                            clampTarget = haveNormal
                                ? dw.Crossing + portalNormal * kPushThroughDistance
                                : dw.Crossing;
                        }
                        else
                        {
                            clampTarget = haveNormal
                                ? pc + portalNormal * kPushThroughDistance
                                : pc;
                        }
                        SetDestination(bot, clampTarget,
                            "portal-crossing far-centre clamp " + fromSeg + "->" + toSeg);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] HoldPortalLine failed: " + ex.ToString());
            }
        }

        // Fix C (Nov 23) -- general terrain-clearance lift on outdoor
        // aims. The 1437 z=512 stall is a portal centre 3.78 m under
        // the hill: the stock destination is a buried point, the bot
        // beaches on the slope edge (stay climbing to 50), and no
        // baked crossing exists for that leg. The lift is general,
        // not per-leg: whenever the destination sits in an outdoor
        // terrain cube, a downward raycast from 40 m above it finds
        // the ground, and the destination is raised to ground + 2.5 m
        // (bot radius 0.60 + 1.9 m of air). It only ever lifts UP
        // (never pushes a destination down), and caps at +20 m so a
        // bogus ray cannot fling the bot into the sky. The
        // destination-segment guard is what makes the ray safe: a
        // terrain cube is open-top, so the first hit below the ray
        // start IS the ground -- there is no ceiling above the dest
        // to confuse it (the 42->807 doorway roof is exactly the case
        // the guard excludes: neither 42 nor 807 is a terrain cube).
        // The portal flag is cleared (the lifted point is a
        // free-space point, not a portal point -- the same rationale
        // as RepairStaleDestination). Runs in the DoPathFollow
        // postfix, after HoldPortalLine and before LogDestination, so
        // it covers stock destinations as well as overrides, and the
        // log records the lifted destination. Validated crossings sit
        // above ground + 2.5 m already (1442->1437: ground + 2.07 m
        // clearance means ground + 2.5 = crossing - 0.43 m, no lift),
        // so the lift does not fight the table. Fail-open: no ray hit
        // (or any exception) leaves the stock destination alone.
        // Internal (not private): the DoPathFollow postfix calls it.
        internal static void LiftDestinationForTerrain(Robot bot)
        {
            try
            {
                if (bot.Global_path == null || bot.Global_path_length <= 0)
                    return;
                int cur = bot.m_current_segment;
                if (cur < 0)
                    return;
                Vector3 dest = bot.m_next_destination_position;
                // Destination must sit in a terrain cube (see above).
                int destSeg = -1;
                try
                {
                    LevelData ld = GameManager.m_level_data;
                    if (ld != null)
                        destSeg = ld.FindSegmentContainingWorldPosition(dest, cur);
                }
                catch (Exception) { }
                if (destSeg < 0 || !IsTerrainCube(destSeg))
                    return;
                Vector3 origin = dest + new Vector3(0f, 40f, 0f);
                int mask = ~(1 << 11); // everything except the robot layer
                RaycastHit hit;
                if (!Physics.Raycast(origin, Vector3.down, out hit, 80f, mask,
                    QueryTriggerInteraction.Ignore))
                    return; // no ground under the dest: leave it alone
                float lifted = hit.point.y + 2.5f;
                if (lifted <= dest.y || lifted > dest.y + 20f)
                    return;
                Vector3 liftedDest = new Vector3(dest.x, lifted, dest.z);
                bot.m_next_destination_position = liftedDest;
                SetTargetIsPortal(bot, false); // a free-space point, not a portal point
                SetDestination(bot, liftedDest,
                    "terrain-lift seg " + cur + " dest-seg " + destSeg);
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] terrain lift failed: " + ex.ToString());
            }
        }

        // Segment centre, fail-open (false when the level data is not
        // there or the segment number is out of range). Same idiom as
        // IsValidSegment / IsTerrainCube above.
        static bool TryGetSegmentCentre(int seg, out Vector3 centre)
        {
            centre = Vector3.zero;
            try
            {
                LevelData ld = null;
                try { ld = GameManager.m_level_data; } catch (Exception) { }
                if (ld == null || ld.Segments == null ||
                    seg < 0 || seg >= ld.Segments.Length)
                    return false;
                centre = ld.Segments[seg].Center;
                return true;
            }
            catch (Exception) { return false; }
        }

        // Portal centre (portal-crossing fix, Nov 22): replicate stock
        // Robot.GetTargetPointFromPortal (decompile L101753), which
        // returns the centre of the portal shared by two segments: the
        // first side whose portal index matches a side of the other
        // segment, averaged over its four vertices
        // (SegmentData.SideVertexOrder gives the vertex order per side;
        // LevelData.SegmentVerts holds the world vertices). Returns
        // false when the two segments share no portal, or when any of
        // the data is missing -- HoldPortalLine then leaves the stock
        // destination alone. Fail-open on any exception.
        static bool GetPortalCentre(Robot bot, int fromSeg, int toSeg, out Vector3 centre)
        {
            centre = Vector3.zero;
            try
            {
                LevelData ld = null;
                try { ld = GameManager.m_level_data; } catch (Exception) { }
                if (ld == null || ld.Segments == null || ld.SegmentVerts == null)
                    return false;
                if (fromSeg < 0 || fromSeg >= ld.Segments.Length ||
                    toSeg < 0 || toSeg >= ld.Segments.Length)
                    return false;
                int[] fromPorts = ld.Segments[fromSeg].Portals;
                int[] toPorts = ld.Segments[toSeg].Portals;
                int[] verts = ld.Segments[fromSeg].VertIndices;
                if (fromPorts == null || toPorts == null || verts == null)
                    return false;
                int n = fromPorts.Length < 6 ? fromPorts.Length : 6;
                int m = toPorts.Length < 6 ? toPorts.Length : 6;
                for (int i = 0; i < n; i++)
                {
                    if (fromPorts[i] < 0)
                        continue;
                    for (int j = 0; j < m; j++)
                    {
                        if (toPorts[j] < 0)
                            continue;
                        if (fromPorts[i] == toPorts[j])
                        {
                            Vector3 sum = Vector3.zero;
                            for (int k = 0; k < 4; k++)
                            {
                                int vi = verts[SegmentData.SideVertexOrder[i][k]];
                                sum += ld.SegmentVerts[vi];
                            }
                            centre = sum * 0.25f;
                            return true;
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] GetPortalCentre failed: " + ex.ToString());
                return false;
            }
        }

        // Portal normal (Fix B, Nov 23): the unit direction through
        // the portal shared by two segments, pointing from fromSeg
        // toward toSeg. Same side-finding as GetPortalCentre (the
        // first side whose portal index matches a side of the other
        // segment), kept as a parallel method so GetPortalCentre --
        // verified by flight logs -- stays byte-identical. The normal
        // is the Newell normal of the shared quad (cross of the
        // diagonals would do for a rectangle, but the quad may be a
        // general quadrilateral); it is flipped when it points away
        // from the next segment's centre. Degenerate quads (zero
        // area, or a to-centre that coincides with the portal centre)
        // return false -- the callers fall back to the old
        // centre-to-centre direction. Fail-open on any exception.
        static bool GetPortalNormal(int fromSeg, int toSeg, out Vector3 normal)
        {
            normal = Vector3.zero;
            try
            {
                LevelData ld = null;
                try { ld = GameManager.m_level_data; } catch (Exception) { }
                if (ld == null || ld.Segments == null || ld.SegmentVerts == null)
                    return false;
                if (fromSeg < 0 || fromSeg >= ld.Segments.Length ||
                    toSeg < 0 || toSeg >= ld.Segments.Length)
                    return false;
                int[] fromPorts = ld.Segments[fromSeg].Portals;
                int[] toPorts = ld.Segments[toSeg].Portals;
                int[] verts = ld.Segments[fromSeg].VertIndices;
                if (fromPorts == null || toPorts == null || verts == null)
                    return false;
                int n = fromPorts.Length < 6 ? fromPorts.Length : 6;
                int m = toPorts.Length < 6 ? toPorts.Length : 6;
                for (int i = 0; i < n; i++)
                {
                    if (fromPorts[i] < 0)
                        continue;
                    for (int j = 0; j < m; j++)
                    {
                        if (toPorts[j] < 0)
                            continue;
                        if (fromPorts[i] == toPorts[j])
                        {
                            Vector3[] q = new Vector3[4];
                            for (int k = 0; k < 4; k++)
                            {
                                int vi = verts[SegmentData.SideVertexOrder[i][k]];
                                q[k] = ld.SegmentVerts[vi];
                            }
                            // Newell normal over the quad.
                            Vector3 nn = Vector3.zero;
                            for (int k = 0; k < 4; k++)
                            {
                                Vector3 a = q[k];
                                Vector3 b = q[(k + 1) % 4];
                                nn.x += (a.y - b.y) * (a.z + b.z);
                                nn.y += (a.z - b.z) * (a.x + b.x);
                                nn.z += (a.x - b.x) * (a.y + b.y);
                            }
                            if (nn.sqrMagnitude < 1e-12f)
                                return false;
                            nn.Normalize();
                            // Orient toward the next segment.
                            Vector3 toCentre;
                            if (!TryGetSegmentCentre(toSeg, out toCentre))
                                return false;
                            Vector3 pc = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
                            if (Vector3.Dot(nn, toCentre - pc) < 0f)
                                nn = -nn;
                            normal = nn;
                            return true;
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.Log("[GuidebotSteering] GetPortalNormal failed: " + ex.ToString());
                return false;
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
                if (IsPassed(wp, botPos, dir, forward, kWaypointReach))
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

            // Next unpassed waypoint in travel order. The crossing
            // itself uses the tighter kCrossingReach (Fix B, Nov 23):
            // at kWaypointReach the sequence ended 2.0 m early and
            // handed back to the stock flip. (wp == dw.Crossing is an
            // exact-value comparison: seq holds the crossing itself.)
            for (int i = 0; i < seq.Count; i++)
            {
                int k = forward ? i : (seq.Count - 1 - i);
                Vector3 wp = seq[k];
                float reach = (wp == dw.Crossing) ? kCrossingReach : kWaypointReach;
                if (IsPassed(wp, botPos, dir, forward, reach))
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

        static bool IsPassed(Vector3 wp, Vector3 botPos, Vector3 dir, bool forward, float reach)
        {
            if (Vector3.Distance(botPos, wp) < reach)
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

        // Glass-route fix (Fix list #5, Nov 22): splice baked detours
        // for BLOCKED (glass) legs into a freshly built GuideBot path.
        // The stock pathfinder has no glass concept, so the avoidance
        // happens here, at path-build time, in the CreatePathToSegment
        // postfix -- after a NEW path was built (never on a kept-path
        // skip: a spliced path stays spliced when re-used) and before
        // the m_target_is_portal reset / any index logic reads it.
        // One pass over the path's adjacent pairs; each pair matching
        // a blocked leg (either direction) is replaced by the baked
        // detour -- forward order [a, v1..vn, b] for a path pair (a,b),
        // REVERSED [b, vn..v1, a] for a path pair (b,a). A path can
        // contain several blocked legs (the L1 spawn->key route uses
        // two); all are replaced in the one pass. Detours contain no
        // blocked legs (bake guarantee), so no chained re-splice.
        // Fail-open: a blocked leg with no detour, an empty detour,
        // or a splice that would overflow Global_path leaves the
        // stock path untouched and logs once. GuideBot only (the
        // postfix gates on m_is_guide_bot); every other robot's paths
        // never reach this code.
        internal static void SpliceBlockedLegs(Robot bot)
        {
            try
            {
                if (bot == null || bot.Global_path == null || bot.Global_path_length <= 0)
                    return;
                CrossingsTable table = GetTable();
                if (table == null || table.BlockedLegs.Count == 0)
                    return;
                int len = bot.Global_path_length;
                var path = new List<int>(len);
                for (int i = 0; i < len; i++)
                    path.Add(bot.Global_path[i]);
                List<int> spliced = SpliceBlockedLegsIntoPath(path, table, bot.Global_path.Length);
                if (object.ReferenceEquals(spliced, path))
                    return; // unchanged (nothing spliced, or fail-open)
                for (int i = 0; i < spliced.Count; i++)
                    bot.Global_path[i] = spliced[i];
                bot.Global_path_length = spliced.Count;
            }
            catch (Exception ex)
            {
                // Fail-open: a splice bug must never break pathing --
                // the stock path stands.
                Debug.Log("[GuidebotSteering] BLOCKED splice failed: " + ex.ToString());
            }
        }

        static BlockedLeg FindBlockedLeg(CrossingsTable table, int a, int b)
        {
            foreach (BlockedLeg leg in table.BlockedLegs)
            {
                if ((leg.A == a && leg.B == b) || (leg.A == b && leg.B == a))
                    return leg;
            }
            return null;
        }

        // Pure path rebuild (no Robot access, so it is unit-testable):
        // returns a NEW list when at least one leg was spliced, the
        // INPUT list (same reference) when nothing changed or when a
        // fail-open bail left the stock path untouched. maxLen is the
        // Global_path array capacity (1000): a splice that would
        // overflow it returns the input unchanged.
        // (CS0051: it takes the private CrossingsTable, so it must be
        // private too -- an internal method may not expose a private
        // type. The harness extracts it verbatim and calls it from
        // inside its own wrapper class, so private is no obstacle.)
        static List<int> SpliceBlockedLegsIntoPath(List<int> path, CrossingsTable table, int maxLen)
        {
            if (path == null || path.Count < 2 || table == null || table.BlockedLegs.Count == 0)
                return path;
            var outPath = new List<int>(path.Count);
            outPath.Add(path[0]);
            bool changed = false;
            for (int i = 0; i + 1 < path.Count; i++)
            {
                int a = path[i], b = path[i + 1];
                BlockedLeg leg = FindBlockedLeg(table, a, b);
                if (leg == null)
                {
                    outPath.Add(b);
                    continue;
                }
                if (leg.Via.Count == 0)
                {
                    Debug.Log("[GuidebotSteering] BLOCKED leg " + leg.A + "<->" + leg.B +
                        " has no detour -- leaving stock path");
                    outPath.Add(b);
                    continue;
                }
                changed = true;
                if (a == leg.A && b == leg.B)
                {
                    // Forward: [a, v1..vn, b]; a already appended.
                    foreach (int v in leg.Via)
                        outPath.Add(v);
                    outPath.Add(b);
                }
                else
                {
                    // Reversed: [b, vn..v1, a]; b (== path[i+1]) is the
                    // leg's A end, appended last; path[i] (leg.B)
                    // already appended.
                    for (int k = leg.Via.Count - 1; k >= 0; k--)
                        outPath.Add(leg.Via[k]);
                    outPath.Add(b);
                }
                if (outPath.Count > maxLen)
                {
                    Debug.Log("[GuidebotSteering] BLOCKED splice would exceed path capacity (" +
                        outPath.Count + " > " + maxLen + ") -- leaving stock path");
                    return path;
                }
                Debug.Log("[GuidebotSteering] spliced BLOCKED leg " + leg.A + "<->" + leg.B +
                    ": inserted " + leg.Via.Count + " detour segments");
            }
            if (!changed)
                return path;
            return outPath;
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
                else if (t[0] == "BLOCKED")
                {
                    // BLOCKED <a> <b> <v1> <v2> ... <vn>
                    // Glass leg a<->b (a<b) with a baked detour: the
                    // full detour path is [a, v1, ..., vn, b].
                    // Same strictness as the rest of the parser: bad
                    // ints, or fewer than 2 endpoints, fail the WHOLE
                    // table (return null). An old table has no BLOCKED
                    // lines and parses exactly as before.
                    if (t.Length < 3)
                    {
                        Debug.Log("[GuidebotSteering] bad BLOCKED line: " + lines[i]);
                        return null;
                    }
                    var bl = new BlockedLeg();
                    if (!TryInt(t[1], out bl.A) || !TryInt(t[2], out bl.B))
                    {
                        Debug.Log("[GuidebotSteering] bad BLOCKED numbers: " + lines[i]);
                        return null;
                    }
                    for (int k = 3; k < t.Length; k++)
                    {
                        int v;
                        if (!TryInt(t[k], out v))
                        {
                            Debug.Log("[GuidebotSteering] bad BLOCKED detour: " + lines[i]);
                            return null;
                        }
                        bl.Via.Add(v);
                    }
                    table.BlockedLegs.Add(bl);
                    i++;
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
                // Glass-route fix (Fix list #5, Nov 22): splice baked
                // BLOCKED-leg detours into the fresh path now, before
                // the portal-flag reset below / any index logic reads
                // it. Runs ONLY on a new path (kept-path skips return
                // above, so a spliced path stays spliced).
                Robot_DoPathfindMovement_GuidebotSteering.SpliceBlockedLegs(__instance);
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
    // the off-route branch stays forward-only. It also logs the
    // destination (LogDestination, Nov 22, e5a796ff flight), so the
    // next flight's log shows what the bot was flying toward.
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
                // Portal-crossing fix (Nov 22): break the portal-centre
                // <-> next-centre limit cycle and hold doorway crossings,
                // before the log line -- so the line records the
                // overridden destination, not the stock flip.
                Robot_DoPathfindMovement_GuidebotSteering.HoldPortalLine(__instance);
                // Fix C (Nov 23): lift outdoor aims above the terrain
                // (buried portal centres), before the log line -- so
                // the line records the lifted destination.
                Robot_DoPathfindMovement_GuidebotSteering.LiftDestinationForTerrain(__instance);
                // Destination logging (Nov 22, e5a796ff flight): after
                // the resync, so the line records the destination the
                // stock follower set for the next frame with the
                // resynced index.
                Robot_DoPathfindMovement_GuidebotSteering.LogDestination(__instance);
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
    //
    // Fix D (Nov 23) -- kept-path destination stomp. The TO_GOAL case
    // (decompile L92935-ish) sets m_next_destination_position =
    // Segments[Global_path[0]].Center after ANY successful
    // CreatePathToSegment -- including our kept-path skip, where the
    // prefix returns __result = true without rebuilding the path, so
    // InitPath never re-runs and the destination is never re-derived:
    // the accumulated destination (portal aim, doorway hold, terrain
    // lift) is stomped to the route-origin centre every 3 s
    // (sub_move_timer). The prefix therefore saves the destination on
    // entry (and resets the shared kept-path marker, so a true
    // observed in the postfix was set during THIS call), and the
    // postfix restores it when the path was kept and the dest was
    // stomped to the origin centre. Unlike the hold inputs, this runs
    // regardless of kHoldGuidebotToGoal_TestFlight: it is a permanent
    // fix, not a test setting. The movement prefix's
    // RepairStaleDestination already repairs the stomp on most frames
    // (pos > 0, now pos 0 too); this preserves the accumulated state
    // across the call for the frames where the follower does not
    // re-derive it (off-route, ridge climb). Fail-open: any exception
    // leaves the stomped destination in place (the repair still runs).
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

        // Fix D save slots: the destination on entry, and whether it
        // is valid (set by the prefix, consumed by the postfix).
        private static Vector3 s_savedDestination;
        private static bool s_savedDestValid;

        static bool Prefix(Robot __instance, int goal_seg)
        {
            if (__instance == null || !__instance.m_is_guide_bot)
                return true;
            try
            {
                // Fix D: save the destination and reset the kept-path
                // marker BEFORE the original runs (see above). This
                // runs regardless of the test-flight hold flag.
                s_savedDestination = __instance.m_next_destination_position;
                s_savedDestValid = true;
                Robot_CreatePathToSegment_GuidebotSteering.s_createPathSkipped = false;
                if (!kHoldGuidebotToGoal_TestFlight)
                    return true;
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
                s_savedDestValid = false;
                Debug.Log("[GuidebotSteering] GuideBotLeadPlayer hold prefix failed: " + ex.ToString());
                return true;
            }
        }

        static void Postfix(Robot __instance)
        {
            // Restore the hold inputs first (existing behaviour).
            if (s_holdActive)
            {
                s_holdActive = false;
                try
                {
                    if (__instance != null)
                    {
                        __instance.m_target_dist = s_savedTargetDist;
                        __instance.target_visible = s_savedTargetVisible;
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.Log("[GuidebotSteering] GuideBotLeadPlayer hold postfix failed: " + ex.ToString());
                }
            }
            // Fix D: undo the kept-path destination stomp (see above).
            if (!s_savedDestValid)
                return;
            s_savedDestValid = false;
            try
            {
                if (__instance == null)
                    return;
                if (!Robot_CreatePathToSegment_GuidebotSteering.s_createPathSkipped)
                    return; // original ran (new path): the reset is legitimate
                if (__instance.Global_path == null || __instance.Global_path_length <= 0)
                    return;
                LevelData ld = null;
                try { ld = GameManager.m_level_data; } catch (System.Exception) { }
                if (ld == null || ld.Segments == null)
                    return;
                int origin = __instance.Global_path[0];
                if (origin < 0 || origin >= ld.Segments.Length)
                    return;
                Vector3 originCentre = ld.Segments[origin].Center;
                Vector3 now = __instance.m_next_destination_position;
                if ((now - originCentre).sqrMagnitude > 0.25f)
                    return; // not stomped: the original left another destination
                if ((s_savedDestination - originCentre).sqrMagnitude <= 0.25f)
                    return; // saved dest WAS the origin centre: nothing to restore
                __instance.m_next_destination_position = s_savedDestination;
                Debug.Log("[GuidebotSteering] kept-path dest stomp undone: restored " +
                    s_savedDestination.ToString() + " (GuideBotLeadPlayer had reset it to route-origin seg " +
                    origin + " centre)");
            }
            catch (System.Exception ex)
            {
                Debug.Log("[GuidebotSteering] GuideBotLeadPlayer dest-restore postfix failed: " + ex.ToString());
            }
        }
    }

    // Wander guard (Nov 22, 7481ae07 flight): a deactivation in the
    // terrain threw IndexOutOfRangeException ~26 times in
    // GuidebotChooseWanderSegment -> Pathfinding.FindGoodNearbySegment
    // (the bot's segment lookup is unreliable in the big open terrain
    // cubes, so my_seg -- Robot.SegmentIndex, set each frame in
    // DoModeGuidebot -- can be invalid), the exception broke the AI,
    // and the bot fell out of the world (the failsafe put it in seg 0
    // and it sat stuck in seg 1). Fail-open: when the bot's segment
    // is a terrain cube or invalid, skip the wander pick entirely --
    // the bot stays where it is instead of throwing. In GO_AWAY it
    // keeps fading out via DoModeGuidebot's end-of-frame fade (the
    // fade does not depend on the wander pick); in WANDER it stays
    // put. The stock wander is only a "pick a nearby segment to idle
    // near" convenience; skipping it loses nothing the bot needs.
    // GuideBot only (the method is GuideBot-only by construction, but
    // the guard is explicit). Fail-open: a guard bug runs the
    // original.
    [HarmonyPatch(typeof(Robot), "GuidebotChooseWanderSegment")]
    public class Robot_GuidebotChooseWanderSegment_GuidebotSteering
    {
        static bool Prefix(Robot __instance, int my_seg)
        {
            try
            {
                if (__instance == null || !__instance.m_is_guide_bot)
                    return true;
                if (!Robot_DoPathfindMovement_GuidebotSteering.IsValidSegment(my_seg))
                {
                    Debug.Log("[GuidebotSteering] wander guard: skipping GuidebotChooseWanderSegment, my_seg invalid (" + my_seg + ") -- fail-open, bot stays put");
                    return false;
                }
                if (Robot_DoPathfindMovement_GuidebotSteering.IsTerrainCube(my_seg))
                {
                    Debug.Log("[GuidebotSteering] wander guard: skipping GuidebotChooseWanderSegment, my_seg " + my_seg + " is a terrain cube -- fail-open, bot stays put");
                    return false;
                }
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.Log("[GuidebotSteering] wander guard failed: " + ex.ToString());
                return true;
            }
        }
    }

    // FindGoodNearbySegment safety net (Nov 22, 7481ae07 flight): the
    // method indexes Segments[start_seg] and m_SegmentBuffer[start_seg]
    // with no bounds check, so an invalid start_seg throws
    // IndexOutOfRangeException (the throw site in the 7481ae07
    // flight, <0x001a7> in the stack trace). This prefix is the safety
    // net for ALL callers -- the GuideBot wander path and
    // Robot.FindBackupSegment's stuck recovery: an invalid start_seg
    // returns false with actual_path_length = -1, exactly the stock
    // failure value (the level_data == null branch does the same), so
    // every caller handles it as "no nearby segment found". A valid
    // terrain-cube start_seg does NOT throw here (all indices are in
    // range for a valid segment), so no terrain check is needed at
    // this level -- the GuideBot's terrain policy lives in the
    // GuidebotChooseWanderSegment prefix above. Fail-open: a guard bug
    // runs the original (which throws only if start_seg is genuinely
    // invalid, as before).
    [HarmonyPatch(typeof(Pathfinding), "FindGoodNearbySegment")]
    public class Pathfinding_FindGoodNearbySegment_GuidebotSteering
    {
        static bool Prefix(int start_seg, ref int actual_path_length, ref bool __result)
        {
            try
            {
                if (Robot_DoPathfindMovement_GuidebotSteering.IsValidSegment(start_seg))
                    return true;
                actual_path_length = -1;
                __result = false;
                Debug.Log("[GuidebotSteering] FindGoodNearbySegment guard: start_seg invalid (" + start_seg + ") -- fail-open, no nearby segment");
                return false;
            }
            catch (System.Exception ex)
            {
                Debug.Log("[GuidebotSteering] FindGoodNearbySegment guard failed: " + ex.ToString());
                return true;
            }
        }
    }
}
