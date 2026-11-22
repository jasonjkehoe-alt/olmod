using HarmonyLib;
using Overload;
using System.Collections.Generic;
using UnityEngine;

namespace GameMod
{
    // Switch motion proof build (Nov 20, 2026), smooth twist (Nov 21):
    // the D3 newswitch (mine_31) rotates once when shot instead of
    // exploding, and its function still fires. Harmony prefix on
    // Destroyable.ApplyDamage.
    //
    // Identification: the converter marks mine_31 carriers by setting
    // Destroyable.m_special_index = 31. That field is unused for
    // ExplodeType SWITCH -- the decompile reads m_special_index only for
    // REACTOR (Destroyable.ExplodeNow, L30193). mine_32 (hatch handle) and
    // every other Destroyable keep the default 0 and are untouched.
    //
    // On the FIRST shot to a marked switch:
    //   1. the carrier's "moving" child twists 90 deg about its local +Y
    //      (the slab's protrusion axis = the twist axis; the moving pivot
    //      is ~ origin for newswitch) over ~0.75 s with a smoothstep
    //      ease-in-out (SwitchTwistAnimator), and stays there;
    //   2. fire the Destroyable's triggered_on_death list with the same
    //      SendMessage("ActivateScriptLink") call Destroyable.ExplodeNow
    //      uses (L30231-30234), so the ON_DESTROY script fires
    //      FORCEFIELD_DISABLE / flames-off + FireDoor exactly as before;
    //   3. swallow the damage (return false) so the switch never explodes.
    // Later shots are swallowed silently (one-time switch). The rotation
    // target is set absolutely (Quaternion.AngleAxis), so a re-activation
    // (e.g. after a save/load, when the in-memory set is empty) is
    // idempotent, and the ON_DESTROY / FORCEFIELD_DISABLE scripts gate
    // themselves via ScriptBase.did_activate.
    //
    // Fail-open: if the animator cannot start, the twist snaps instantly
    // (the switch must still end up twisted 90 deg). If the animator is
    // removed mid-twist, OnDestroy snaps to the target pose. Without
    // this patch the switch explodes as before (graceful degradation --
    // the level does not depend on the patch to load). If the "moving"
    // child is missing, the prefix falls through to the stock method
    // (old explode behaviour) instead of breaking the switch.
    // A patch exception fails open to stock behaviour for the same reason.
    [HarmonyPatch(typeof(Destroyable), "ApplyDamage")]
    public class Destroyable_ApplyDamage_SwitchRotateOnce
    {
        // Converter marker for mine_31 (newswitch) carriers.
        private const int kMine31SpecialIndex = 31;
        // D3 newswitch twist: ~90 deg about the slab's protrusion axis.
        private const float kTwistDegrees = 90f;

        private static readonly HashSet<int> s_activated = new HashSet<int>();

        static bool Prefix(Destroyable __instance, DamageInfo di)
        {
            try { return PrefixInner(__instance, di); }
            catch (System.Exception ex)
            {
                // Fail open to stock behaviour: a patch bug must never
                // break a switch (or any Destroyable) in flight.
                Debug.Log("[SwitchRotateOnce] prefix failed: " + ex.ToString());
                return true;
            }
        }

        static bool PrefixInner(Destroyable __instance, DamageInfo di)
        {
            if (__instance == null)
                return true;
            // Only mine_31 switches (marker + SWITCH type). Everything else
            // (mine_32 hatch, barrels, grates, reactors, secret panels)
            // runs the stock method untouched.
            if (__instance.m_special_index != kMine31SpecialIndex ||
                __instance.m_explode_type != Destroyable.ExplodeType.SWITCH)
                return true;

            int id = __instance.GetInstanceID();
            // Already activated: one-time switch, swallow further damage.
            if (s_activated.Contains(id))
                return false;

            Transform moving = __instance.transform.Find("moving");
            if (moving == null)
            {
                Debug.Log("[SwitchRotateOnce] mine_31 switch has no 'moving' child; falling through to stock explode.");
                return true;
            }

            // 1. Twist once and stay. Absolute target angle so a
            // re-activation (e.g. after a save/load) is idempotent.
            // Animated over ~0.75 s (smoothstep ease-in-out) via the
            // animator component; if it cannot start, snap instantly
            // (fail-open: the switch must end up twisted 90 deg).
            Quaternion target = Quaternion.AngleAxis(kTwistDegrees, Vector3.up);
            bool animated = false;
            try
            {
                SwitchTwistAnimator animator = __instance.gameObject.AddComponent<SwitchTwistAnimator>();
                animator.Begin(moving, target);
                animated = true;
            }
            catch (System.Exception ex)
            {
                Debug.Log("[SwitchRotateOnce] twist animator failed, snapping instantly: " + ex.ToString());
            }
            if (!animated)
                moving.localRotation = target;
            // 2. Fire the ON_DESTROY logic exactly as ExplodeNow does.
            List<ScriptBase> links = __instance.triggered_on_death;
            int linkCount = 0;
            if (links != null)
            {
                linkCount = links.Count;
                foreach (ScriptBase item in links)
                    item.SendMessage("ActivateScriptLink", null, SendMessageOptions.DontRequireReceiver);
            }
            s_activated.Add(id);
            Debug.Log("[SwitchRotateOnce] mine_31 switch activated: twisting 'moving' " + kTwistDegrees + " deg over 0.75 s, fired " + linkCount + " on-destroy link(s).");
            // 3. Swallow the damage: no explosion, no destruction.
            return false;
        }
    }

    // Animates one mine_31 switch twist: the "moving" child rotates from
    // its current rotation to the 90 deg twisted pose over kDuration
    // seconds (smoothstep ease-in-out: slow at both ends, fastest
    // mid-twist), snaps exactly to the target at the end, and removes
    // itself. Fail-open: if this component is destroyed before the
    // animation finishes (level unload, etc.), OnDestroy snaps the
    // switch to the target pose so it is never left half-twisted.
    public class SwitchTwistAnimator : MonoBehaviour
    {
        // Twist duration: inside Jason's 0.5-1 s window.
        private const float kDuration = 0.75f;

        private Transform m_moving;
        private Quaternion m_start;
        private Quaternion m_target;
        private float m_elapsed;
        private bool m_finished;

        public void Begin(Transform moving, Quaternion target)
        {
            m_moving = moving;
            m_target = target;
            m_start = moving.localRotation;
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
            m_moving.localRotation = Quaternion.Slerp(m_start, m_target, eased);
            if (u >= 1f)
            {
                m_moving.localRotation = m_target;
                m_finished = true;
                Destroy(this);
            }
        }

        void OnDestroy()
        {
            // Fail-open: never leave the switch half-twisted if this
            // component is removed before the animation completes.
            if (!m_finished && m_moving != null)
                m_moving.localRotation = m_target;
        }
    }
}
