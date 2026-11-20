using HarmonyLib;
using Overload;
using UnityEngine;

namespace GameMod
{
    // Swatter HP patch (Nov 20, 2026): the 2 Swatters in L1 are GRUNTA
    // (robot_type 0) with 55 HP in game because RobotManager.ReadPresetData
    // clobbers the per-instance m_hp=1000 Cmd 10 with the GRUNTA robotdata
    // entry (m_hp 55). The type-wide entry cannot be raised -- that would
    // give all 44 Goblins 1000 HP. So this Harmony postfix on
    // RobotManager.ReadPresetData sets m_hp=1000 (4x the D3-correct 250)
    // on the 2 Swatter instances only, after ReadPresetData has run.
    //
    // Hook: postfix on RobotManager.ReadPresetData(GameObject[]).
    // Gated on robot_prefab == null, so it only runs on the level-init
    // path (InitializeForNewLevel -> ReadPresetData(null)), which sets
    // fields on the LIVE Robots in the scene. When called with a prefab
    // array, ReadPresetData sets fields on the prefab templates -- the
    // postfix returns immediately there (setting the GRUNTA prefab's HP
    // would give every future Goblin 1000 HP).
    //
    // Identification: robot_type == GRUNTA (0) AND the Robot's position
    // is within 1.0 m of one of the 2 known Swatter positions
    // (631.3, 38.6, 473.6) / (580.4, 38.6, 444.0). A regular GRUNTA
    // cannot match: the other 42 Goblins are placed elsewhere. The
    // GameObject name is logged for confirmation (prefab
    // entity_enemy_Swatter) but is not the matcher, because the .sp
    // names instances from the prefab and the runtime name is not
    // guaranteed.
    //
    // Both m_hp and m_hp_starting are set to 1000. The robotdata entry
    // has m_hp but no m_hp_starting, so ReadPresetData leaves
    // m_hp_starting at its prior value; setting both keeps the damage
    // display ratio (m_hp / m_hp_starting) correct, the same way
    // Robot.MakeRobotVariant sets them together.
    //
    // Idempotent: if ReadPresetData runs again (level restart), it resets
    // the Swatters to 55 and this postfix sets them to 1000 again.
    // On Continue (save/load), ReadPresetData does not run -- the saved
    // HP is used, which is correct.
    [HarmonyPatch(typeof(RobotManager), "ReadPresetData")]
    public class RobotManager_ReadPresetData_SwatterHp
    {
        // Target: 1000 HP (4x the D3-correct Swatter HP of 250).
        private const float kSwatterHp = 1000f;
        // Position match tolerance (m). Swatter positions are fixed D3
        // data and do not drift between reconverts; 1.0 m is generous.
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
                Debug.Log("[SwatterHp] postfix failed: " + ex.ToString());
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
                robot.m_hp = kSwatterHp;
                robot.m_hp_starting = kSwatterHp;
                patched++;
                Debug.Log("[SwatterHp] Swatter at " + pos.ToString() + " (" + robot.gameObject.name + ") HP set to " + kSwatterHp + ".");
            }
            if (patched > 0)
                Debug.Log("[SwatterHp] patched " + patched + " Swatter(s) to " + kSwatterHp + " HP.");
        }
    }
}
