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
// Round 2 (this file): do NOT depend on RenderSettings.skybox or any
// skybox shader. The fix is a runtime sky-box MESH built from the
// dome's own texture, using only components proven to render in this
// build: the Standard shader (the dome renders with it), a mesh, and
// the dome's own texture.
//
// The mesh is a spherified cube ("sky box") centred on the camera:
//   - Radius 700 m: inside the camera farClip of 800 m (SkyFarClip),
//     outside the 511 m dome, so the dome (nearer) still renders on
//     top of it wherever the dome covers the view.
//   - Each face is a 4x4 grid spherified onto the 700 m sphere, so no
//     point of the mesh sags inside the dome or pokes past the far
//     plane (a flat cube's corners sit at 1212 m and would be clipped
//     by the far plane, leaving gray bands along the cube edges).
//   - Each face maps the full nebula texture (a disc on every face --
//     the same visual result the cubemap attempted).
//   - Triangles are wound inward (visible from inside) and the
//     material also sets Cull Off as belt-and-braces.
//   - Material: Standard shader, full-strength emission from the dome
//     texture (same as the dome), metallic 0, glossiness 0.
//   - renderQueue 1000 (Background): drawn first, so the dome, the
//     Sun+stars mesh, and the terrain all draw over it. ZWrite off,
//     so nothing behind the box is depth-blocked by it.
//   - No collider (raycasts pass through), no shadow casting or
//     receiving, no light/reflection probes.
//   - A follower component re-centres the box on Camera.main every
//     frame (LateUpdate), so it always surrounds the player.
//   - Layer 14 (Level, same as the dome) so it is inside the camera
//     cull mask.
//
// Shader fallback chain (cheap, first): Shader.Find("Skybox/Cubemap"),
// then "Skybox/Procedural", then "Skybox/6 Sided". Only Skybox/Cubemap
// and Skybox/6 Sided can actually carry the nebula texture (Procedural
// generates its own sky and has no texture input, so it is logged and
// skipped). If a usable shader IS found, the RenderSettings.skybox
// path (the round-1 code) is used; otherwise the box mesh is built.
// Either way the log says which path was taken, with a [SKY FIX] tag.
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
// probe stays for verification (its BIG scan will now list the box as
// SkyboxFix_Box -- that is expected -- and its CAM lines will show
// occCull=False and the box in the SKY/BIG lines).

