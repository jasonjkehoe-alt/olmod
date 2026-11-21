using HarmonyLib;
using Overload;
using System.Reflection;
using UnityEngine;

namespace GameMod
{
    // Relentless first Goblin (Nov 20, 2026): the FIRST Goblin in L1
    // (room 32, [405.2, 54.7, 473.7]) must keep hunting the player even
    // when the player breaks line of sight -- never "forget" the player.
    // Jason's go Nov 20 ~19:55 ("Relentless would be nice").
    //
    // WHAT IS ALREADY PROVEN (decompile archaeology, see the report):
    //   * AI_always_pathfind_to_player (public bool, Robot L89444) makes
    //     DoModePathfind re-path to the believed player position every
    //     5-6 s instead of going ASLEEP (Robot.DoModePathfind, L95785).
    //     It does NOT stop the bot from ENTERING ASLEEP mode in the first
    //     place -- that is the "forget".
    //   * The "forget" is DoModeApproach (non-melee, L96119): when the bot
    //     is chasing and MoveTowardsTarget2 returns false (no direct line
    //     to the player -- wall / broken line of sight), it goes PATHFIND
    //     for 2 s with AI_next_mode = ASLEEP queued. DoModePathfind then
    //     executes AI_next_mode via set_AI_mode(ASLEEP, 2f) (L95747-95758).
    //     Other give-up paths also end in set_AI_mode(ASLEEP, ...):
    //     DoModeSearching L96376 (SEARCHING timer expired), DoModeStation
    //     return L95716, the cloak handler L95468/L95482.
    //   * set_AI_mode (Robot L90855) is the single funnel: EVERY give-up
    //     transition goes through it, including the AI_next_mode queue.
    //     It also clears AI_cheat_pathfind_to_player on every call, so
    //     the "cheat" (knowing the player's real position) never survives
    //     a mode change.
    //   * m_has_seen_player (private bool, Robot L89594) is set to true
    //     by UpdateVisibility the first time the bot actually sees the
    //     player (line of sight + within FOV, L96963). It is false at
    //     level start and latches true after first contact. It is the
    //     gate: the bot must sleep normally until first contact, and only
    //     become relentless after that (otherwise it would hunt the player
    //     at spawn, ~49 m away, before the player has moved).
    //   * AI_robot_station is FALSE for the first Goblin (0 Cmd 10
    //     occurrences in the deployed .sp; absent from the GRUNTA
    //     robotdata entry), so the STATION branch in DoModePathfind does
    //     not apply. STATION is still intercepted here as a safety net.
    //   * A robotdata change is REJECTED for this job: the GRUNTA entry
    //     is type-wide, so any change there would make ALL 44 Goblins
    //     relentless, not just the first one.
    //
    // APPROACH: a Harmony prefix on Robot.set_AI_mode. For the first
    // Goblin only, after first contact, any transition to ASLEEP or
    // STATION is redirected to PATHFIND (timer 0.5 s). The existing
    // AI_always_pathfind_to_player flag then makes DoModePathfind re-path
    // to the player's current segment every 5-6 s, and
    // UpdateBelievedPlayerLocation refreshes the believed position to
    // near the player's actual position every ~1 s in PATHFIND mode, so
    // the bot keeps closing in. Combat modes (APPROACH, CIRCLE, BACKOFF,
    // SEARCHING, ZIGZAG, ...) pass through untouched.
    //
    // Identification: robot_type == GRUNTA (0) AND
    // AI_always_pathfind_to_player == true AND the Robot's position is
    // within 1.0 m of the first Goblin's spawn [405.2, 54.7, 473.7] at
    // identification time. Only the first Goblin has the flag (exactly 1
    // Cmd 10 occurrence in the deployed .sp, value True). The reference
    // is cached once identified (the bot moves, so position is only
    // checked at identification). The cache self-invalidates on level
    // unload (a destroyed Unity object compares == null).
    //
    // Fail-open: the prefix body is wrapped in try/catch (same pattern as
    // SwitchRotateOnce/SwatterHp) -- a patch exception logs a tag and the
    // original set_AI_mode runs unchanged (stock behaviour).
    [HarmonyPatch(typeof(Robot), "set_AI_mode")]
    public class Robot_set_AI_mode_RelentlessGoblin
    {
        // First Goblin spawn (room 32), fixed D3 data.
        private static readonly Vector3 kFirstGoblinSpawn =
            new Vector3(405.2f, 54.7f, 473.7f);
        // Position match tolerance (m), same as the Swatter patch.
        private const float kMatchTolerance = 1.0f;
        // PATHFIND timer for a redirected transition (s). Short so the
        // always-pathfind re-path in DoModePathfind fires almost at once.
        private const float kRedirectPathfindTimer = 0.5f;

