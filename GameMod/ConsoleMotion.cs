using HarmonyLib;
using Overload;
using System.Collections.Generic;
using UnityEngine;

namespace GameMod
{
    // Data console motion (Nov 21, 2026): the D3 consoleturnonswitch
    // (mine_30) animates when touched — the arm tilts 22 deg, the two
    // paddles flip 180 deg each, and the arm pulls back ~0.65 m over 4 s,
    // once, latched — and a light switches on as the handle goes up.
    //
    // The D3 tracks come from consoleturnonswitch.OOF (RANI/PANI,
    // aObjPlayAnim frames 0-7, 4.0 s): sm1 rotates 22 deg about +X over
    // t=0-0.5 s and slides back 0.647 m over t=2.0-2.5 s; sm4 flips 180 deg
    // about the diagonal axis (0, 0.375, 0.927) over t=0.5-1.0 s and
    // slides back 0.259 m over t=1.0-1.5 s; sm3 flips 180 deg about the
    // same axis over t=1.0-1.5 s and slides back 0.285 m over t=1.5-2.0 s
    // (then returns to ~0 by t=3.0 s); sm2 slides back 0.292 m over
    // t=3.0-3.5 s. All values are the exact .oof deltas (x0.2 m scale).
    // The RANI axes are in the submodel's local frame (= the parent's
    // frame) and the PANI deltas are in the parent's frame, so
    // child.localRotation = Quaternion.AngleAxis(angle, axis) and
    // child.localPosition = base + paniDelta reproduce D3 exactly on the
    // nested moving_sm1 -> moving_sm2 -> moving_sm3/moving_sm4 chain
    // (the Tier 2 poc re-export preserves the D3 parent chain).
    //
    // The trigger fires the comms (the .sp wires TRIGGER:BOX -> comms).
    // This patch adds the animation + light on the same fire, identified
    // by PROXIMITY (the trigger box centre is ~0.89 m from the console
    // carrier origin; the three consoles are ~70 m apart, so there is no
    // ambiguity). No converter change, no .sp change, no hardcoded
    // positions. Fail-open: any exception logs and the console stays
    // static (the comms still fire).
    [HarmonyPatch(typeof(TriggerBase), "OnTrigger")]
    public class TriggerBase_OnTrigger_ConsoleMotion
    {
        private const float kSearchRadius = 2.0f;
        private static readonly HashSet<int> s_activated = new HashSet<int>();

        static void Prefix(TriggerBase __instance, Collider other, bool ___m_has_triggered)
        {
            try { PrefixInner(__instance, other, ___m_has_triggered); }
            catch (System.Exception ex)
            {
                Debug.Log("[ConsoleMotion] prefix failed: " + ex.ToString());
            }
        }

        static void PrefixInner(TriggerBase __instance, Collider other, bool hasTriggered)
        {
            if (__instance == null || other == null)
                return;
            // Replicate TriggerBase.OnTrigger's fire condition, so the
            // animation starts only when the trigger actually fires: a
            // one-time trigger that already fired, or a weapon hitting a
            // touch-only box, must not animate. (m_has_triggered is
            // persisted across save/load, so this also prevents a
            // re-animation when the player touches an already-used
            // console after a reload.)
            if ((__instance.m_one_time && hasTriggered) ||
                (other.gameObject.layer == 12 && !__instance.m_player_weapons))
                return;
            Transform carrier = FindConsoleCarrier(__instance.transform.position);
            if (carrier == null)
                return;
            int id = carrier.GetInstanceID();
            if (s_activated.Contains(id))
                return;
            ConsoleMotion motion = carrier.gameObject.AddComponent<ConsoleMotion>();
            if (motion == null)
                return;
            s_activated.Add(id);
            // Steady-glow boost: the touched console's screen gets
            // brighter/redder (ConsoleGlow, below). If the glow was
            // never applied (no materials found at level start), this
            // logs and the flip still runs.
            ConsoleGlow.BoostOnTouch(carrier);
            Debug.Log("[ConsoleMotion] console activated at " + carrier.position);
        }

        static Transform FindConsoleCarrier(Vector3 triggerPos)
        {
            // The console carriers are the parents of the distinctive
            // moving_sm1 children (Tier 2 poc). FindObjectsOfType returns
            // scene objects only (not prefab assets), so no scene check
            // is needed. This runs once per console touch (3 max per
            // level), so no cache is kept — a cache would go stale on
            // level reload.
            Transform[] all = Object.FindObjectsOfType<Transform>();
            Transform best = null;
            float bestDist = kSearchRadius;
            foreach (Transform t in all)
            {
                if (t == null || t.name != "moving_sm1" || t.parent == null)
                    continue;
                Transform carrier = t.parent;
                float d = Vector3.Distance(triggerPos, carrier.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = carrier;
                }
            }
            return best;
        }
    }

