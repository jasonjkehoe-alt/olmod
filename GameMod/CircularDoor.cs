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
// LOCK (Nov 23, Step 3 fix option A -- D3 DF_LOCKED model, see
// MATCEN_UPDATE_NOV23.md section 2.1): the controller carries a
// `Locked` flag (the DF_LOCKED equivalent), consulted in the
// proximity-open decision -- a locked slab never opens on player
// proximity. The lock does NOT force-close by itself (D3's
// DoorwayLockUnlock does not move the door); the separate ForceClose()
// is the DoorwaySetPosition equivalent, called by the lock path to shut
// an already-open slab (stock ScriptDoorLock does exactly this:
// LockType = LOCKED + ForceClose() on a DoorAnimating).
// Bridge: Overload's door scripts speak only DoorBase
// (ScriptDoorLock/Unlock GetComponentInChildren<DoorBase>), and the
// slab has none -- so a Harmony postfix on ScriptDoorLock.ActivateScript
// (and ScriptDoorUnlock) translates to the controller: the postfix
// finds the CircularDoorController on each linked carrier GO and sets
// the flag (+ force-close on lock). A DoorBase adapter was rejected:
// DoorBase is abstract with engine-side registration (segment, renderers,
// chunking) and the adapter would NOT be a DoorAnimating, so stock
// ForceClose would never fire on it -- the postfix is smaller and exact.
// GB-IGNORE (D3 DF_GB_IGNORE_LOCKED): the GuideBot routes through locked
// doors by design -- its key route goes through rooms 23/24. A locked
// slab still opens on GuideBot proximity (Robot.m_guidebot); the player
// alone can never open it. The player could follow the bot through while
// it is open for the bot -- the same trade-off D3 accepts.
//
// Fail-open: no marker in the .sp (the prop lands with the later batch
// reconvert) -> nothing is built and the level loads unchanged. A marker
// with an invalid diameter, or any build error, is logged and skipped.
// The marker's damage is swallowed (D3 doors are not destroyed by
// shooting), via the ApplyDamage prefix below. If the lock state is
// unreadable (no controller on a linked GO, no GuideBot yet, any
// exception in the postfix), the slab behaves exactly as it does today.

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
    // LOCK (option A): Locked is the D3 DF_LOCKED equivalent -- a locked
    // slab never opens on player proximity (checked in ShouldOpen).
    // ForceClose() is the separate D3 DoorwaySetPosition equivalent:
    // drive the slab to position 0 from wherever it is.
    public class CircularDoorController : MonoBehaviour
    {
        private GameObject m_slab;
        private Vector3 m_closedPos;
        private Vector3 m_openPos;
        private float m_animT;
        private int m_state; // 0 closed, 1 opening, 2 open, 3 closing
        private float m_farTimer;

        // D3 DF_LOCKED equivalent. Set only via SetLocked (script bridge).
        public bool Locked { get; private set; }

        public Vector3 ClosedPosition { get { return m_closedPos; } }

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
            Locked = false;
        }

        // D3 DoorwayLockUnlock equivalent: set the flag only, do not move
        // the slab. The lock path (ScriptDoorLock postfix) calls
        // ForceClose() separately, mirroring stock ScriptDoorLock
        // (LockType = LOCKED + ForceClose() on a DoorAnimating).
        public void SetLocked(bool locked)
        {
            if (Locked == locked) return;
            Locked = locked;
            Debug.Log("[CircularDoor] " + (locked ? "Locked" : "Unlocked")
                + " slab at " + m_closedPos + ".");
        }

        // D3 DoorwaySetPosition equivalent: drive the slab to position 0
        // (closed) from wherever it is, reversing a mid-open smoothly.
        // No-op when already closed. Does not check for a ship in the
        // doorway (D3's auto-close does; ours does not -- small polish
        // gap, same as a stock DoorAnimating ForceClose()).
        public void ForceClose()
        {
            if (m_slab == null) return;
            if (m_state == 0) return;
            float travel = (m_openPos - m_closedPos).magnitude;
            float f = 0f;
            if (travel > 0.001f)
            {
                f = Mathf.Clamp01(Vector3.Dot(m_slab.transform.position - m_closedPos, Vector3.up) / travel);
            }
            if (f <= 0.001f)
            {
                m_slab.transform.position = m_closedPos;
                m_state = 0;
                m_animT = 0f;
                m_farTimer = 0f;
                return;
            }
            // Closing lerps Lerp(open, closed, t): the current position
            // (fraction f from closed) is t = 1 - f.
            m_state = 3;
            m_animT = 1f - f;
            m_farTimer = 0f;
            Debug.Log("[CircularDoor] Force-closing slab at " + m_closedPos + ".");
        }

        // D3 DF_GB_IGNORE_LOCKED equivalent: the GuideBot routes through
        // locked doors by design. Fail-open: no bot (null / destroyed /
        // any exception) means "no bot near", stock behaviour.
        private bool GuideBotNear()
        {
            try
            {
                Robot bot = Robot.m_guidebot;
                if (bot == null) return false;
                return Vector3.Distance(bot.transform.position, m_closedPos)
                    < CircularDoorPatch.kOpenDistance;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // The open decision (D3 DoorwayOpenable equivalent): the player
        // opens only when unlocked; the GuideBot opens regardless of the
        // lock. A locked slab never opens on player proximity alone.
        private bool ShouldOpen(float playerDist)
        {
            bool playerNear = playerDist < CircularDoorPatch.kOpenDistance;
            if (playerNear && !Locked) return true;
            return GuideBotNear();
        }

        void Update()
        {
            if (m_slab == null) return;
            PlayerShip ship = GameManager.m_player_ship;
            if (ship == null) return;
            float dist = Vector3.Distance(ship.transform.position, m_closedPos);

            if (m_state == 0) // closed
            {
                if (ShouldOpen(dist))
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
                // Locked slabs do not reverse for the player, only for
                // the GuideBot (ShouldOpen encodes both rules).
                if (ShouldOpen(dist))
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

    // Step 3 lock bridge (option A): the door scripts speak only DoorBase
    // (ScriptDoorLock/Unlock do GetComponentInChildren<DoorBase> on each
    // linked GO; a circular carrier has none, so stock silently skips
    // it). These postfixes translate the script to the slab: for each
    // linked GO that carries a CircularDoorController (i.e. a circular
    // marker), drive the controller. Chosen over a DoorBase adapter:
    // DoorBase is abstract with engine-side registration (segment,
    // renderers, chunking) and an adapter would not be a DoorAnimating,
    // so stock ForceClose() would never fire on it -- the postfix calls
    // the controller's lock + force-close directly, and touches nothing
    // in the engine's door system. Fail-open everywhere: a linked GO
    // without a controller is ignored (stock behaviour), and any
    // exception leaves the slab exactly as it is.
    //
    // Semantics mirror stock exactly:
    // - Lock (DOOR_LOCK): SetLocked(true) + ForceClose() -- stock
    //   ScriptDoorLock sets LockType = LOCKED and calls ForceClose()
    //   on a DoorAnimating. This is the login sequence's close: the
    //   slab shuts and stays shut.
    // - Unlock (DOOR_CLOSE, which the editor maps to
    //   entity_script_door_unlock): SetLocked(false) only -- stock
    //   ScriptDoorUnlock sets LockType = NONE and does not move the
    //   door. NOTE the DOOR_CLOSE entities never fire in the base game
    //   either: ScriptDoorLock never forwards ActivateScriptLink to its
    //   link1/link3, so they are dormant wiring kept for completeness;
    //   the actual close in the login sequence is the lock's
    //   ForceClose() above, not these scripts.
    [HarmonyPatch(typeof(ScriptDoorLock), "ActivateScript")]
    public static class CircularDoorLockPatch
    {
        static void Postfix(ScriptDoorLock __instance)
        {
            try
            {
                if (__instance == null) return;
                foreach (GameObject go in __instance.c_go_link)
                {
                    if (go == null) continue;
                    CircularDoorController ctl = go.GetComponent<CircularDoorController>();
                    if (ctl == null) continue;
                    ctl.SetLocked(true);
                    ctl.ForceClose();
                    Debug.Log("[CircularDoor] Lock script locked + force-closed slab at "
                        + ctl.ClosedPosition + ".");
                }
            }
            catch (System.Exception ex)
            {
                Debug.Log("[CircularDoor] ScriptDoorLock postfix failed (fail open, slab unchanged): " + ex.ToString());
            }
        }
    }

    [HarmonyPatch(typeof(ScriptDoorUnlock), "ActivateScript")]
    public static class CircularDoorUnlockPatch
    {
        static void Postfix(ScriptDoorUnlock __instance)
        {
            try
            {
                if (__instance == null) return;
                foreach (GameObject go in __instance.c_go_link)
                {
                    if (go == null) continue;
                    CircularDoorController ctl = go.GetComponent<CircularDoorController>();
                    if (ctl == null) continue;
                    ctl.SetLocked(false);
                    Debug.Log("[CircularDoor] Unlock script unlocked slab at "
                        + ctl.ClosedPosition + ".");
                }
            }
            catch (System.Exception ex)
            {
                Debug.Log("[CircularDoor] ScriptDoorUnlock postfix failed (fail open, slab unchanged): " + ex.ToString());
            }
        }
    }
}