        // Cached first-Goblin reference (null until identified).
        private static Robot s_firstGoblin;

        // Private Robot.m_has_seen_player (Robot L89594), read via
        // AccessTools (same reflection pattern the GameMod already uses
        // in Console.cs / Controllers.cs / AxisCountFix.cs).
        private static readonly FieldInfo s_hasSeenPlayerField =
            AccessTools.Field(typeof(Robot), "m_has_seen_player");

        static bool Prefix(Robot __instance, ref AIModeType mode, ref float timer)
        {
            try { return PrefixInner(__instance, ref mode, ref timer); }
            catch (System.Exception ex)
            {
                // Fail open to stock behaviour: a patch bug must never
                // break a Robot's AI mode transition.
                Debug.Log("[RelentlessGoblin] prefix failed: " + ex.ToString());
                return true;
            }
        }

        static bool PrefixInner(Robot __instance, ref AIModeType mode, ref float timer)
        {
            if (__instance == null)
                return true;

            // Identify the first Goblin once (GRUNTA + the flag + at its
            // spawn position). The bot moves, so position is only checked
            // at identification; the reference is cached after that.
            if (s_firstGoblin == null)
            {
                if (__instance.robot_type == EnemyType.GRUNTA
                    && __instance.AI_always_pathfind_to_player
                    && Vector3.Distance(__instance.transform.position, kFirstGoblinSpawn) <= kMatchTolerance)
                {
                    s_firstGoblin = __instance;
                    Debug.Log("[RelentlessGoblin] first Goblin identified at "
                        + __instance.transform.position.ToString() + " ("
                        + __instance.gameObject.name + ").");
                }
                else
                {
                    return true;
                }
            }
            if (__instance != s_firstGoblin)
                return true;

            // Only intercept "give up" transitions (ASLEEP / STATION).
            // Combat and hunting modes pass through untouched.
            if (mode != AIModeType.ASLEEP && mode != AIModeType.STATION)
                return true;

            // A dying bot must be allowed to die normally.
            if (__instance.m_dying)
                return true;

            // Only after first contact (has seen the player). Before
            // that, let it sleep normally -- the init set_AI_mode(ASLEEP)
            // at level start must pass, or the bot would hunt the player
            // at spawn before the player has moved.
            if (!HasSeenPlayer(__instance))
                return true;

            // Redirect: instead of sleeping / returning to station, keep
            // pathfinding to the player. The existing
            // AI_always_pathfind_to_player flag makes DoModePathfind
            // re-path to the player's current segment every 5-6 s.
            AIModeType origMode = mode;
            mode = AIModeType.PATHFIND;
            timer = kRedirectPathfindTimer;
            Debug.Log("[RelentlessGoblin] first Goblin tried to go " + origMode
                + "; redirected to PATHFIND (relentless).");
            return true; // run the original with the modified args
        }

        private static bool HasSeenPlayer(Robot robot)
        {
            if (s_hasSeenPlayerField == null)
                return false;
            object v = s_hasSeenPlayerField.GetValue(robot);
            return v is bool b && b;
        }
    }
}