    // Login-console steady glow (Nov 22, 2026; Jason's decision): the
    // console screen should be LIT all the time (it is a console, the
    // screen is supposed to be on) -- and on touch it gets BRIGHTER /
    // turns RED. Jason will check what original Descent 3 does and may
    // correct this; built as proposed for now.
    //
    // The console prop (consoleturnonswitch.OOF, entity_mine_30) wears a
    // single texture, "p-she strip3", on every part (static + moving_sm1-4
    // -- prop_switch_manifest.json), so there is no separate screen
    // material: the steady glow is a moderate warm emission on the
    // console's own material(s), which reads as the screen being on.
    // Technique: the TextureMotionManager lesson (Nov 21 bisect) -- NEVER
    // touch r.materials (it instantiates + renames every material
    // "X (Instance)" and breaks the game's exact-name glow matching).
    // Scan r.sharedMaterials read-only, instantiate ONLY the matched
    // material by hand (new Material), name preserved, installed via
    // sharedMaterials, _EmissionMap repointed at _MainTex (the stock map
    // is black, so colour alone would emit black) with a moderate steady
    // _EmissionColor. p-she strip3 is NOT a scrolling texture
    // (texturemotion.txt: datascroll, energycenter1, pillar), so the
    // TextureMotion scan never wraps these instances.
    //
    // Hook: postfix on GameplayManager.StartLevel (same pattern as
    // TextureMotionManager) -- by then the level is loaded and the
    // renderers/materials exist. The touch boost is applied from the
    // trigger prefix above, so the flip and the brightening fire
    // together. Fail-open: if no console / no material is found, log
    // and continue; the console flip (ConsoleMotion) is untouched and
    // still works.
    [HarmonyPatch(typeof(GameplayManager), "StartLevel")]
    public class GameplayManager_StartLevel_ConsoleGlow
    {
        static void Postfix()
        {
            try { ConsoleGlow.ApplySteadyGlow(); }
            catch (System.Exception ex)
            {
                Debug.Log("[ConsoleGlow] StartLevel postfix failed: " + ex.ToString());
            }
        }
    }

    public static class ConsoleGlow
    {
        // Steady: warm amber at room level -- the screen reads ON
        // without washing out the texture. Touch: brighter AND redder.
        // (Nov 22, route-resync fix: the 0.45/0.36/0.22 steady value
        // was INVISIBLE in flight -- 7 [ConsoleGlow] lines, consoles
        // dark until touched -- while the 1.5/0.35/0.10 touch boost
        // was clearly visible. Raised to 1.0/0.8/0.5 warm amber: a
        // clearly visible screen-on level, still below the touch
        // boost's 1.5 peak red. Jason will check what original D3 does
        // and may correct the colour -- this constant is the one line
        // to change.)
        private static readonly Color kSteadyEmission = new Color(1.0f, 0.8f, 0.5f);
        private static readonly Color kTouchEmission = new Color(1.5f, 0.35f, 0.10f);
        // Steady light: the touch state is bright because ConsoleMotion
        // creates a real PointLight; the steady glow was emission-only
        // on a mid-grey texture, so no emission value could light the
        // console (investigation Nov 22, console-glow-why-dark.md).
        // A dim steady PointLight (same position/colour/range as the
        // touch light, intensity 0.5 vs the touch 1.5) lights the
        // dome/base/panels and the floor, and makes the cyan
        // "LOGON..." screen read clearly. One-line change if Jason
        // wants it brighter/dimmer. Do NOT raise kSteadyEmission
        // again -- the value was never the lever.
        private const float kSteadyLightIntensity = 0.5f;
        internal const string kSteadyLightName = "console_steady_light";

        // Carrier instance ID -> installed glow material instances.
        private static readonly Dictionary<int, List<Material>> s_glowByCarrier = new Dictionary<int, List<Material>>();

