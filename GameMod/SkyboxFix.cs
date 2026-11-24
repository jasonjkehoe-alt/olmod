// SkyboxFix.cs -- runtime fix for the gray sky mask (L1/L2).
//
// Phase 1 (overnight job, Nov 24) decided H2: the gray over the sky is
// a separate mask/overlay, NOT the dome's own "crazy sci-fi" texture.
// The texture is exonerated (video ball avg (115,77,27) == texture avg
// (114,76,27); the gray (68-115, neutral) matches no part of it), and
// the dome mesh/UVs are verified correct on disk. The strongest concrete
// anomaly is the camera render state: the camera clears with
// clearFlags=Skybox but RenderSettings.skybox is NONE (the converter
// deliberately removed the bluesky AssetBundle workaround; the dome mesh
// is the sky by design). The gray is Unity's Skybox-clear fallback
// showing where the dome mesh does not cover the view.
//
// Round 1 (fc35e85) built a Cubemap skybox from the dome's own texture
// and assigned it to RenderSettings.skybox via the Skybox/Cubemap
// shader. That FAILED in the Nov 24 morning flight: the log shows
//   [SKY FIX] Skybox/Cubemap shader not found -- skybox NOT set (fail open)
// Shader.Find("Skybox/Cubemap") returns null -- Unity strips shaders no
// asset references, and no skybox shader survives in the Overload build.
// So RenderSettings.skybox was never set, and the CAM lines confirm
// skybox=none skyboxShader=none on every tick. The dome mesh itself
// renders fine (the bright circle in the flight screenshot IS the dome).
//
// Round 3 (this file): the shader fallback chain PREEMPTED the mesh
// and proved the gray is NOT the Skybox-clear fallback. The Nov 24
// round-2 flight log shows:
//   [SKY FIX] skybox shader found: Skybox/6 Sided
//   [SKY FIX] skybox set: SkyboxFix_crazy sci-fi shader=Skybox/6 Sided srcTex=crazy sci-fi
// and the CAM lines confirm skybox=SkyboxFix_crazy sci-fi,
// skyboxShader=Skybox/6 Sided, occCull=False, bg=(0,0,0,1.00),
// farClip=800 -- a skybox IS set -- yet the flight screenshot looks
// THE SAME (bounded nebula circle, dull gray around it) and there is
// no "sky sphere mesh built" line and no SkyboxFix_Sphere in the probe
// BIG scan. So the dull gray is NOT the clear fallback. The sphere
// mesh -- the intended fix -- never ran.
//
// Leading hypothesis H-A: the readable copy made by MakeReadable
// (Graphics.Blit + ReadPixels) comes back blank/gray, so the 6 Sided
// skybox renders gray on all six faces while the dome (which uses the
// ORIGINAL texture) renders the bright circle on top. Round 3
// therefore: (a) FORCE the sphere-mesh path -- the
// RenderSettings.skybox path is removed entirely, no shader is ever
// consulted, and any stale skybox is cleared; (b) the mesh material
// uses the ORIGINAL texture object directly (as the dome does -- it
// never went through MakeReadable); (c) a one-line diagnostic samples
// the readable copy and logs its average RGB, so the next flight log
// settles H-A without blocking the mesh build.
//
// The mesh is a SPHERE centred on the camera (round 3, revised
// design -- see below for why not a box):
//   - Radius 700 m: inside the camera farClip of 800 m (SkyFarClip),
//     outside the 511 m dome, so the dome (nearer) still renders on
//     top of it wherever the dome covers the view. Every point of the
//     mesh is exactly 700 m from the camera, so nothing sags inside
//     the dome or pokes past the far plane.
//   - SPHERICAL UV mapping: u = longitude 0-1 around the sphere,
//     v = latitude 0-1 pole to pole. The nebula renders as a
//     continuous band (dark zenith, nebula mid, orange horizon) with
//     NO discrete face-discs and NO view-following circle.
//   - Triangles are wound inward (visible from inside) and the
//     material also sets Cull Off as belt-and-braces.
//   - Material: Standard shader, full-strength emission from the dome
//     texture (the ORIGINAL texture object, same as the dome),
//     metallic 0, glossiness 0.
//   - renderQueue 1000 (Background): drawn first, so the dome, the
//     Sun+stars mesh, and the terrain all draw over it. ZWrite off,
//     so nothing behind the sphere is depth-blocked by it.
//   - No collider (raycasts pass through), no shadow casting or
//     receiving, no light/reflection probes.
//   - A follower component re-centres the sphere on Camera.main every
//     frame (LateUpdate), so it always surrounds the player.
//   - Layer 14 (Level, same as the dome) so it is inside the camera
//     cull mask.
//
// WHY A SPHERE, NOT A BOX (Jason, Nov 24 ~08:09 -- new evidence
// that changed the diagnosis): "the whole nebula is there in the
// sky, it looks right, it stays where it's supposed to be, it's
// perfect -- it's just I have this round circle [that] is the only
// way I can view it with all its colors and its richness, and that
// circle FOLLOWS MY VIEW all around. The nebula is there how it's
// supposed to be, it's rendered properly and it stays where it's
// supposed to be." So the nebula is WORLD-LOCKED and correct (the
// dome renders it right) and the bright circle is VIEW-FOLLOWING.
// That is exactly what a 6 Sided skybox does: the disc texture on
// all six faces, so whichever face you look at, its disc is centred
// in your view -- the bright disc follows your view and the dull
// face corners surround it. A box mesh with the disc on each face
// would REPLICATE that (same mapping as the 6 Sided skybox). The
// sphere with spherical UVs has no face-discs, so there is nothing
// to follow the view.
//
// Round 3: NO shader fallback chain. The 6 Sided skybox path was
// proven in the Nov 24 round-2 flight to change nothing (skybox set,
// visual identical), so no shader is consulted and the sphere mesh is
// always built. RenderSettings.skybox is cleared so no stale skybox
// can render over or under the mesh. The log says which path ran,
// with a [SKY FIX] tag.
//
// Also (unchanged from round 1): disable occlusion culling on the main
// camera and set the camera background colour to black as a fallback.
// These run on every level load, before the path choice, so a fresh
// camera on a reloaded level still gets them.
//
// No geometry, UVs, normals, textures, or materials of the level are
// changed. No level is hardcoded; the sky texture is found generically,
// so this works for L1 (crazy sci-fi) and L2 (phobossky) alike.
//
// Fail-open throughout: any failure is logged with a "[SKY FIX]"
// prefix and the level loads unchanged. Do NOT remove SkyProbe.cs; the
// probe stays for verification (its BIG scan will now list the sphere as
// SkyboxFix_Sphere -- that is expected -- and its CAM lines will show
// occCull=False and the sphere in the SKY/BIG lines).

