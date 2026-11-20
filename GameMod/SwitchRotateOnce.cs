using HarmonyLib;
using Overload;
using System.Collections.Generic;
using UnityEngine;

namespace GameMod
{
    // Switch motion proof build (Nov 20, 2026): the D3 newswitch (mine_31)
    // rotates once when shot instead of exploding, and its function still
    // fires. Harmony prefix on Destroyable.ApplyDamage.
    //
    // Identification: the converter marks mine_31 carriers by setting
    // Destroyable.m_special_index = 31. That field is unused for
    // ExplodeType SWITCH -- the decompile reads m_special_index only for
    // REACTOR (Destroyable.ExplodeNow, L30193). mine_32 (hatch handle) and
    // every other Destroyable keep the default 0 and are untouched.
    //
    // On the FIRST shot to a marked switch:
    //   1. rotate the carrier's "moving" child 90 deg about its local +Y
    //      (the slab's protrusion axis = the twist axis; the moving pivot
    //      is ~ origin for newswitch) and leave it there;
    //   2. fire the Destroyable's triggered_on_death list with the same
    //      SendMessage("ActivateScriptLink") call Destroyable.ExplodeNow
    //      uses (L30231-30234), so the ON_DESTROY script fires
    //      FORCEFIELD_DISABLE / flames-off + FireDoor exactly as before;
    //   3. swallow the damage (return false) so the switch never explodes.
    // Later shots are swallowed silently (one-time switch). The rotation
    // is set absolutely (Quaternion.AngleAxis), so a re-activation (e.g.
    // after a save/load, when the in-memory set is empty) is idempotent,
    // and the ON_DESTROY / FORCEFIELD_DISABLE scripts gate themselves via
    // ScriptBase.did_activate.
    //
    // Without this patch the switch explodes as before (graceful
    // degradation -- the level does not depend on the patch to load).
    // If the "moving" child is missing, the prefix falls through to the
    // stock method (old explode behaviour) instead of breaking the switch.
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

            // 1. Twist once and stay. Absolute angle so a re-activation
            // (e.g. after a save/load) is idempotent.
            moving.localRotation = Quaternion.AngleAxis(kTwistDegrees, Vector3.up);
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
            Debug.Log("[SwitchRotateOnce] mine_31 switch activated: rotated 'moving' " + kTwistDegrees + " deg, fired " + linkCount + " on-destroy link(s).");
            // 3. Swallow the damage: no explosion, no destruction.
            return false;
        }
    }
}
