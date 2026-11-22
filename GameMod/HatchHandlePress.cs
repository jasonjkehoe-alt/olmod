using HarmonyLib;
using Overload;
using System.Collections.Generic;
using UnityEngine;

namespace GameMod
{
    // Hatch handle press (Nov 22, 2026; Jason's decision): the fire-room
    // exterior hatch handle (mine_32, handleswitchsean.OOF, entity_mine_32,
    // outside the FireDoor, [530.57, 52.28, 618.20], seg 257) must NOT
    // explode when shot -- it presses DOWN and triggers the FireDoor.
    // Follows SwitchRotateOnce.cs exactly (mine_31 twist): Harmony prefix
    // on Destroyable.ApplyDamage, animate instead of exploding, fire the
    // Destroyable's triggered_on_death list with the same
    // SendMessage("ActivateScriptLink") call Destroyable.ExplodeNow uses,
    // swallow the damage.
    //
    // Identification (CORRECTED Nov 22, route-resync fix): the handle
    // is the SWITCH Destroyable that is NOT a mine_31 switch -- the
    // converter marks every mine_31 with m_special_index = 31 and
    // leaves mine_32 at the default 0, and the deployed .sp (parsed,
    // 78b6f162) proves the handle is the ONLY SWITCH Destroyable with
    // m_special_index 0 (the three mine_31 have 31; every other
    // Destroyable is ALIEN_POD or REACTOR, never SWITCH). This is the
    // exact complement of SwitchRotateOnce (which fires on
    // m_special_index == 31), so the two patches never both fire.
    // The previous build identified the handle by prefab name
    // (entity_mine_32) -- WRONG: the runtime GameObject name is
    // entity_prop_n0000_mine for EVERY prop carrier (the .sp
    // GameObject name, verified by parsing the deployed .sp), so
    // StartsWith("entity_mine_32") never matched and the patch never
    // fired (0 [HatchHandlePress] lines in the flight log). Every
    // mine_32 prop IS a hatch handle by definition
    // (HANDLE_SWITCH_MINE_INDEX = 32), so the rule is general, not
    // L1-specific; no converter change and no reconvert were needed.
    // Every SWITCH intercept is logged (only 4 SWITCH Destroyables
    // in L1: the 3 mine_31 + the handle) so a silent 0-line flight
    // log can never happen again.
    //
    // Animation: the "moving" child (the lever, one rigid part -- the
    // same single-"moving"-child prefab as newswitch) translates DOWN
    // 0.18 m over 0.75 s, smoothstep ease-in-out (HatchPressAnimator,
    // mirroring SwitchTwistAnimator), and stays pressed (latched). Down
    // is world -Y converted into the carrier's local frame, so the press
    // reads as down whatever the handle's wall orientation.
    //
    // Fail-open twice: if the "moving" child is missing, the prefix falls
    // through to the stock method (old explode behaviour) instead of
    // breaking the switch; if the animator cannot start, the press snaps
    // instantly (the handle must still end up pressed). A patch exception
    // fails open to stock behaviour for the same reason.
    [HarmonyPatch(typeof(Destroyable), "ApplyDamage")]
    public class Destroyable_ApplyDamage_HatchHandlePress
    {
        // The mine_31 marker (SwitchRotateOnce.cs). The handle is a
        // SWITCH whose m_special_index is NOT this value.
        private const int kMine31SpecialIndex = 31;
        // Press depth: reads clearly on the lever without burying it.
        private const float kPressDistance = 0.18f;

        private static readonly HashSet<int> s_activated = new HashSet<int>();

        static bool Prefix(Destroyable __instance, DamageInfo di)
        {
            try { return PrefixInner(__instance, di); }
            catch (System.Exception ex)
            {
                // Fail open to stock behaviour: a patch bug must never
                // break a switch (or any Destroyable) in flight.
                Debug.Log("[HatchHandlePress] prefix failed: " + ex.ToString());
                return true;
            }
        }

