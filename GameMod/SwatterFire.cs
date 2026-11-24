using HarmonyLib;
using Overload;
using UnityEngine;

namespace GameMod
{
    // Swatter firing split (Nov 24, 2026): the 2 Swatters in L1 are
    // GRUNTA (robot_type 0) but must fire on their own setting -- the
    // stock GRUNTA profile (proj_enemy_blaster, long 17-22 bursts,
    // fire_dist 30, fire_angle 25) -- while the 42 Goblins keep the
    // Nov 20 override (proj_enemy_vulcan, 4-shot bursts, fire_dist 50,
    // fire_angle 45). The type-wide GRUNTA robotdata entry cannot be
    // changed (that hits all 42 Goblins), a separate robotdata entry
    // is impossible (the engine caps at 26 types), and per-instance
    // Cmd 10 tuning is dead (ReadPresetData clobbers it -- proven
    // Nov 20). So this Harmony postfix on RobotManager.ReadPresetData
    // restores the stock GRUNTA firing profile on the 2 Swatter
    // instances only, after ReadPresetData has run. Same hook, same
    // position matcher, and same fail-open shape as the flight-proven
    // SwatterHp.cs (which sets the 1000 HP).
    //
    // Hook: postfix on RobotManager.ReadPresetData(GameObject[]).
    // Gated on robot_prefab == null, so it only runs on the level-init
    // path (InitializeForNewLevel -> ReadPresetData(null)), which sets
    // fields on the LIVE Robots in the scene. When called with a prefab
    // array, ReadPresetData sets fields on the prefab templates -- the
    // postfix returns immediately there (setting the GRUNTA prefab's
    // firing profile would give every future Goblin the blaster).
    //
    // Identification: robot_type == GRUNTA (0) AND the Robot's position
    // is within 1.0 m of one of the 2 known Swatter positions
    // (631.3, 38.6, 473.6) / (580.4, 38.6, 444.0). A regular GRUNTA
    // cannot match: the other 42 Goblins are placed elsewhere. The
    // GameObject name is logged for confirmation but is not the
    // matcher (both Swatters are named entity_enemy_grunta at runtime,
    // identical to a Goblin -- flight-log proven Nov 23).
    //
    // Fields restored (stock GRUNTA, robot_data_stock.txt):
    //   m_fire_projectile = ProjPrefab.proj_enemy_blaster (red laser
    //     feel -- the ribbon renders orange-red; this is what Jason
    //     saw and liked before Nov 20)
    //   m_burst_fire_min = 17, m_burst_fire_max = 22 (long sustained
    //     bursts, not the Goblin 4-shot bursts)
    //   AI_fire_dist = 30, AI_fire_angle = 25 (matches the blaster's
    //     ~28 m effective range; the 50 m Goblin distance would make
    //     the Swatter fire at targets its bolts can never reach)
    // Everything else (refire 0.18-0.225, delay 1.8-2.2, SEQUENTIAL,
    // spread 5) is already at stock values -- no change needed.
    //
    // The kill-feed name is NOT changed here (OGRE vs GOBLIN is
    // Jason's call). The feed reads EnemyNames[robot_type]; a Swatter
    // still reads GOBLIN. Do NOT set AI_robot_no_move here or in the
    // shared GRUNTA entry (it would freeze all 42 Goblins).
    //
    // Idempotent: if ReadPresetData runs again (level restart), it
    // resets the Swatters to the Goblin profile and this postfix
    // restores the stock profile again. On Continue (save/load),
    // ReadPresetData does not run -- the saved fields are used.
    [HarmonyPatch(typeof(RobotManager), "ReadPresetData")]
    public class RobotManager_ReadPresetData_SwatterFire
    {
        // Position match tolerance (m). Same as SwatterHp.cs; the
        // Swatter positions are fixed D3 data and do not drift.
        private const float kMatchTolerance = 1.0f;

        private static readonly Vector3[] kSwatterPositions = new Vector3[]
        {
            new Vector3(631.3f, 38.6f, 473.6f),
            new Vector3(580.4f, 38.6f, 444.0f),
        };

        static void Postfix(GameObject[] robot_prefab)
        {
            try { PostfixInner(robot_prefab); }
            catch (System.Exception ex)
            {
                // Fail open: a patch bug must never break level init.
                Debug.Log("[SwatterFire] postfix failed: " + ex.ToString());
            }
        }

        static void PostfixInner(GameObject[] robot_prefab)
        {
            // Only the level-init path (live scene Robots). The prefab
            // path sets fields on templates -- do not touch it.
            if (robot_prefab != null)
                return;

            Robot[] robots = UnityEngine.Object.FindObjectsOfType<Robot>();
            if (robots == null || robots.Length == 0)
                return;

            int patched = 0;
            foreach (Robot robot in robots)
            {
                if (robot == null)
                    continue;
                if (robot.robot_type != EnemyType.GRUNTA)
                    continue;
                Vector3 pos = robot.transform.position;
                bool isSwatter = false;
                foreach (Vector3 sw in kSwatterPositions)
                {
                    if (Vector3.Distance(pos, sw) <= kMatchTolerance)
                    {
                        isSwatter = true;
                        break;
                    }
                }
                if (!isSwatter)
                    continue;
                robot.m_fire_projectile = ProjPrefab.proj_enemy_blaster;
                robot.m_burst_fire_min = 17;
                robot.m_burst_fire_max = 22;
                robot.AI_fire_dist = 30f;
                robot.AI_fire_angle = 25f;
                patched++;
                Debug.Log("[SwatterFire] Swatter at " + pos.ToString() + " (" + robot.gameObject.name + ") firing set to stock GRUNTA: proj_enemy_blaster, burst 17-22, fire_dist 30, fire_angle 25.");
            }
            if (patched > 0)
                Debug.Log("[SwatterFire] patched " + patched + " Swatter(s) to stock GRUNTA firing profile.");
        }
    }
}
