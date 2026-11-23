// CircularDoor.cs -- GameMod-driven circular slab for D3 round doors.
//
// D3 builds some doors as round openings (an octagonal portal face --
// e.g. the PTMC Covert H door behind the L1 spawn, room 35, 7.33 m).
// The native Overload DOOR frame is rectangular and cannot fill a round
// opening, so the converter emits these doors as a PROP:N0000_MINE on the
// existing mine_26 trapdoor carrier, and transform_sp_for_flames adds a
// Destroyable marker (ExplodeType SWITCH, HP 20, m_special_index 33) +
// kinematic Rigidbody at load. This patch finds the markers at level
// start, hides the carrier's flat trapdoor mesh, builds a cylinder disc
// that fills the round opening, and slides it open on player proximity.
//
// Motion: SLIDE UP (world +Y), ~1.0 s smoothstep. Why up: the disc
// exactly fills the round opening, so any in-plane slide would clip the
// frame; up is the only clean direction, and it matches the blast-door
// look. For a vertical door the disc slides in-plane into the lintel
// above the opening; for a horizontal door (floor hatch) up is the
// opening normal, so the disc lifts straight out like a hatch. D3 doors
// slide (they do not swing), and the converter does not read door flags,
// so the trigger is proximity (D3's default for doors): open within
// 3.0 m, close when the player stays beyond 4.5 m for 2.0 s.
//
// Fail-open: no marker in the .sp (the prop lands with the later batch
// reconvert) -> nothing is built and the level loads unchanged. A marker
// with an invalid diameter, or any build error, is logged and skipped.
// The marker's damage is swallowed (D3 doors are not destroyed by
// shooting), via the ApplyDamage prefix below.

using HarmonyLib;
using Overload;
using UnityEngine;

namespace GameMod
{
    public static class CircularDoorPatch
    {
        // Must match CIRCULAR_DOOR_SPECIAL_INDEX in converters/d3_mp_converter.py.
        // 33 is free: 0 = hatch handle/default, 2 = matcen marker, 31 = mine_31 switches.
        public const int kCircularDoorSpecialIndex = 33;

        // Proximity trigger (D3's default for doors) + animation.
        internal const float kOpenDistance = 3.0f;    // open when the player is this close (m)
        internal const float kCloseDistance = 4.5f;   // close only once the player is this far (m)
        internal const float kCloseDelay = 2.0f;      // ... and stays that far this long (s)
        internal const float kSlideDuration = 1.0f;   // open/close animation time (s)
        private const float kThickness = 0.25f;      // disc thickness (m)
        private const float kCoverageMargin = 1.02f; // disc diameter = opening * 1.02 (covers the octagon's vertices; the rim hides in the jamb)
        private const float kMinDiameter = 1.0f;     // sanity range for the converter's diameter (m)
        private const float kMaxDiameter = 20.0f;
        internal const float kOpenClearance = 0.2f;   // disc bottom edge clears the opening top by this much (m)

        [HarmonyPatch(typeof(GameplayManager), "StartLevel")]
        public static class StartLevelPatch
        {
            static void Postfix()
            {
                try
                {
                    Destroyable[] all = UnityEngine.Object.FindObjectsOfType<Destroyable>();
                    int found = 0;
                    foreach (Destroyable d in all)
                    {
                        if (d == null) continue;
                        if (d.m_explode_type == Destroyable.ExplodeType.SWITCH
                            && d.m_special_index == kCircularDoorSpecialIndex)
                        {
                            found++;
                            try { BuildDoor(d); }
                            catch (System.Exception ex)
                            {
                                Debug.Log("[CircularDoor] BuildDoor failed for marker at "
                                    + d.transform.position + " (fail open, doorway stays open): " + ex.ToString());
                            }
                        }
                    }
                    if (found > 0)
                        Debug.Log("[CircularDoor] StartLevel: found " + found + " circular-door marker(s).");
                }
                catch (System.Exception ex)
                {
                    // Never break level load: no disc is better than no level.
                    Debug.Log("[CircularDoor] StartLevel scan failed (fail open): " + ex.ToString());
                }
            }
        }