        static bool PrefixInner(Destroyable __instance, DamageInfo di)
        {
            if (__instance == null)
                return true;
            // Log every SWITCH intercept (only 4 SWITCH Destroyables in
            // L1: the 3 mine_31 + the handle) so the log proves the patch
            // is live and shows which one was hit. Non-SWITCH
            // Destroyables (barrels, grates, reactors, panels) are not
            // logged -- they are numerous and irrelevant here.
            bool isSwitch = __instance.m_explode_type == Destroyable.ExplodeType.SWITCH;
            if (isSwitch)
            {
                Debug.Log("[HatchHandlePress] SWITCH intercept: m_special_index=" +
                    __instance.m_special_index + " at " + __instance.transform.position);
            }
            // Only the mine_32 hatch handle: a SWITCH that is NOT a
            // mine_31. Everything else (mine_31 switches -- left to
            // SwitchRotateOnce -- barrels, grates, reactors, secret
            // panels) runs the stock method untouched.
            if (!isSwitch || __instance.m_special_index == kMine31SpecialIndex)
                return true;

            int id = __instance.GetInstanceID();
            // Already activated: one-time switch, swallow further damage.
            if (s_activated.Contains(id))
                return false;

            Transform moving = __instance.transform.Find("moving");
            if (moving == null)
            {
                Debug.Log("[HatchHandlePress] hatch handle has no 'moving' child; falling through to stock explode.");
                return true;
            }

            // 1. Press down once and stay. Target is absolute (start +
            // world-down in carrier-local space), so a re-activation
            // (e.g. after a save/load, when the in-memory set is empty)
            // is idempotent. Animated over ~0.75 s (smoothstep
            // ease-in-out) via the animator component; if it cannot
            // start, snap instantly (fail-open: the handle must still
            // end up pressed).
            Vector3 downLocal = __instance.transform.InverseTransformDirection(Vector3.down);
            Vector3 target = moving.localPosition + downLocal * kPressDistance;
            bool animated = false;
            try
            {
                HatchPressAnimator animator = __instance.gameObject.AddComponent<HatchPressAnimator>();
                animator.Begin(moving, target);
                animated = true;
            }
            catch (System.Exception ex)
            {
                Debug.Log("[HatchHandlePress] press animator failed, snapping instantly: " + ex.ToString());
            }
            if (!animated)
                moving.localPosition = target;
            // 2. Fire the ON_DESTROY logic exactly as ExplodeNow does
            // (link0 = hatch switch, link1 = flames DEACTIVATE, link2 =
            // FireDoor UNLOCK, link3 = FireDoor OPEN, link4 = Emergency
            // Hatch comm -- the converter's ON_DESTROY script wiring).
            List<ScriptBase> links = __instance.triggered_on_death;
            int linkCount = 0;
            if (links != null)
            {
                linkCount = links.Count;
                foreach (ScriptBase item in links)
                    item.SendMessage("ActivateScriptLink", null, SendMessageOptions.DontRequireReceiver);
            }
            s_activated.Add(id);
            Debug.Log("[HatchHandlePress] hatch handle activated at " + __instance.transform.position +
                ": pressing 'moving' down " + kPressDistance + " m over 0.75 s, fired " + linkCount + " on-destroy link(s).");
            // 3. Swallow the damage: no explosion, no destruction.
            return false;
        }
    }

    // Animates one hatch-handle press: the "moving" child translates
    // from its current position to the pressed pose over kDuration
    // seconds (smoothstep ease-in-out: slow at both ends, fastest
    // mid-press), snaps exactly to the target at the end, and removes
    // itself. Fail-open: if this component is destroyed before the
    // animation finishes (level unload, etc.), OnDestroy snaps the
    // handle to the target pose so it is never left half-pressed.
    public class HatchPressAnimator : MonoBehaviour
    {
        // Press duration: the same 0.75 s as the switch twist.
        private const float kDuration = 0.75f;

        private Transform m_moving;
        private Vector3 m_start;
        private Vector3 m_target;
        private float m_elapsed;
        private bool m_finished;

        public void Begin(Transform moving, Vector3 target)
        {
            m_moving = moving;
            m_target = target;
            m_start = moving.localPosition;
            m_elapsed = 0f;
            m_finished = false;
        }

        void Update()
        {
            if (m_moving == null)
            {
                Destroy(this);
                return;
            }
            m_elapsed += Time.deltaTime;
            float u = Mathf.Clamp01(m_elapsed / kDuration);
            // Smoothstep ease-in-out.
            float eased = u * u * (3f - 2f * u);
            m_moving.localPosition = Vector3.Lerp(m_start, m_target, eased);
            if (u >= 1f)
            {
                m_moving.localPosition = m_target;
                m_finished = true;
                Destroy(this);
            }
        }

        void OnDestroy()
        {
            // Fail-open: never leave the handle half-pressed if this
            // component is removed before the animation completes.
            if (!m_finished && m_moving != null)
                m_moving.localPosition = m_target;
        }
    }
}