using HarmonyLib;
using Overload;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameMod
{
    // Keeps the sky sphere centred on the camera so it always surrounds
    // the player. LateUpdate runs after the camera has moved.
    public class SkyboxFixFollower : MonoBehaviour
    {
        private Camera m_cam = null;

        void LateUpdate()
        {
            try
            {
                if (m_cam == null)
                    m_cam = Camera.main;
                if (m_cam != null)
                    transform.position = m_cam.transform.position;
            }
            catch (Exception)
            {
                // Fail silent: a static box is still better than none.
            }
        }
    }

    public static class SkyboxFixPatch
    {
        private const string kTag = "[SKY FIX]";

        // D3 sky textures as embedded in the .sp (L1: crazy sci-fi /
        // marsplanet / sun; L2: phobossky / marsplanet / mercsun11 /
        // sun). Matched case-insensitively, exact name. Same list as
        // SkyProbe so the two agree on what "the sky texture" is.
        private static readonly string[] kSkyTextures = new string[]
        {
            "crazy sci-fi", "marsplanet", "sun", "phobossky", "mercsun11"
        };

        // Sky sphere geometry: radius 700 m (inside the 800 m
        // farClip, outside the 511 m dome), 32 longitude x 16
        // latitude segments (561 verts, 1024 tris). Every point is
        // exactly 700 m from the centre -- no sag, no far-plane poke.
        private const float kSphereRadius = 700f;
        private const int kSphereLonSeg = 32;
        private const int kSphereLatSeg = 16;

        // The live sphere mesh (null until built, Unity-null after
        // the scene unloads -- so a reloaded level rebuilds it fresh).
        private static GameObject s_sphere = null;

        [HarmonyPatch(typeof(GameplayManager), "StartLevel")]
        public static class StartLevelPatch
        {
            static void Postfix()
            {
                try
                {
                    SkyboxFixPatch.Apply();
                }
                catch (Exception ex)
                {
                    Debug.Log(kTag + " apply failed (fail open): " + ex.ToString());
                }
            }
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

        // Find the D3 sky texture: the first live-scene MeshRenderer
        // named sm_* whose material mainTexture is a known sky texture.
        // Prefers "crazy sci-fi" (the dome texture in L1) over the
        // satellite/star discs, since the dome is the sky background.
        private static Texture2D FindSkyTexture()
        {
            MeshRenderer[] all = null;
            try { all = Resources.FindObjectsOfTypeAll<MeshRenderer>(); }
            catch (Exception ex)
            {
                Debug.Log(kTag + " renderer scan failed: " + ex.ToString());
                return null;
            }
            if (all == null)
                return null;
            Texture2D fallback = null;
            foreach (MeshRenderer mr in all)
            {
                if (mr == null || mr.gameObject == null)
                    continue;
                try
                {
                    if (!mr.gameObject.scene.IsValid())
                        continue;
                    if (!mr.gameObject.name.StartsWith("sm_"))
                        continue;
                    Material mat = mr.sharedMaterial;
                    if (mat == null || mat.mainTexture == null)
                        continue;
                    string texName = mat.mainTexture.name;
                    if (!IsSkyTexture(texName))
                        continue;
                    Texture2D tex = mat.mainTexture as Texture2D;
                    if (tex == null)
                        continue;
                    if (texName.ToLowerInvariant() == "crazy sci-fi" ||
                        texName.ToLowerInvariant() == "phobossky")
                    {
                        Debug.Log(kTag + " dome sky texture found: " + texName
                            + " (" + tex.width + "x" + tex.height + ") on "
                            + mr.gameObject.name);
                        return tex;
                    }
                    if (fallback == null)
                        fallback = tex;
                }
                catch (Exception ex)
                {
                    Debug.Log(kTag + " scan entry failed: " + ex.ToString());
                }
            }
            if (fallback != null)
                Debug.Log(kTag + " sky texture (fallback) found: " + fallback.name);
            return fallback;
        }

        // Make a readable copy of a texture. .sp-embedded textures are
        // typically not CPU-readable, so GetPixels() on the source would
        // throw; blitting through a RenderTexture works regardless.
        private static Texture2D MakeReadable(Texture2D src)
        {
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                try
                {
                    Texture2D copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                    copy.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                    copy.Apply();
                    return copy;
                }
                finally
                {
                    RenderTexture.active = prev;
                }
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // Round 3: the RenderSettings.skybox path (round 1) and the
        // shader fallback chain (round 2) are REMOVED. The 6 Sided
        // skybox was proven in the Nov 24 round-2 flight to change
        // nothing (skybox set, visual identical), so no shader is
        // ever consulted and the box mesh is always built.
        // MakeReadable is kept ONLY for the H-A diagnostic below.

        // H-A diagnostic (round 3): sample the readable copy of the
        // sky texture and log its average RGB. Hypothesis H-A is that
        // MakeReadable (Graphics.Blit + ReadPixels) returns a blank /
        // gray copy, which is what the 6 Sided skybox rendered in
        // round 2 while the dome (original texture) rendered the
        // bright circle. This never blocks the mesh build: any failure
        // is logged and Apply() continues.
        private static void LogReadableCopyAvg(Texture2D skyTex)
        {
            try
            {
                Texture2D copy = MakeReadable(skyTex);
                if (copy == null)
                {
                    Debug.Log(kTag + " readable copy avg RGB: (null copy)");
                    return;
                }
                Color[] px = copy.GetPixels();
                if (px == null || px.Length == 0)
                {
                    Debug.Log(kTag + " readable copy avg RGB: (no pixels)");
                    return;
                }
                double r = 0, g = 0, b = 0;
                foreach (Color c in px)
                {
                    r += c.r; g += c.g; b += c.b;
                }
                int n = px.Length;
                Debug.Log(kTag + " readable copy avg RGB: ("
                    + (int)(r / n * 255.0 + 0.5) + ","
                    + (int)(g / n * 255.0 + 0.5) + ","
                    + (int)(b / n * 255.0 + 0.5) + ")"
                    + " src=" + skyTex.name + " " + copy.width + "x" + copy.height
                    + " (H-A diagnostic; dome texture avg is (114,76,27))");
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " readable copy avg failed (diagnostic only): " + ex.Message);
            }
        }

        // Build the sky sphere mesh: a sphere of radius kSphereRadius
        // with spherical UV mapping (u = longitude 0-1, v = latitude
        // 0-1 pole to pole), triangles wound inward, using the ORIGINAL
        // sky texture directly (same as the dome -- never the readable
        // copy). No collider, no shadows, Background render queue,
        // ZWrite off, full emission from the dome texture via the
        // Standard shader.
        private static void BuildSkySphereMesh(Texture2D skyTex)
        {
            float r = kSphereRadius;
            int lon = kSphereLonSeg;
            int lat = kSphereLatSeg;

            int vertsPerRow = lon + 1;
            int totalVerts = (lat + 1) * vertsPerRow;
            int totalTris = lat * lon * 2;
            Vector3[] verts = new Vector3[totalVerts];
            Vector3[] normals = new Vector3[totalVerts];
            Vector2[] uvs = new Vector2[totalVerts];
            int[] tris = new int[totalTris * 3];

            for (int j = 0; j <= lat; j++)
            {
                // Polar angle from +Y (north pole): 0 at the north
                // pole, PI at the south pole.
                float phi = Mathf.PI * (float)j / (float)lat;
                float sinPhi = Mathf.Sin(phi);
                float cosPhi = Mathf.Cos(phi);
                for (int i = 0; i <= lon; i++)
                {
                    float theta = 2f * Mathf.PI * (float)i / (float)lon;
                    float sinTheta = Mathf.Sin(theta);
                    float cosTheta = Mathf.Cos(theta);
                    Vector3 dir = new Vector3(sinPhi * cosTheta, cosPhi, sinPhi * sinTheta);
                    int idx = j * vertsPerRow + i;
                    verts[idx] = dir * r;
                    normals[idx] = -dir; // inward
                    // u = longitude 0-1, v = latitude 0-1 (v=1 north pole).
                    uvs[idx] = new Vector2((float)i / (float)lon, 1f - (float)j / (float)lat);
                }
            }

            int tBase = 0;
            for (int j = 0; j < lat; j++)
            {
                for (int i = 0; i < lon; i++)
                {
                    int a = j * vertsPerRow + i;
                    int b = j * vertsPerRow + (i + 1);
                    int c = (j + 1) * vertsPerRow + (i + 1);
                    int d = (j + 1) * vertsPerRow + i;
                    // (a,c,b),(a,d,c): wound inward (verified by cross
                    // product at the equator -- the face normal points
                    // toward the centre). Cull Off is belt-and-braces.
                    tris[tBase++] = a; tris[tBase++] = c; tris[tBase++] = b;
                    tris[tBase++] = a; tris[tBase++] = d; tris[tBase++] = c;
                }
            }

            Mesh mesh = new Mesh();
            mesh.name = "SkyboxFix_SphereMesh";
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();

            Shader standard = Shader.Find("Standard");
            if (standard == null)
            {
                Debug.Log(kTag + " Standard shader not found -- sky sphere NOT built (fail open)");
                return;
            }
            Material mat = new Material(standard);
            mat.name = "SkyboxFix_Sphere_" + skyTex.name;
            // ORIGINAL texture object directly -- same as the dome.
            mat.mainTexture = skyTex;
            mat.SetTexture("_EmissionMap", skyTex);
            mat.SetColor("_EmissionColor", Color.white);
            mat.EnableKeyword("_EMISSION");
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Glossiness", 0f);
            // Do not write depth: the dome, Sun+stars, and terrain are
            // nearer and must draw over the sphere. (Harmless if the
            // shader ignores _ZWrite.)
            mat.SetInt("_ZWrite", 0);
            // Belt-and-braces: faces are wound inward anyway.
            mat.SetInt("_Cull", 0);
            // Background queue: drawn first, everything else draws over it.
            mat.renderQueue = 1000;

            GameObject go = new GameObject("SkyboxFix_Sphere");
            // Layer 14 (Level, same as the dome) -- inside the camera cull mask.
            go.layer = 14;
            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            go.AddComponent<SkyboxFixFollower>();
            Camera cam = Camera.main;
            if (cam != null)
                go.transform.position = cam.transform.position;
            s_sphere = go;

            Debug.Log(kTag + " sky sphere mesh built: SkyboxFix_Sphere"
                + " radius=" + r + " lon=" + lon + " lat=" + lat
                + " verts=" + totalVerts + " tris=" + totalTris
                + " tex=" + skyTex.name
                + " renderQueue=1000 layer=14"
                + " pos=" + go.transform.position.ToString());
        }

        // Occlusion culling off on the main camera (rules out an
        // occlusion-culling false positive masking the sky) and the
        // camera background to black as a belt-and-braces fallback
        // (better than gray if the sky ever fails to apply). Runs on
        // every level load so a fresh camera still gets it.
        private static void ApplyCameraTweaks()
        {
            try
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    bool wasOcc = cam.useOcclusionCulling;
                    cam.useOcclusionCulling = false;
                    cam.backgroundColor = Color.black;
                    Debug.Log(kTag + " camera: occCull " + wasOcc + " -> false, bg -> black"
                        + " clear=" + cam.clearFlags
                        + " skybox=" + ((RenderSettings.skybox != null) ? RenderSettings.skybox.name : "none"));
                }
                else
                {
                    Debug.Log(kTag + " Camera.main null at apply time");
                }
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " camera tweak failed (fail open): " + ex.ToString());
            }
        }

        private static void Apply()
        {
            // Camera tweaks first: idempotent, and a reloaded level
            // has a fresh camera that needs them too.
            ApplyCameraTweaks();

            Texture2D skyTex = FindSkyTexture();
            if (skyTex == null)
            {
                Debug.Log(kTag + " no D3 sky texture found -- sky NOT fixed (fail open)");
                return;
            }

            // Sphere mesh already active (same load)? Skip.
            if (s_sphere != null)
            {
                Debug.Log(kTag + " sky sphere mesh already active, skipping");
                return;
            }

            // Round 3: the RenderSettings.skybox path is gone. Clear
            // any skybox (e.g. a stale 6 Sided one from an earlier
            // build in this session) so the mesh is the only sky
            // background; with clearFlags=Skybox and no skybox, the
            // camera falls back to the black background colour.
            if (RenderSettings.skybox != null)
            {
                Debug.Log(kTag + " clearing stale skybox: " + RenderSettings.skybox.name);
                RenderSettings.skybox = null;
            }

            // H-A diagnostic: log the readable copy's average RGB.
            // Never blocks the mesh build.
            LogReadableCopyAvg(skyTex);

            // Round 3: always build the sphere mesh. No shader is
            // consulted. The mesh material uses the ORIGINAL texture
            // object directly (same as the dome) -- never the
            // readable copy.
            Debug.Log(kTag + " building sky sphere mesh (forced mesh path, round 3)");
            BuildSkySphereMesh(skyTex);
        }
    }
}