        public static void ApplySteadyGlow()
        {
            s_glowByCarrier.Clear();
            Transform[] all = Object.FindObjectsOfType<Transform>();
            int consoles = 0, materials = 0;
            foreach (Transform t in all)
            {
                if (t == null || t.name != "moving_sm1" || t.parent == null)
                    continue;
                Transform carrier = t.parent;
                int id = carrier.GetInstanceID();
                if (s_glowByCarrier.ContainsKey(id))
                    continue;
                List<Material> installed = new List<Material>();
                Renderer[] renderers = carrier.GetComponentsInChildren<Renderer>();
                foreach (Renderer r in renderers)
                {
                    if (r == null)
                        continue;
                    Material[] shared = r.sharedMaterials;
                    for (int i = 0; i < shared.Length; i++)
                    {
                        Material sharedMat = shared[i];
                        if (sharedMat == null || sharedMat.mainTexture == null)
                            continue;
                        if (!sharedMat.HasProperty("_EmissionColor") || !sharedMat.HasProperty("_EmissionMap"))
                            continue;
                        // Hand-instantiate ONLY this material (name
                        // preserved, no " (Instance)" suffix) and install
                        // it via sharedMaterials -- the game's own idiom.
                        Material m = new Material(sharedMat);
                        m.name = sharedMat.name;
                        m.SetTexture("_EmissionMap", m.mainTexture);
                        m.SetColor("_EmissionColor", kSteadyEmission);
                        Material[] arr = r.sharedMaterials;
                        arr[i] = m;
                        r.sharedMaterials = arr;
                        installed.Add(m);
                        materials++;
                    }
                }
                if (installed.Count > 0)
                {
                    s_glowByCarrier[id] = installed;
                    consoles++;
                    Debug.Log("[ConsoleGlow] steady glow on console at " + carrier.position + " (" + installed.Count + " material(s))");
                    ApplySteadyLight(carrier);
                }
            }
            if (consoles == 0)
                Debug.Log("[ConsoleGlow] no consoles found for steady glow.");
            else
                Debug.Log("[ConsoleGlow] steady glow applied to " + consoles + " console(s), " + materials + " material(s).");
        }

        // Steady PointLight: the same runtime light the touch uses
        // (ConsoleMotion), but dim (0.5) and on from level start.
        // Child of the carrier so it moves with the console. If a
        // steady light is already present (level reloaded / called
        // twice), reuse it instead of creating a second one.
        // Fail-open: a light failure logs and the console still works.
        private static void ApplySteadyLight(Transform carrier)
        {
            try
            {
                if (carrier == null)
                    return;
                Transform existing = carrier.Find(kSteadyLightName);
                if (existing != null)
                {
                    Light existingLight = existing.GetComponent<Light>();
                    if (existingLight != null)
                    {
                        existingLight.intensity = kSteadyLightIntensity;
                        Debug.Log("[ConsoleGlow] steady light already on console at " + carrier.position + " (intensity " + kSteadyLightIntensity + ").");
                        return;
                    }
                }
                GameObject lightGO = new GameObject(kSteadyLightName);
                lightGO.transform.SetParent(carrier, false);
                lightGO.transform.localPosition = ConsoleMotion.kLightLocalPos;
                Light light = lightGO.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = ConsoleMotion.kLightColor;
                light.range = ConsoleMotion.kLightRange;
                light.intensity = kSteadyLightIntensity;
                Debug.Log("[ConsoleGlow] steady light on console at " + carrier.position + " (intensity " + kSteadyLightIntensity + ").");
            }
            catch (System.Exception ex)
            {
                Debug.Log("[ConsoleGlow] steady light failed: " + ex.ToString());
            }
        }

        public static void BoostOnTouch(Transform carrier)
        {
            if (carrier == null)
                return;
            List<Material> mats;
            if (!s_glowByCarrier.TryGetValue(carrier.GetInstanceID(), out mats) || mats == null)
            {
                Debug.Log("[ConsoleGlow] touch boost: no steady-glow materials recorded for console at " + carrier.position + "; flip still runs.");
                return;
            }
            int n = 0;
            foreach (Material m in mats)
            {
                if (m == null)
                    continue;
                if (m.HasProperty("_EmissionColor"))
                {
                    m.SetColor("_EmissionColor", kTouchEmission);
                    n++;
                }
            }
            Debug.Log("[ConsoleGlow] touch boost on console at " + carrier.position + " (" + n + " material(s) brighter/redder).");
        }
    }