        private static void BuildDoor(Destroyable marker)
        {
            // The opening diameter rides the carrier's localScale.x (set by
            // transform_sp_for_flames from the intermediate door_diameter).
            float opening = marker.transform.localScale.x;
            if (opening < kMinDiameter || opening > kMaxDiameter)
            {
                Debug.Log("[CircularDoor] Marker at " + marker.transform.position
                    + " has invalid diameter " + opening + " (expected " + kMinDiameter
                    + "-" + kMaxDiameter + " m); skipping (fail open).");
                return;
            }
            float diameter = opening * kCoverageMargin;

            // Grab the carrier's material BEFORE hiding it, so the disc
            // wears the same trapdoor look.
            Material carrierMat = null;
            MeshRenderer[] renderers = marker.GetComponentsInChildren<MeshRenderer>();
            foreach (MeshRenderer r in renderers)
            {
                if (r != null && r.sharedMaterial != null) { carrierMat = r.sharedMaterial; break; }
            }
            // Hide the flat trapdoor mesh (the carrier is only a marker now).
            int hidden = 0;
            foreach (MeshRenderer r in renderers)
            {
                if (r != null) { r.enabled = false; hidden++; }
            }
            foreach (SkinnedMeshRenderer r in marker.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (r != null) { r.enabled = false; hidden++; }
            }

            // Build the disc top-level (NOT a child of the carrier: the
            // carrier's localScale is (d, d, d) and would pollute a child).
            // Cylinder primitive: height 2 along local Y, radius 0.5.
            GameObject slab = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            slab.name = "CircularDoorSlab";
            slab.transform.position = marker.transform.position;
            // Cylinder Y (height) -> carrier Z (the opening normal).
            slab.transform.rotation = marker.transform.rotation * Quaternion.Euler(90f, 0f, 0f);
            slab.transform.localScale = new Vector3(diameter, kThickness * 0.5f, diameter);
            if (carrierMat != null)
                slab.GetComponent<MeshRenderer>().sharedMaterial = carrierMat;
            // Replace the capsule collider with a box that fills the
            // opening: size (1, 2, 1) in the slab's local units, scaled by
            // (D, T/2, D), gives world (D, T, D) in the slab's local axes --
            // D wide and D tall across the opening, T thick along the normal.
            UnityEngine.Object.Destroy(slab.GetComponent<CapsuleCollider>());
            BoxCollider bc = slab.AddComponent<BoxCollider>();
            bc.size = new Vector3(1f, 2f, 1f);
            bc.center = Vector3.zero;

            CircularDoorController ctl = marker.gameObject.AddComponent<CircularDoorController>();
            ctl.Init(slab, diameter);
            Debug.Log("[CircularDoor] Built slab at " + marker.transform.position
                + " diameter " + diameter.ToString("F2") + " m (opening " + opening.ToString("F2")
                + " m), hid " + hidden + " carrier renderer(s).");
        }
    }

    // Slides one circular-door slab up on player proximity. Lives on the
    // marker (the carrier); holds the top-level slab by reference.
    public class CircularDoorController : MonoBehaviour
    {
        private GameObject m_slab;
        private Vector3 m_closedPos;
        private Vector3 m_openPos;
        private float m_animT;
        private int m_state; // 0 closed, 1 opening, 2 open, 3 closing
        private float m_farTimer;

        public void Init(GameObject slab, float diameter)
        {
            m_slab = slab;
            m_closedPos = slab.transform.position;
            // Slide up until the disc's bottom edge clears the opening's
            // top edge by kOpenClearance: centre must rise diameter + 0.2.
            m_openPos = m_closedPos + Vector3.up * (diameter + CircularDoorPatch.kOpenClearance);
            m_state = 0;
            m_animT = 0f;
            m_farTimer = 0f;
        }

        void Update()
        {
            if (m_slab == null) return;
            PlayerShip ship = GameManager.m_player_ship;
            if (ship == null) return;
            float dist = Vector3.Distance(ship.transform.position, m_closedPos);

            if (m_state == 0) // closed
            {
                if (dist < CircularDoorPatch.kOpenDistance)
                {
                    m_state = 1;
                    m_animT = 0f;
                    Debug.Log("[CircularDoor] Opening slab at " + m_closedPos + ".");
                }
            }
            else if (m_state == 1) // opening
            {
                m_animT += Time.deltaTime / CircularDoorPatch.kSlideDuration;
                float t = SmoothStep(Mathf.Clamp01(m_animT));
                m_slab.transform.position = Vector3.Lerp(m_closedPos, m_openPos, t);
                if (m_animT >= 1f) { m_state = 2; m_farTimer = 0f; }
            }
            else if (m_state == 2) // open
            {
                if (dist > CircularDoorPatch.kCloseDistance)
                {
                    m_farTimer += Time.deltaTime;
                    if (m_farTimer >= CircularDoorPatch.kCloseDelay)
                    {
                        m_state = 3;
                        m_animT = 0f;
                        Debug.Log("[CircularDoor] Closing slab at " + m_closedPos + ".");
                    }
                }
                else
                {
                    m_farTimer = 0f;
                }
            }
            else // closing
            {
                // Player came back mid-close: reverse smoothly from here.
                if (dist < CircularDoorPatch.kOpenDistance)
                {
                    m_state = 1;
                    m_animT = 1f - Mathf.Clamp01(m_animT);
                    return;
                }
                m_animT += Time.deltaTime / CircularDoorPatch.kSlideDuration;
                float t = SmoothStep(Mathf.Clamp01(m_animT));
                m_slab.transform.position = Vector3.Lerp(m_openPos, m_closedPos, t);
                if (m_animT >= 1f) m_state = 0;
            }
        }

        private static float SmoothStep(float t)
        {
            return t * t * (3f - 2f * t);
        }
    }

    // D3 doors are not destroyed by shooting: swallow all damage on a
    // circular-door marker. Fail-open to stock on any exception.
    [HarmonyPatch(typeof(Destroyable), "ApplyDamage")]
    public static class CircularDoorDamagePatch
    {
        static bool Prefix(Destroyable __instance, DamageInfo di)
        {
            try
            {
                if (__instance != null
                    && __instance.m_explode_type == Destroyable.ExplodeType.SWITCH
                    && __instance.m_special_index == CircularDoorPatch.kCircularDoorSpecialIndex)
                {
                    Debug.Log("[CircularDoor] Swallowed damage on circular-door marker (doors are not destroyed by shooting).");
                    return false;
                }
            }
            catch (System.Exception ex)
            {
                Debug.Log("[CircularDoor] ApplyDamage prefix failed (fail open to stock): " + ex.ToString());
            }
            return true;
        }
    }
}
