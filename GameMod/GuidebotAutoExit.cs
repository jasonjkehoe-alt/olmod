using HarmonyLib;
using Overload;
using System;
using UnityEngine;

namespace GameMod
{
    // GuideBot conditional auto-advance: after the player picks up a key, the
    // bot should automatically guide to the NEXT objective (D3-style) instead
    // of going idle. In stock Overload it does nothing, because of a dead call
    // in the GO_TO_KEY case of Robot.DoModeGuidebot:
    //
    //   int num = FindNextGuidebotObjective();   // force_find defaults FALSE
    //   if (num != -1) { m_guidebot_goal = ...; break; }
    //   AI_guidebot_submode = WANDER_NEAR_PLAYER;  // "AWAITING COMMAND"
    //
    // FindNextGuidebotObjective returns -1 immediately unless force_find is
    // true, so the advance can never fire and the bot always wanders after a
    // key pickup ("he brought me to the key and then had nothing to do").
    //
    // This patch is a prefix on DoModeGuidebot. It runs before the stock
    // GO_TO_KEY case, at exactly the moment the stock code would advance:
    // when the bot is in GO_TO_KEY submode and the player-just-picked-up-a-key
    // flag is set. It calls FindNextGuidebotObjective(force_find: true) — the
    // game's own next-objective logic, the same call the command wheel's
    // "next objective" option makes — and sets m_guidebot_goal to the result.
    // The stock DoModeGuidebot then calls GuidebotSetGoal() on the same tick,
    // which applies the goal (submode, special goal segment, path, HUD line).
    //
    // CONDITIONAL (Jason, Nov 19): the bot only auto-exits when the exit is
    // the next objective. FindNextGuidebotObjective already encodes that:
    // it returns GO_TO_KEY while a key item is still in the level (so after
    // key #1 in a multi-key level the bot guides to key #2, NOT the exit),
    // GO_TO_REACTOR_OR_BOSS if a reactor/boss is next, and GO_TO_EXIT only
    // when no key and no reactor remain. It reads the live item list
    // (FindSegmentContainingKey scans RobotManager.m_master_item_list for a
    // KEY_SECURITY item), so no per-level data and no key counting is needed.
    // If it returns -1 (no more objectives) the prefix does nothing and the
    // stock GO_TO_KEY case wanders, as before.
    //
    // The flag is consumed here (set false) and the objective-found cue is
    // played, mirroring what the stock GO_TO_KEY case would do — so the case
    // never sees a stale flag. In a multi-key level this matters: without
    // consuming the flag, the GO_TO_KEY case on the next tick would read the
    // stale "key #1 picked up" flag while guiding to key #2 and wander.
    //
    // Trigger is the key pickup as observed by the bot (submode GO_TO_KEY +
    // flag), NOT the objective banner — the "ESCAPE THE FACILITY" banner
    // fires early passing R26 and is a separate bug; this patch does not
    // hook it.
    //
    // Decompile references (Assembly-CSharp, game 1.1.1886.0):
    //   GO_TO_KEY case: Robot.DoModeGuidebot, `if (m_player_picked_up_security_key)`
    //     + `int num = FindNextGuidebotObjective();` (dead: force_find false -> -1)
    //   next objective: Robot.FindNextGuidebotObjective(force_find: true)
    //     (key -> 3 GO_TO_KEY; reactor/boss -> 7; exit -> 13; surface -> 16;
    //      alien ship -> 17; none -> -1)
    //   goal application: Robot.DoModeGuidebot, `if (m_guidebot_goal != 0)
    //     GuidebotSetGoal();` then the submode switch; GuidebotSetGoal's
    //     GO_TO_EXIT case sets the exit segment, paths, and shows
    //     "FOLLOW ME TO THE EXIT"
    //   wheel precedent: PlayerShip.IssueGuidebotCommandFromWheel case 0 sets
    //     m_guidebot_goal = FindNextGuidebotObjective(force_find: true)
    //   flag set: Item.OnTriggerEnter, KEY_SECURITY case
    //     (`c_player.AddKey(); Robot.m_player_picked_up_security_key = true;`)
    [HarmonyPatch(typeof(Robot), "DoModeGuidebot")]
    public class Robot_DoModeGuidebot_GuidebotAutoExit
    {
        static void Prefix(Robot __instance)
        {
            try { PrefixInner(__instance); }
            catch (Exception ex) { Debug.Log("[GuidebotAutoExit] prefix failed: " + ex.ToString()); }
        }

        static void PrefixInner(Robot __instance)
        {
            // GuideBot only. DoModeGuidebot runs for the GuideBot's AI; combat
            // robots never reach this method, but gate anyway.
            if (!__instance.m_is_guide_bot)
                return;
            // Only when the bot was guiding to a key. Any other submode means
            // the bot was not on a key objective, so a key pickup is not its
            // business (the player may have found a key on their own).
            if (Robot.AI_guidebot_submode != AIGuidebotSubmodeType.GO_TO_KEY)
                return;
            // Only on the tick after a key pickup (the item code sets this flag
            // when the player collects a KEY_SECURITY item).
            if (!Robot.m_player_picked_up_security_key)
                return;

            // The game's own next-objective logic, with force_find so it actually
            // computes (the stock GO_TO_KEY case calls it without force_find and
            // always gets -1). Returns GO_TO_KEY while another key remains,
            // GO_TO_REACTOR_OR_BOSS / GO_TO_EXIT / GO_TO_SURFACE / GO_TO_ALIEN_SHIP
            // as appropriate, or -1 when there are no more objectives.
            int next = Robot.FindNextGuidebotObjective(force_find: true);
            if (next == -1)
            {
                // No more objectives: leave the flag set and the goal alone, so
                // the stock GO_TO_KEY case plays the cue and wanders
                // ("AWAITING COMMAND"), as it does today.
                return;
            }

            // Consume the pickup flag and play the objective-found cue, exactly
            // as the stock GO_TO_KEY case would — the case will not run now
            // (the submode changes below), so the flag must not stay set or a
            // later GO_TO_KEY case would read it stale and wander.
            Robot.m_player_picked_up_security_key = false;
            SFXCueManager.PlayCue2D(SFXCue.guidebot_objective_found);
            Robot.m_guidebot_goal = (AIGuidebotSubmodeType)next;
            Debug.Log("[GuidebotAutoExit] key picked up; next objective is " + (AIGuidebotSubmodeType)next + "; guiding to it.");
            // The original DoModeGuidebot now calls GuidebotSetGoal() (the goal
            // is not NONE), which sets the submode, the special goal segment
            // and the path, and the submode switch runs the new goal's case.
        }
    }
}
