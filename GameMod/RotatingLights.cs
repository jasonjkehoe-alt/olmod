using HarmonyLib;
using Overload;
using UnityEngine;

namespace GameMod
{
    // Rotating warning lights (Nov 21, 2026): the D3 fixedrotatinglight02
    // (mine_15) fixtures rotate in D3, but the conversion leaves them
    // static — the lens glows (yellowligthcc emission) but the fixture
    // never turns and casts no real light. Jason: "they must ROTATE, GLOW,
    // and CAST LIGHT — all three."
    //
    // Geometry (fixedrotatinglight02.oof, single submodel Light02, offset
    // 0; 1 .oof unit = 0.2 m): the fixture hangs from its mount at the
    // origin (bounds y[-0.90, 0.02] m) and the 2 lens faces sit on the +Z
    // side (centroids (0.11, -0.51, 0.57) and (-0.10, -0.36, 0.59) m,
    // normal (0, -0.135, 0.991) — outward and slightly down). So the
    // fixture rotates about its own local Y (the mount axis): the lens
    // sweeps horizontally around the room, exactly a warning beacon.
    //
    // Identification: the carrier's MeshFilter wears the mesh
    // "mesh_prop_fixedrotatinglight02" (the generic prop builder names
    // the mesh mesh_prop_<modelKey>). No converter change, no .sp change,
    // no hardcoded positions — any level with these fixtures gets
    // rotating lights (universal, like the Swatter rule). The patch runs
    // at level start (same GameplayManager.StartLevel postfix pattern as
    // TextureMotionManager) and attaches a RotatingLight MonoBehaviour to
    // each carrier.
    //
    // The light: a runtime SpotLight, child of the carrier at the lens
    // position, aimed along the lens normal — it rotates WITH the
    // fixture, so the beam sweeps the room. Warm yellow (the lens
    // colour), range 8 m, 60 deg cone, intensity 2.0, no shadows.
    // The lens emission itself is unchanged (it already glows).
    //
    // Fail-open: any exception logs and the fixtures stay static (the
    // level never depends on this patch to load). A carrier that already
    // has a RotatingLight (StartLevel called twice in one scene) is
    // skipped, so a restart never doubles the speed or the lights.
    [HarmonyPatch(typeof(GameplayManager), "StartLevel")]
    public class GameplayManager_StartLevel_RotatingLights
    {
        private const string kRotatingLightMesh = "mesh_prop_fixedrotatinglight02";

        static void Postfix()
        {
            try { PostfixInner(); }
            catch (System.Exception ex)
            {
                Debug.Log("[RotatingLights] StartLevel postfix failed: " + ex.ToString());
            }
        }

        static void PostfixInner()
        {
            MeshFilter[] filters = Object.FindObjectsOfType<MeshFilter>();
            int attached = 0;
            int skipped = 0;
            foreach (MeshFilter mf in filters)
            {
                if (mf == null || mf.sharedMesh == null)
                    continue;
                if (mf.sharedMesh.name != kRotatingLightMesh)
                    continue;
                GameObject carrier = mf.gameObject;
                if (carrier == null)
                    continue;
                if (carrier.GetComponent<RotatingLight>() != null)
                {
                    skipped++;
                    continue;
                }
                RotatingLight rl = carrier.AddComponent<RotatingLight>();
                if (rl != null)
                    attached++;
            }
            Debug.Log("[RotatingLights] attached to " + attached + " fixture(s), skipped " + skipped + " already-rotating.");
        }
    }

    // The per-fixture rotator. Added to the carrier at level start.
    // Rotates the whole fixture about its local Y (the mount axis) and
    // carries a SpotLight through the lens, so the beam sweeps the room.
    // Runs forever (D3's rotating lights never stop); Time.deltaTime
    // respects pause.
    public class RotatingLight : MonoBehaviour
    {
        // One revolution per 3 s — a calm warning-beacon sweep. Tunable;
        // D3's exact rate was not recovered from local data.
        private const float kDegreesPerSecond = 120f;
        // Lens position / aim in the fixture's local frame (metres,
        // from fixedrotatinglight02.oof): the average of the 2 lens-face
        // centroids, and their shared normal.
        private static readonly Vector3 kLensLocalPos = new Vector3(0f, -0.44f, 0.58f);
        private static readonly Vector3 kLensNormal = new Vector3(0f, -0.135f, 0.991f);
        private const float kLightRange = 8.0f;
        private const float kLightSpotAngle = 60.0f;
        private const float kLightIntensity = 2.0f;
        private static readonly Color kLightColor = new Color(1.0f, 0.85f, 0.45f);

        void Start()
        {
            try
            {
                GameObject lightGO = new GameObject("rotating_light");
                lightGO.transform.SetParent(transform, false);
                lightGO.transform.localPosition = kLensLocalPos;
                lightGO.transform.localRotation = Quaternion.LookRotation(kLensNormal);
                Light light = lightGO.AddComponent<Light>();
                light.type = LightType.Spot;
                light.color = kLightColor;
                light.range = kLightRange;
                light.spotAngle = kLightSpotAngle;
                light.intensity = kLightIntensity;
                light.shadows = LightShadows.None;
            }
            catch (System.Exception ex)
            {
                Debug.Log("[RotatingLights] Start failed: " + ex.ToString());
                enabled = false;
            }
        }

        void Update()
        {
            try
            {
                transform.Rotate(Vector3.up * kDegreesPerSecond * Time.deltaTime);
            }
            catch (System.Exception ex)
            {
                Debug.Log("[RotatingLights] Update failed: " + ex.ToString());
                enabled = false;
            }
        }
    }
}
