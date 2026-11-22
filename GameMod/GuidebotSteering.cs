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
    // The leg is keyed off the PATH (Global_path + Global_path_index),
    // not off m_current_segment: the climb to y=50 takes the bot out of
    // the route segments (808/43/804 top at y=48.9) and into segment
    // 1470's volume, which is not on the route and not portal-connected
    // to it, so the bot's physical segment may be off-route while it
    // climbs. The path index is the reliable leg signal. The ridge rule
    // is additionally position-based (bot inside the band), so it applies
    // even if the path index is stuck.
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
    //   RIDGE <z_min> <z_max> <climb_y> <n_wp> <n_legs> <leg_from> <leg_to> ...
    //   <wp_x> <wp_y> <wp_z>        (n_wp lines, in route order)
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
    // ridge). Forward-only, so a resync can only accelerate an advance
    // the stock code would make, or skip a segment the bot is already
    // physically beyond. The crossings table and the crossing points
    // are UNCHANGED -- they were correct; the index was the missing
    // piece.
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

            // Current leg from the PATH, not the physical segment (the
            // bot's physical segment may be off-route while climbing --
            // see the header comment on segment 1470).
            int fromSeg = -1, toSeg = -1;
            if (__instance.Global_path != null && __instance.Global_path_length > 0)
            {
                int idx = __instance.Global_path_index;
                if (idx >= 0 && idx < __instance.Global_path_length)
                {
                    toSeg = __instance.Global_path[idx];
                    fromSeg = (idx > 0)
                        ? __instance.Global_path[idx - 1]
                        : __instance.m_current_segment;
                }
            }

            // ---- doorway: leg match, either direction ----
            foreach (var dw in table.Doorways)
            {
                if ((fromSeg == dw.From && toSeg == dw.To) ||
                    (fromSeg == dw.To && toSeg == dw.From))
                {
                    SetDestination(__instance, dw.Crossing,
                        "doorway " + fromSeg + "->" + toSeg);
                    return;
                }
            }

            // ---- ridge: position-based band (applies even if the path
            // index is stuck off-route) ----
            foreach (var rg in table.Ridges)
            {
                if (botPos.z < rg.ZMin || botPos.z > rg.ZMax)
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
        // Forward-only: the index never moves backward, so a resync can
        // only accelerate an advance the stock code would make when the
        // bot enters the segment, or skip a segment the bot is already
        // physically beyond. One log line per actual resync, so the next
        // flight log shows whether the index advances.
        static void ResyncPathIndex(Robot bot)
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

                // (a) Bot's segment IS on the route: jump the index to
                // the NEXT segment after it, so the bot aims forward,
                // not at its own segment. (Setting it to the bot's own
                // segment would also self-correct -- the stock code
                // advances on a match -- but aiming forward is the
                // point of the fix.)
                int pos = -1;
                for (int i = 0; i < len; i++)
                {
                    if (bot.Global_path[i] == cur) { pos = i; break; }
                }
                if (pos >= 0)
                {
                    int target = pos + 1;
                    if (target > len - 1)
                        target = len - 1;
                    if (target > idx)
                    {
                        bot.Global_path_index = target;
                        Debug.Log("[GuidebotSteering] resync path index " + idx + " -> " + target +
                            " (bot segment " + cur + " at path pos " + pos + ")");
                    }
                    return;
                }

                // (b) Bot's segment is NOT on the route (off-route, e.g.
                // seg 1470 in the ridge climb -- not on the route, not
                // portal-connected): fall back to the NEAREST route
                // segment by centre distance, and aim at the next one
                // after it. In the ridge band this lands on seg 804
                // (the route segment after the ridge): the bot at the
                // climb waypoint is nearest to 43's centre, so the
                // index jumps to 804. Segment centres come from the
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
                int target2 = best + 1;
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
}
