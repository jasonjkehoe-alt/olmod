// SkyProbe.cs -- runtime diagnostic for the flat-gray sky (L1/L2).
//
// The sky dome is wound correctly on disk, the sky textures are embedded
// in the .sp, and the material bindings are right -- yet L1 flies flat
// gray, and in L2 Jason sees stars only partly (they vanish when he
// pans). The suspects can only be settled live: the camera far clip
// plane cutting the dome off (dome radius 511.3 m, Mars 550-650 m from
// the player), the game's scene fog washing the sky out, or the sky
// pieces being culled/disabled.
//
// This patch logs everything needed to tell those apart, with a
// "[SKY PROBE]" prefix, to the game output log:
//   - At level load: camera (farClipPlane, clearFlags, backgroundColor,
//     cullingMask, layerCullDistances[14]) + RenderSettings fog, and one
//     line per sky object (active, renderer enabled, isVisible,
//     in-frustum test, bounds, material/shader/texture, _EMISSION
//     keyword, distance to bounds centre and to first mesh vertex).
//   - Every 5 s while in a level: a TICK row with player position,
//     segment (LevelData.FindSegmentContainingWorldPosition), whether
//     that segment is an outdoor terrain cube, camera forward, the
//     camera/fog block again, and the sky-object lines again (so a
//     pan that makes the stars vanish shows up as a state change).
//
// Sky objects are matched generically, not per level: any scene
// MeshRenderer named sm_* whose material mainTexture is a D3 sky
// texture (crazy sci-fi, marsplanet, sun, phobossky, mercsun11) or
// whose bounds are huge (dome / star field). No level is hardcoded.
//
// Fail-open throughout: any failure is logged and the level loads
// unchanged. Diagnostic only -- no gameplay behaviour is changed.