using HarmonyLib;
using Overload;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameMod
{
    // Keeps the sky box centred on the camera so it always surrounds
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

        // Skybox shaders, in preference order. Only Skybox/Cubemap and
        // Skybox/6 Sided can carry a texture; Skybox/Procedural has no
        // texture input and is logged and skipped if found.
        private static readonly string[] kSkyboxShaders = new string[]
        {
            "Skybox/Cubemap", "Skybox/Procedural", "Skybox/6 Sided"
        };

        // Sky box geometry: spherified cube, radius 700 m (inside the
        // 800 m farClip, outside the 511 m dome), 4 quads per face
        // edge (sag ~13 m, so the closest mesh point is ~687 m --
        // still outside the dome, still inside the far plane).
        private const float kBoxRadius = 700f;
        private const int kBoxSubdiv = 4;

        // The live box mesh (null until built, Unity-null after the
        // scene unloads -- so a reloaded level rebuilds it fresh).
        private static GameObject s_box = null;

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

        private static Cubemap BuildCubemap(Texture2D readable)
        {
            // Cubemap is square: one size (the texture is 256x256).
            int size = readable.width;
            Cubemap cm = new Cubemap(size, TextureFormat.RGBA32, false);
            Color[] px = readable.GetPixels();
            for (CubemapFace face = CubemapFace.PositiveX; face <= CubemapFace.NegativeZ; face++)
            {
                cm.SetPixels(px, face);
            }
            cm.Apply();
            return cm;
        }

        // Try the skybox shader chain. Returns the shader to use for
        // the RenderSettings.skybox path, or null if no usable shader
        // is in the build (the round-1 failure: Unity strips shaders
        // no asset references). Skybox/Procedural is logged and
        // skipped -- it generates its own sky and has no texture
        // input, so it cannot carry the nebula.
        private static Shader FindSkyboxShader()
        {
            foreach (string name in kSkyboxShaders)
            {
                Shader s = null;
                try { s = Shader.Find(name); }
                catch (Exception ex)
                {
                    Debug.Log(kTag + " Shader.Find(" + name + ") threw: " + ex.Message);
                }
                if (s == null)
                {
                    Debug.Log(kTag + " skybox shader not found: " + name);
                    continue;
                }
                if (name == "Skybox/Procedural")
                {
                    Debug.Log(kTag + " skybox shader found: " + name
                        + " -- has no texture input, cannot carry the nebula; using box mesh instead");
                    continue;
                }
                Debug.Log(kTag + " skybox shader found: " + name);
                return s;
            }
            return null;
        }

        // Round-1 path: build a Cubemap from the dome texture and
        // assign it to RenderSettings.skybox. Only used when a usable
        // skybox shader survived in the build.
        private static void ApplySkyboxMaterial(Texture2D skyTex, Shader skyShader)
        {
            Texture2D readable = null;
            try
            {
                readable = MakeReadable(skyTex);
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " readable copy failed (fail open): " + ex.ToString());
                return;
            }

            Material skyboxMat = new Material(skyShader);
            skyboxMat.name = "SkyboxFix_" + skyTex.name;
            if (skyShader.name == "Skybox/6 Sided")
            {
                // 6 Sided takes one texture per face; the nebula disc
                // goes on all six, same as the cubemap did.
                skyboxMat.SetTexture("_FrontTex", readable);
                skyboxMat.SetTexture("_BackTex", readable);
                skyboxMat.SetTexture("_LeftTex", readable);
                skyboxMat.SetTexture("_RightTex", readable);
                skyboxMat.SetTexture("_UpTex", readable);
                skyboxMat.SetTexture("_DownTex", readable);
            }
            else
            {
                // Skybox/Cubemap.
                Cubemap cm = null;
                try
                {
                    cm = BuildCubemap(readable);
                }
                catch (Exception ex)
                {
                    Debug.Log(kTag + " cubemap build failed (fail open): " + ex.ToString());
                    return;
                }
                skyboxMat.SetTexture("_Tex", cm);
                skyboxMat.SetFloat("_Exposure", 1.0f);
                skyboxMat.SetFloat("_Rotation", 0f);
            }
            RenderSettings.skybox = skyboxMat;
            Debug.Log(kTag + " skybox set: " + skyboxMat.name
                + " shader=" + skyShader.name
                + " srcTex=" + skyTex.name);
        }

        // One face of the spherified cube, as its four corners in
        // order v0, v1, v2, v3 (u runs v0->v1, v runs v0->v3).
        // Triangles per quad are (a,b,c),(a,c,d) on the +X/-X faces
        // and (a,c,b),(a,d,c) on the others, where a=(i,j), b=(i+1,j),
        // c=(i+1,j+1), d=(i,j+1) -- wound so the front faces point
        // INWARD (visible from inside the box). Verified by cross
        // product on every face before this shipped.
        private struct FaceDef
        {
            public Vector3 v0;
            public Vector3 v1;
            public Vector3 v2;
            public Vector3 v3;
            public bool flip;
            public FaceDef(Vector3 a, Vector3 b, Vector3 c, Vector3 d, bool f)
            {
                v0 = a; v1 = b; v2 = c; v3 = d; flip = f;
            }
        }

        // Build the sky box mesh: a spherified cube of radius
        // kBoxRadius, each face a kBoxSubdiv x kBoxSubdiv grid mapping
        // the full nebula texture, triangles wound inward. No collider,
        // no shadows, Background render queue, ZWrite off, full
        // emission from the dome texture via the Standard shader.
        private static void BuildSkyBoxMesh(Texture2D skyTex)
        {
            float h = kBoxRadius;
            int n = kBoxSubdiv;
            FaceDef[] faces = new FaceDef[]
            {
                // +X (flip=false): (0,1,2),(0,2,3)
                new FaceDef(new Vector3(h, -h, -h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(h, h, -h), false),
                // -X (flip=false)
                new FaceDef(new Vector3(-h, -h, h), new Vector3(-h, -h, -h), new Vector3(-h, h, -h), new Vector3(-h, h, h), false),
                // +Y (flip=true): (0,2,1),(0,3,2)
                new FaceDef(new Vector3(-h, h, -h), new Vector3(-h, h, h), new Vector3(h, h, h), new Vector3(h, h, -h), true),
                // -Y (flip=true)
                new FaceDef(new Vector3(-h, -h, h), new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, -h, h), true),
                // +Z (flip=true)
                new FaceDef(new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(-h, h, h), true),
                // -Z (flip=true)
                new FaceDef(new Vector3(h, -h, -h), new Vector3(-h, -h, -h), new Vector3(-h, h, -h), new Vector3(h, h, -h), true),
            };

            int vertsPerFace = (n + 1) * (n + 1);
            int totalVerts = faces.Length * vertsPerFace;
            int totalTris = faces.Length * n * n * 2;
            Vector3[] verts = new Vector3[totalVerts];
            Vector3[] normals = new Vector3[totalVerts];
            Vector2[] uvs = new Vector2[totalVerts];
            int[] tris = new int[totalTris * 3];

            int vBase = 0;
            int tBase = 0;
            for (int f = 0; f < faces.Length; f++)
            {
                FaceDef fd = faces[f];
                for (int j = 0; j <= n; j++)
                {
                    for (int i = 0; i <= n; i++)
                    {
                        float u = (float)i / (float)n;
                        float v = (float)j / (float)n;
                        // Bilinear point on the flat cube face, then
                        // spherify onto the kBoxRadius sphere.
                        Vector3 p = fd.v0 + (fd.v1 - fd.v0) * u + (fd.v3 - fd.v0) * v;
                        Vector3 dir = p.normalized;
                        Vector3 sp = dir * h;
                        int idx = vBase + j * (n + 1) + i;
                        verts[idx] = sp;
                        normals[idx] = -dir; // inward
                        uvs[idx] = new Vector2(u, v);
                    }
                }
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int a = vBase + j * (n + 1) + i;
                        int b = vBase + j * (n + 1) + (i + 1);
                        int c = vBase + (j + 1) * (n + 1) + (i + 1);
                        int d = vBase + (j + 1) * (n + 1) + i;
                        if (!fd.flip)
                        {
                            tris[tBase++] = a; tris[tBase++] = b; tris[tBase++] = c;
                            tris[tBase++] = a; tris[tBase++] = c; tris[tBase++] = d;
                        }
                        else
                        {
                            tris[tBase++] = a; tris[tBase++] = c; tris[tBase++] = b;
                            tris[tBase++] = a; tris[tBase++] = d; tris[tBase++] = c;
                        }
                    }
                }
                vBase += vertsPerFace;
            }

            Mesh mesh = new Mesh();
            mesh.name = "SkyboxFix_BoxMesh";
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();

            Shader standard = Shader.Find("Standard");
            if (standard == null)
            {
                Debug.Log(kTag + " Standard shader not found -- sky box NOT built (fail open)");
                return;
            }
            Material mat = new Material(standard);
            mat.name = "SkyboxFix_Box_" + skyTex.name;
            mat.mainTexture = skyTex;
            mat.SetTexture("_EmissionMap", skyTex);
            mat.SetColor("_EmissionColor", Color.white);
            mat.EnableKeyword("_EMISSION");
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Glossiness", 0f);
            // Do not write depth: the dome, Sun+stars, and terrain are
            // nearer and must draw over the box. (Harmless if the
            // shader ignores _ZWrite.)
            mat.SetInt("_ZWrite", 0);
            // Belt-and-braces: faces are wound inward anyway.
            mat.SetInt("_Cull", 0);
            // Background queue: drawn first, everything else draws over it.
            mat.renderQueue = 1000;

            GameObject go = new GameObject("SkyboxFix_Box");
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
            s_box = go;

            Debug.Log(kTag + " sky box mesh built: SkyboxFix_Box"
                + " radius=" + h + " subdiv=" + n
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

            // Box mesh already active (same load)? Skip.
            if (s_box != null)
            {
                Debug.Log(kTag + " sky box mesh already active, skipping");
                return;
            }

            // Shader path already set for this texture (RenderSettings
            // persists across scene loads)? Skip.
            if (RenderSettings.skybox != null
                && RenderSettings.skybox.name == "SkyboxFix_" + skyTex.name)
            {
                Debug.Log(kTag + " skybox already set for " + skyTex.name + ", skipping");
                return;
            }

            // Shader fallback chain first (cheap). If a usable skybox
            // shader survived in the build, use the RenderSettings
            // path; otherwise build the box mesh.
            Shader skyShader = FindSkyboxShader();
            if (skyShader != null)
            {
                ApplySkyboxMaterial(skyTex, skyShader);
                return;
            }

            Debug.Log(kTag + " no usable skybox shader in build -- building sky box mesh");
            BuildSkyBoxMesh(skyTex);
        }
    }
}