    // The per-console animator. Added to the console carrier when its
    // trigger fires. Animates the four moving children over 4 s per the
    // D3 RANI/PANI tracks, latches the final pose, and switches on a
    // light as the handle goes up. Disables itself when done (the pose
    // persists — the transforms keep their final values, and the light
    // stays on). One-shot: the MonoBehaviour is added once per carrier
    // (guarded by s_activated above) and never re-runs.
    public class ConsoleMotion : MonoBehaviour
    {
        private const float kDuration = 4.0f;
        private const float kLightRampTime = 0.5f;
        private const float kLightIntensity = 1.5f;
        internal const float kLightRange = 6.0f;
        internal static readonly Color kLightColor = new Color(1.0f, 0.95f, 0.85f);
        internal static readonly Vector3 kLightLocalPos = new Vector3(0f, 0.5f, 1.0f);
        // D3 flip axis (RANI sm3/sm4): (0, 0.3746, 0.9272), normalized.
        private static readonly Vector3 kFlipAxis = new Vector3(0f, 0.3746f, 0.9272f).normalized;
        private static readonly Vector3 kTiltAxis = Vector3.right; // sm1: +X

        // Rotation keyframes: (time, angleDeg). sm1 tilts 22 deg (D3 RANI
        // 4004 = 21.99 deg) over t=0-0.5 s; sm4 flips 180 deg over
        // t=0.5-1.0 s; sm3 flips 180 deg over t=1.0-1.5 s. sm2 has no
        // rotation. A single (time, angle) pair per segment is enough:
        // each rotation is one continuous move between two keyframes.
        private static readonly float[] kSm1RotTimes = { 0f, 0.5f };
        private static readonly float[] kSm1RotAngles = { 0f, 22f };
        private static readonly float[] kSm3RotTimes = { 0f, 1.0f, 1.5f };
        private static readonly float[] kSm3RotAngles = { 0f, 0f, 180f };
        private static readonly float[] kSm4RotTimes = { 0f, 0.5f, 1.0f };
        private static readonly float[] kSm4RotAngles = { 0f, 0f, 180f };

        // Position keyframes: (time, offset). Offset is added to the
        // child's base localPosition (the D3 parent-relative offset from
        // the poc). sm1 slides back 0.647 m over t=2.0-2.5 s; sm2 slides
        // back 0.292 m over t=3.0-3.5 s; sm3 slides back 0.285 m over
        // t=1.5-2.0 s then returns to ~0 by t=3.0 s; sm4 slides back
        // 0.259 m over t=1.0-1.5 s then settles at (0, 0.009, 0.023) by
        // t=3.0 s. (Exact .oof PANI deltas, x0.2 m.)
        private static readonly float[] kSm1PosTimes = { 0f, 2.0f, 2.5f };
        private static readonly Vector3[] kSm1PosOffsets = {
            Vector3.zero, Vector3.zero, new Vector3(0f, 0f, -0.6474f) };
        private static readonly float[] kSm2PosTimes = { 0f, 3.0f, 3.5f };
        private static readonly Vector3[] kSm2PosOffsets = {
            Vector3.zero, Vector3.zero, new Vector3(0f, -0.1181f, -0.2923f) };
        private static readonly float[] kSm3PosTimes = { 0f, 1.5f, 2.0f, 3.0f };
        private static readonly Vector3[] kSm3PosOffsets = {
            Vector3.zero, Vector3.zero,
            new Vector3(0f, -0.1150f, -0.2848f),
            new Vector3(0f, -0.0009f, -0.0023f) };
        private static readonly float[] kSm4PosTimes = { 0f, 1.0f, 1.5f, 3.0f };
        private static readonly Vector3[] kSm4PosOffsets = {
            Vector3.zero, Vector3.zero,
            new Vector3(0f, -0.1047f, -0.2590f),
            new Vector3(0f, 0.0095f, 0.0234f) };

        private Transform m_sm1, m_sm2, m_sm3, m_sm4;
        private Vector3 m_sm1Base, m_sm2Base, m_sm3Base, m_sm4Base;
        private Light m_light;
        private float m_lightStartIntensity;
        private float m_time;

