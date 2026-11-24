// SkyProbe.cs -- runtime diagnostic for the flat-gray sky (L1/L2),
// extended Nov 23 for the bounded-ball sky (dome renders as a ~60 deg
// ball against flat gray instead of an enclosing sky).
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
// Nov 23 ball extension -- the dome is verified perfect on disk (384
// verts, 176 tris, all normals inward, 100% raycast coverage from the
// player) and the first probe showed it active/enabled/visible/in-frustum
// with farClip 800 and fog off, yet it still renders as a bounded ball.
// Three candidates remain, and this extension discriminates them in ONE
// flight:
//   (a) Chunk culling: ChunkManager.ActivateChunks() runs every frame
//       and SetActive(false)s any chunk not visible from the player's
//       segment in the baked m_segment_visibility table. CHUNK lines log
//       the dome chunk's live state plus the baked visibility value from
//       the player's segment to the chunk's segments.
//   (b) Occluding mesh: RAY lines sweep the camera in 25 world
//       directions and log the first collider hit (name/dist/layer).
//       A remnant sky cube/sphere or terrain backfaces in front of the
//       dome show up as a near hit in the gray directions. A BIG scan at
//       level load lists every huge-bounds renderer by any name.
//   (c) Material/shader: SKY lines now carry layer, cull-mask
//       membership, renderQueue, the Standard shader _Cull mode,
//       RenderType tag, shadow/light-probe settings and the collider
//       type; CAM lines now carry RenderSettings.skybox (the camera
//       clears with clearFlags=Skybox, so a gray skybox is a suspect)
//       and useOcclusionCulling.
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
            string skyboxName = "none";
            string skyboxShader = "none";
            try
            {
                Material sb = RenderSettings.skybox;
                if (sb != null)
                {
                    skyboxName = sb.name;
                    if (sb.shader != null)
                        skyboxShader = sb.shader.name;
                }
            }
            catch (Exception) { }
            string occCull = "n/a";
            try { occCull = cam.useOcclusionCulling.ToString(); }
            catch (Exception) { }
            return kTag + " " + when + " CAM farClip=" + cam.farClipPlane.ToString("F1")
                + " nearClip=" + cam.nearClipPlane.ToString("F2")
                + " clear=" + cam.clearFlags.ToString()
                + " bg=" + FmtColor(cam.backgroundColor)
                + " cullMask=" + cam.cullingMask
                + " cullMaskHex=" + cam.cullingMask.ToString("X8")
                + " occCull=" + occCull
                + " skybox=" + skyboxName
                + " skyboxShader=" + skyboxShader
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
            // Nov 23 ball extension: material/render-path detail for
            // candidate (c), plus the layer/cull-mask check and the
            // collider type (the RAY sweep can only see the dome if it
            // has a collider).
            int layer = go.layer;
            string layerName = "n/a";
            try { layerName = LayerMask.LayerToName(layer); }
            catch (Exception) { }
            bool inCullMask = false;
            if (cam != null)
            {
                try { inCullMask = (cam.cullingMask & (1 << layer)) != 0; }
                catch (Exception) { }
            }
            string renderQueue = "n/a";
            string cullMode = "n/a";
            string renderType = "n/a";
            try
            {
                if (mat != null)
                {
                    renderQueue = mat.renderQueue.ToString();
                    if (mat.HasProperty("_Cull"))
                        cullMode = mat.GetInt("_Cull").ToString();
                    renderType = mat.GetTag("RenderType", false);
                    if (renderType == null || renderType.Length == 0)
                        renderType = "n/a";
                }
            }
            catch (Exception) { }
            string shadowCast = "n/a";
            string recvShadow = "n/a";
            string lightProbe = "n/a";
            string reflProbe = "n/a";
            string sortingLayer = "n/a";
            string sortingOrder = "n/a";
            try
            {
                shadowCast = mr.shadowCastingMode.ToString();
                recvShadow = mr.receiveShadows.ToString();
                lightProbe = mr.lightProbeUsage.ToString();
                reflProbe = mr.reflectionProbeUsage.ToString();
                sortingLayer = mr.sortingLayerName;
                sortingOrder = mr.sortingOrder.ToString();
            }
            catch (Exception) { }
            string colliderType = "none";
            try
            {
                Collider col = mr.GetComponent<Collider>();
                if (col != null)
                    colliderType = col.GetType().Name;
            }
            catch (Exception) { }
            return "SKY " + go.name
                + " parent=" + ((mr.transform.parent != null) ? mr.transform.parent.name : "null")
                + " parentChain=" + ParentChain(mr.transform)
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
                + " distToVtx0=" + vtxDist
                + " layer=" + layer + "(" + layerName + ")"
                + " inCullMask=" + inCullMask
                + " renderQueue=" + renderQueue
                + " cull=" + cullMode
                + " renderType=" + renderType
                + " shadowCast=" + shadowCast
                + " recvShadow=" + recvShadow
                + " lightProbe=" + lightProbe
                + " reflProbe=" + reflProbe
                + " sorting=" + sortingLayer + "/" + sortingOrder
                + " collider=" + colliderType;
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

        // Full transform path to the scene root, e.g.
        // Chunk0013/sm_0013_001. Confirms which chunk GameObject owns
        // the dome at runtime (candidate (a)).
        internal static string ParentChain(Transform t)
        {
            try
            {
                string chain = "";
                Transform cur = t;
                int depth = 0;
                while (cur != null && depth < 8)
                {
                    chain = cur.name + ((chain.Length > 0) ? "/" + chain : "");
                    cur = cur.parent;
                    depth++;
                }
                return (chain.Length > 0) ? chain : "n/a";
            }
            catch (Exception)
            {
                return "n/a";
            }
        }

        // First run of digits in a name, e.g. "Chunk0013" -> 13,
        // "sm_0013_001" -> 13. -1 when none.
        internal static int ParseChunkNum(string name)
        {
            try
            {
                if (name == null)
                    return -1;
                int start = -1;
                int end = -1;
                for (int i = 0; i < name.Length; i++)
                {
                    bool digit = name[i] >= '0' && name[i] <= '9';
                    if (digit && start < 0)
                        start = i;
                    if (!digit && start >= 0)
                    {
                        end = i;
                        break;
                    }
                }
                if (start < 0)
                    return -1;
                if (end < 0)
                    end = name.Length;
                int val = 0;
                for (int i = start; i < end; i++)
                    val = val * 10 + (name[i] - '0');
                return val;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // Candidate (a): the dome chunk's live state + the baked
        // visibility value from the player's segment to the chunk's
        // segments. ActivateChunks() SetActive(false)s a chunk when NO
        // segment visible from the player's segment belongs to it, so
        // visMax=0 with the chunk active means something else (robot
        // force-on / use_segment_visibility off) keeps it alive.
        internal static void LogChunkState(string when, int playerSeg)
        {
            try
            {
                int domeChunk = -1;
                foreach (MeshRenderer mr in s_skyRenderers)
                {
                    if (mr == null || mr.gameObject == null)
                        continue;
                    if (mr.transform.parent != null)
                        domeChunk = ParseChunkNum(mr.transform.parent.name);
                    if (domeChunk >= 0)
                        break;
                }
                if (domeChunk < 0)
                {
                    Debug.Log(kTag + " " + when + " CHUNK domeChunk=unknown (no sky renderer parent)");
                    return;
                }
                GameObject chunkGo = null;
                try { chunkGo = ChunkManager.GetChunkObject(domeChunk); }
                catch (Exception) { }
                string chunkState = "null";
                if (chunkGo != null)
                    chunkState = "activeSelf=" + chunkGo.activeSelf + " activeInHierarchy=" + chunkGo.activeInHierarchy;
                string useSegVis = "n/a";
                try { useSegVis = GameplayManager.m_use_segment_visibility.ToString(); }
                catch (Exception) { }
                int visMax = -1;
                int visCount = 0;
                int chunkSegs = 0;
                try
                {
                    LevelData ld = GameManager.m_level_data;
                    if (ld != null && ld.Segments != null && ld.m_segment_visibility != null && playerSeg >= 0 && playerSeg < ld.Segments.Length)
                    {
                        for (int k = 0; k < ld.Segments.Length; k++)
                        {
                            if (ld.Segments[k].ChunkNum != domeChunk)
                                continue;
                            chunkSegs++;
                            int v = ld.m_segment_visibility[playerSeg, k];
                            if (v > visMax)
                                visMax = v;
                            if (v > 0)
                                visCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log(kTag + " " + when + " CHUNK visibility lookup failed: " + ex.ToString());
                }
                int activeChunks = -1;
                int totalChunks = -1;
                try
                {
                    GameObject[] all = GameManager.m_level_data.ChunkObjects;
                    if (all != null)
                    {
                        totalChunks = all.Length;
                        activeChunks = 0;
                        for (int i = 0; i < all.Length; i++)
                        {
                            if (all[i] != null && all[i].activeSelf)
                                activeChunks++;
                        }
                    }
                }
                catch (Exception) { }
                Debug.Log(kTag + " " + when + " CHUNK domeChunk=" + domeChunk
                    + " " + chunkState
                    + " useSegVis=" + useSegVis
                    + " playerSeg=" + playerSeg
                    + " chunkSegs=" + chunkSegs
                    + " visMax=" + visMax
                    + " visCount=" + visCount
                    + " activeChunks=" + activeChunks + "/" + totalChunks);
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " " + when + " CHUNK log failed: " + ex.ToString());
            }
        }

        // Candidate (b): one ray per line, world directions, from the
        // camera. A near hit in a gray direction is the occluder; no
        // hit out to 2000 m means nothing collider-bearing stands
        // between the camera and the sky there. The dome itself only
        // registers if it has a collider (see the SKY collider= field).
        internal static void LogRaycastSweep(Vector3 camPos, Vector3 camFwd)
        {
            try
            {
                int[] azimuths = new int[] { 0, 45, 90, 135, 180, 225, 270, 315 };
                float[] elevations = new float[] { -10f, 25f, 60f };
                for (int e = 0; e < elevations.Length; e++)
                {
                    for (int a = 0; a < azimuths.Length; a++)
                    {
                        float azR = azimuths[a] * Mathf.Deg2Rad;
                        float elR = elevations[e] * Mathf.Deg2Rad;
                        Vector3 dir = new Vector3(
                            Mathf.Cos(elR) * Mathf.Sin(azR),
                            Mathf.Sin(elR),
                            Mathf.Cos(elR) * Mathf.Cos(azR));
                        LogOneRay("RAY", camPos, dir, azimuths[a], elevations[e]);
                    }
                }
                // Straight up.
                LogOneRay("RAY", camPos, Vector3.up, 0, 90f);
                // Centre of the screen: what Jason is looking at.
                if (camFwd.sqrMagnitude > 0.01f)
                    LogOneRay("RAYFWD", camPos, camFwd.normalized, -1, -999f);
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " RAY sweep failed: " + ex.ToString());
            }
        }

        private static void LogOneRay(string tag, Vector3 origin, Vector3 dir, int az, float el)
        {
            try
            {
                RaycastHit hit;
                bool hasHit = Physics.Raycast(origin, dir, out hit, 2000f, Physics.AllLayers, QueryTriggerInteraction.Ignore);
                string dirStr = (az >= 0) ? (" az=" + az + " el=" + el.ToString("F0")) : " fwd";
                if (hasHit && hit.collider != null)
                {
                    GameObject go = hit.collider.gameObject;
                    Debug.Log(kTag + " " + tag + dirStr
                        + " hit=" + go.name
                        + " dist=" + hit.distance.ToString("F1")
                        + " layer=" + go.layer
                        + " point=" + FmtVec(hit.point));
                }
                else
                {
                    Debug.Log(kTag + " " + tag + dirStr + " hit=none dist>2000");
                }
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " " + tag + " ray failed: " + ex.ToString());
            }
        }

        // Candidate (b), level-load scan: any renderer with huge
        // bounds, whatever its name -- a remnant sky cube/sphere or
        // other giant occluder would show up here even if it is not
        // named sm_*. The sky objects themselves are expected here too.
        internal static void ScanBigRenderers()
        {
            try
            {
                MeshRenderer[] all = Resources.FindObjectsOfTypeAll<MeshRenderer>();
                if (all == null)
                    return;
                int found = 0;
                foreach (MeshRenderer mr in all)
                {
                    if (mr == null || mr.gameObject == null)
                        continue;
                    try
                    {
                        if (!mr.gameObject.scene.IsValid())
                            continue;
                        if (mr.bounds.size.magnitude <= kBigBoundsMagnitude)
                            continue;
                        found++;
                        Material mat = mr.sharedMaterial;
                        Debug.Log(kTag + " BIG " + mr.gameObject.name
                            + " parentChain=" + ParentChain(mr.transform)
                            + " active=" + mr.gameObject.activeInHierarchy
                            + " enabled=" + mr.enabled
                            + " visible=" + mr.isVisible
                            + " boundsC=" + FmtVec(mr.bounds.center)
                            + " boundsSize=" + FmtVec(mr.bounds.size)
                            + " mat=" + ((mat != null) ? mat.name : "null")
                            + " renderQueue=" + ((mat != null) ? mat.renderQueue.ToString() : "n/a"));
                    }
                    catch (Exception ex)
                    {
                        Debug.Log(kTag + " BIG scan entry failed: " + ex.ToString());
                    }
                }
                Debug.Log(kTag + " LEVEL-LOAD big renderers found: " + found);
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " BIG scan failed: " + ex.ToString());
            }
        }

        private static void OnLevelLoaded()
        {
            Debug.Log(CameraAndFogLine("LEVEL-LOAD"));
            ScanSkyObjects();
            LogSkyObjects("LEVEL-LOAD");
            ScanBigRenderers();
            LogChunkState("LEVEL-LOAD", -1);
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
            SkyProbePatch.LogChunkState("TICK", seg);
            if (cam != null)
                SkyProbePatch.LogRaycastSweep(cam.transform.position, cam.transform.forward);
        }
    }
}