using HarmonyLib;
using Overload;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameMod
{
    public static class SkyProbePatch
    {
        private const string kTag = "[SKY PROBE]";
        private const float kTickInterval = 5.0f;
        private const float kBigBoundsMagnitude = 400.0f;

        // D3 sky textures as embedded in the .sp (L1: crazy sci-fi /
        // marsplanet / sun; L2: phobossky / marsplanet / mercsun11 /
        // sun). Matched case-insensitively, exact name.
        private static readonly string[] kSkyTextures = new string[]
        {
            "crazy sci-fi", "marsplanet", "sun", "phobossky", "mercsun11"
        };

        private static List<MeshRenderer> s_skyRenderers = new List<MeshRenderer>();
        private static GameObject s_tickerGo = null;

        [HarmonyPatch(typeof(GameplayManager), "StartLevel")]
        public static class StartLevelPatch
        {
            static void Postfix()
            {
                try
                {
                    SkyProbePatch.OnLevelLoaded();
                }
                catch (Exception ex)
                {
                    Debug.Log(kTag + " level-load scan failed (fail open): " + ex.ToString());
                }
            }
        }

        internal static string FmtVec(Vector3 v)
        {
            return "(" + v.x.ToString("F1") + "," + v.y.ToString("F1") + "," + v.z.ToString("F1") + ")";
        }

        internal static string FmtColor(Color c)
        {
            return "(" + c.r.ToString("F2") + "," + c.g.ToString("F2") + "," + c.b.ToString("F2") + "," + c.a.ToString("F2") + ")";
        }

        private static Camera GetCamera()
        {
            try { return Camera.main; }
            catch (Exception) { return null; }
        }

        // Camera + fog block. Logged at level load and on every tick row.
        internal static string CameraAndFogLine(string when)
        {
            Camera cam = GetCamera();
            if (cam == null)
                return kTag + " " + when + " CAM camera=null (no MainCamera yet)";
            string lcd14 = "n/a";
            try
            {
                float[] lcd = cam.layerCullDistances;
                if (lcd != null && lcd.Length > 14)
                    lcd14 = lcd[14].ToString("F1");
                else if (lcd != null)
                    lcd14 = "n/a(len=" + lcd.Length + ")";
            }
            catch (Exception) { }
            return kTag + " " + when + " CAM farClip=" + cam.farClipPlane.ToString("F1")
                + " clear=" + cam.clearFlags.ToString()
                + " bg=" + FmtColor(cam.backgroundColor)
                + " cullMask=" + cam.cullingMask
                + " layerCullDist14=" + lcd14
                + " | FOG fog=" + RenderSettings.fog
                + " mode=" + RenderSettings.fogMode.ToString()
                + " color=" + FmtColor(RenderSettings.fogColor)
                + " density=" + RenderSettings.fogDensity.ToString("F4")
                + " start=" + RenderSettings.fogStartDistance.ToString("F1")
                + " end=" + RenderSettings.fogEndDistance.ToString("F1");
        }

        private static bool IsSkyTexture(string name)
        {
            if (name == null || name.Length == 0)
                return false;
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < kSkyTextures.Length; i++)
            {
                if (lower == kSkyTextures[i])
                    return true;
            }
            return false;
        }

        // True when the segment is an outdoor-terrain cube: a 16 x 8 x
        // 16 m axis-aligned box. Same AABB test as GuidebotSteering's
        // IsTerrainCube (copied here so this probe does not depend on
        // GuideBot code): SegmentData.MinCornerPos / MaxCornerPos.
        internal static bool IsTerrainCube(int seg)
        {
            try
            {
                LevelData ld = GameManager.m_level_data;
                if (ld == null || ld.Segments == null)
                    return false;
                if (seg < 0 || seg >= ld.Segments.Length)
                    return false;
                Vector3 size = ld.Segments[seg].MaxCornerPos - ld.Segments[seg].MinCornerPos;
                return size.x >= 15f && size.x <= 17f &&
                       size.y >= 7f && size.y <= 9f &&
                       size.z >= 15f && size.z <= 17f;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void ScanSkyObjects()
        {
            s_skyRenderers.Clear();
            MeshRenderer[] all = null;
            try { all = Resources.FindObjectsOfTypeAll<MeshRenderer>(); }
            catch (Exception ex)
            {
                Debug.Log(kTag + " renderer scan failed: " + ex.ToString());
                return;
            }
            if (all == null)
                return;
            foreach (MeshRenderer mr in all)
            {
                if (mr == null)
                    continue;
                try
                {
                    GameObject go = mr.gameObject;
                    if (go == null)
                        continue;
                    // Skip prefab assets: only objects in a live scene.
                    if (!go.scene.IsValid())
                        continue;
                    if (!go.name.StartsWith("sm_"))
                        continue;
                    Material mat = mr.sharedMaterial;
                    string texName = (mat != null && mat.mainTexture != null) ? mat.mainTexture.name : "";
                    bool texMatch = IsSkyTexture(texName);
                    bool bigMatch = mr.bounds.size.magnitude > kBigBoundsMagnitude;
                    if (!texMatch && !bigMatch)
                        continue;
                    s_skyRenderers.Add(mr);
                    Debug.Log(kTag + " FOUND " + go.name
                        + " match=" + (texMatch ? "tex" : "big")
                        + " tex=" + (texName.Length > 0 ? texName : "null"));
                }
                catch (Exception ex)
                {
                    Debug.Log(kTag + " scan entry failed: " + ex.ToString());
                }
            }
            Debug.Log(kTag + " LEVEL-LOAD sky objects found: " + s_skyRenderers.Count);
        }

        private static string SkyObjectLine(MeshRenderer mr, Camera cam, Vector3 camPos)
        {
            GameObject go = mr.gameObject;
            Material mat = mr.sharedMaterial;
            string matName = (mat != null) ? mat.name : "null";
            string shaderName = (mat != null && mat.shader != null) ? mat.shader.name : "null";
            string texName = (mat != null && mat.mainTexture != null) ? mat.mainTexture.name : "null";
            bool emission = false;
            try { emission = (mat != null) && mat.IsKeywordEnabled("_EMISSION"); }
            catch (Exception) { }
            Bounds b = mr.bounds;
            string vtxDist = "n/a";
            try
            {
                // The mesh lives on the MeshFilter, not the renderer.
                MeshFilter mf = mr.GetComponent<MeshFilter>();
                Mesh mesh = (mf != null) ? mf.sharedMesh : null;
                if (mesh != null && mesh.vertexCount > 0)
                {
                    Vector3 w0 = mr.transform.TransformPoint(mesh.vertices[0]);
                    vtxDist = Vector3.Distance(camPos, w0).ToString("F1");
                }
            }
            catch (Exception) { }
            bool inFrustum = false;
            bool frustumOk = false;
            if (cam != null)
            {
                try
                {
                    Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
                    inFrustum = GeometryUtility.TestPlanesAABB(planes, b);
                    frustumOk = true;
                }
                catch (Exception) { }
            }
            return "SKY " + go.name
                + " parent=" + ((mr.transform.parent != null) ? mr.transform.parent.name : "null")
                + " active=" + go.activeInHierarchy
                + " enabled=" + mr.enabled
                + " visible=" + mr.isVisible
                + " inFrustum=" + (frustumOk ? inFrustum.ToString() : "n/a")
                + " boundsC=" + FmtVec(b.center)
                + " boundsSize=" + FmtVec(b.size)
                + " mat=" + matName
                + " shader=" + shaderName
                + " tex=" + texName
                + " emission=" + emission
                + " distToBoundsC=" + Vector3.Distance(camPos, b.center).ToString("F1")
                + " distToVtx0=" + vtxDist;
        }

        internal static void LogSkyObjects(string when)
        {
            Camera cam = GetCamera();
            Vector3 camPos = (cam != null) ? cam.transform.position : Vector3.zero;
            foreach (MeshRenderer mr in s_skyRenderers)
            {
                if (mr == null)
                    continue;
                try
                {
                    Debug.Log(kTag + " " + when + " " + SkyObjectLine(mr, cam, camPos));
                }
                catch (Exception ex)
                {
                    Debug.Log(kTag + " sky log failed for a renderer: " + ex.ToString());
                }
            }
        }

        private static void OnLevelLoaded()
        {
            Debug.Log(CameraAndFogLine("LEVEL-LOAD"));
            ScanSkyObjects();
            LogSkyObjects("LEVEL-LOAD");
            EnsureTicker();
        }

        private static void EnsureTicker()
        {
            if (s_tickerGo != null)
                return;
            try
            {
                s_tickerGo = new GameObject("SkyProbeTicker");
                s_tickerGo.AddComponent<SkyProbeTicker>();
                UnityEngine.Object.DontDestroyOnLoad(s_tickerGo);
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " ticker create failed: " + ex.ToString());
            }
        }
    }

    // Ticks every 5 s while a level is loaded. The first row logs as
    // soon as the player ship exists, so the in-game camera values are
    // captured even if Camera.main was null at StartLevel time.
    public class SkyProbeTicker : MonoBehaviour
    {
        private float m_nextLog = 0f;
        private bool m_firstRow = true;

        void Update()
        {
            try
            {
                if (GameManager.m_player_ship == null)
                    return;
                if (!m_firstRow && Time.time < m_nextLog)
                    return;
                m_firstRow = false;
                m_nextLog = Time.time + 5.0f;
                LogTickRow();
            }
            catch (Exception ex)
            {
                Debug.Log("[SKY PROBE] tick failed: " + ex.ToString());
            }
        }

        private void LogTickRow()
        {
            PlayerShip ship = GameManager.m_player_ship;
            if (ship == null)
                return;
            Vector3 pos = ship.transform.position;
            int seg = -1;
            bool terrain = false;
            try
            {
                LevelData ld = GameManager.m_level_data;
                if (ld != null)
                    seg = ld.FindSegmentContainingWorldPosition(pos, -1, true);
                terrain = SkyProbePatch.IsTerrainCube(seg);
            }
            catch (Exception ex)
            {
                Debug.Log("[SKY PROBE] segment lookup failed: " + ex.ToString());
            }
            Camera cam = Camera.main;
            string fwd = (cam != null) ? SkyProbePatch.FmtVec(cam.transform.forward) : "n/a";
            Debug.Log("[SKY PROBE] TICK t=" + Time.time.ToString("F1")
                + " pos=" + SkyProbePatch.FmtVec(pos)
                + " seg=" + seg
                + " terrain=" + terrain
                + " camFwd=" + fwd);
            Debug.Log(SkyProbePatch.CameraAndFogLine("TICK"));
            SkyProbePatch.LogSkyObjects("TICK");
        }
    }
}