        void Start()
        {
            try
            {
                m_sm1 = transform.Find("moving_sm1");
                if (m_sm1 == null)
                    throw new System.Exception("moving_sm1 not found");
                m_sm2 = m_sm1.Find("moving_sm2");
                if (m_sm2 == null)
                    throw new System.Exception("moving_sm2 not found");
                m_sm3 = m_sm2.Find("moving_sm3");
                m_sm4 = m_sm2.Find("moving_sm4");
                if (m_sm3 == null || m_sm4 == null)
                    throw new System.Exception("moving_sm3/moving_sm4 not found");
                m_sm1Base = m_sm1.localPosition;
                m_sm2Base = m_sm2.localPosition;
                m_sm3Base = m_sm3.localPosition;
                m_sm4Base = m_sm4.localPosition;
                // The light: a runtime PointLight, child of the carrier
                // root, above the arm and 1 m out (local +Z is the arm
                // direction; local Y is ~world up). Warm white, range
                // 6 m, ramps to 1.5 over the first 0.5 s (as the handle
                // tilts up), then holds full. No .sp change — the light
                // is created here, at trigger time.
                //
                // Reuse the steady light if ApplySteadyGlow already
                // created one (console_steady_light, intensity 0.5 from
                // level start): raise THAT light to 1.5 instead of
                // creating a second PointLight. The ramp starts from
                // the light's current intensity, so a steady light
                // brightens 0.5 -> 1.5 with no dip. If no steady light
                // exists (console added after level start), create
                // console_light as before.
                Transform steady = transform.Find(ConsoleGlow.kSteadyLightName);
                if (steady != null)
                {
                    m_light = steady.GetComponent<Light>();
                    if (m_light != null)
                    {
                        m_lightStartIntensity = m_light.intensity;
                        Debug.Log("[ConsoleMotion] reusing steady light on console at " + transform.position + " (raising " + m_lightStartIntensity + " -> " + kLightIntensity + ").");
                    }
                }
                if (m_light == null)
                {
                    GameObject lightGO = new GameObject("console_light");
                    lightGO.transform.SetParent(transform, false);
                    lightGO.transform.localPosition = kLightLocalPos;
                    m_light = lightGO.AddComponent<Light>();
                    m_light.type = LightType.Point;
                    m_light.color = kLightColor;
                    m_light.range = kLightRange;
                    m_light.intensity = 0f;
                    m_lightStartIntensity = 0f;
                }
                m_time = 0f;
            }
            catch (System.Exception ex)
            {
                Debug.Log("[ConsoleMotion] Start failed: " + ex.ToString());
                enabled = false;
            }
        }

        void Update()
        {
            try
            {
                m_time += Time.deltaTime;
                float t = Mathf.Min(m_time, kDuration);
                // Rotations: single-axis AngleAxis about the D3 axis.
                m_sm1.localRotation = Quaternion.AngleAxis(
                    SampleFloat(kSm1RotTimes, kSm1RotAngles, t), kTiltAxis);
                m_sm3.localRotation = Quaternion.AngleAxis(
                    SampleFloat(kSm3RotTimes, kSm3RotAngles, t), kFlipAxis);
                m_sm4.localRotation = Quaternion.AngleAxis(
                    SampleFloat(kSm4RotTimes, kSm4RotAngles, t), kFlipAxis);
                // Positions: base localPosition + the PANI offset.
                m_sm1.localPosition = m_sm1Base + SampleVector3(kSm1PosTimes, kSm1PosOffsets, t);
                m_sm2.localPosition = m_sm2Base + SampleVector3(kSm2PosTimes, kSm2PosOffsets, t);
                m_sm3.localPosition = m_sm3Base + SampleVector3(kSm3PosTimes, kSm3PosOffsets, t);
                m_sm4.localPosition = m_sm4Base + SampleVector3(kSm4PosTimes, kSm4PosOffsets, t);
                // Light ramps from its starting intensity to full over
                // the first 0.5 s, then holds. A fresh light ramps
                // 0 -> 1.5; a reused steady light ramps 0.5 -> 1.5
                // (no dip).
                if (m_light != null)
                    m_light.intensity = Mathf.Lerp(m_lightStartIntensity, kLightIntensity, Mathf.Clamp01(t / kLightRampTime));
                if (m_time >= kDuration)
                {
                    // Latched: the transforms keep their final values.
                    enabled = false;
                }
            }
            catch (System.Exception ex)
            {
                Debug.Log("[ConsoleMotion] Update failed: " + ex.ToString());
                enabled = false;
            }
        }

        // Piecewise-linear sample of a (time, float) track. Clamps to the
        // first/last value outside the range (D3 holds the first frame
        // until the track starts, and latches the last frame).
        private static float SampleFloat(float[] times, float[] values, float t)
        {
            if (t <= times[0])
                return values[0];
            for (int i = 1; i < times.Length; i++)
            {
                if (t <= times[i])
                {
                    float f = (t - times[i - 1]) / (times[i] - times[i - 1]);
                    return Mathf.Lerp(values[i - 1], values[i], f);
                }
            }
            return values[values.Length - 1];
        }

        private static Vector3 SampleVector3(float[] times, Vector3[] values, float t)
        {
            if (t <= times[0])
                return values[0];
            for (int i = 1; i < times.Length; i++)
            {
                if (t <= times[i])
                {
                    float f = (t - times[i - 1]) / (times[i] - times[i - 1]);
                    return Vector3.Lerp(values[i - 1], values[i], f);
                }
            }
            return values[values.Length - 1];
        }
    }
}
