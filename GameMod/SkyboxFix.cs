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
// This patch gives the camera a real sky background built from the
// dome's own texture, so the background is nebula instead of gray:
//   - At level load: find the D3 sky texture (same matching as
//     SkyProbe: "crazy sci-fi" / "marsplanet" / "sun" / "phobossky" /
//     "mercsun11", or a huge-bounds sky renderer) and build a Cubemap
//     from it (the disc texture on all six faces -- a nebula disc in
//     every direction, which is what a space sky is). Assign it to
//     RenderSettings.skybox via the Skybox/Cubemap shader.
//   - Disable occlusion culling on the main camera (rules out occlusion
//     culling as a contributor; the dome is huge and should never be
//     occluded, but a false positive would mask the sky).
//   - Set the camera background colour to black as a belt-and-braces
//     fallback (harmless once the skybox is set; better than gray if
//     the skybox ever fails to apply).
//
// The dome mesh continues to render on top for detail (it is opaque
// and depth-tested, so it draws over the skybox where it renders).
// No geometry, UVs, normals, textures, or materials are changed.
// No level is hardcoded; the sky texture is found generically, so
// this works for L1 (crazy sci-fi) and L2 (phobossky) alike.
//
// Fail-open throughout: any failure is logged with a "[SKY FIX]"
// prefix and the level loads unchanged. This is a fix, not a probe,
// but it never blocks level load. Do NOT remove SkyProbe.cs; the probe
// stays for verification (its CAM lines will now show the skybox set).

using HarmonyLib;
using Overload;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameMod
{
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

        private static bool s_applied = false;

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

        private static void Apply()
        {
            // Run once per level load; StartLevel fires once per load,
            // and s_applied guards against a double-apply within it.
            if (s_applied)
            {
                Debug.Log(kTag + " already applied this load, skipping");
                return;
            }

            Texture2D skyTex = FindSkyTexture();
            if (skyTex == null)
            {
                Debug.Log(kTag + " no D3 sky texture found -- skybox NOT set (fail open)");
                return;
            }

            Shader cubemapShader = Shader.Find("Skybox/Cubemap");
            if (cubemapShader == null)
            {
                Debug.Log(kTag + " Skybox/Cubemap shader not found -- skybox NOT set (fail open)");
                return;
            }

            Texture2D readable = null;
            Cubemap cm = null;
            try
            {
                readable = MakeReadable(skyTex);
                cm = BuildCubemap(readable);
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " cubemap build failed (fail open): " + ex.ToString());
                return;
            }

            Material skyboxMat = new Material(cubemapShader);
            skyboxMat.SetTexture("_Tex", cm);
            skyboxMat.SetFloat("_Exposure", 1.0f);
            skyboxMat.SetFloat("_Rotation", 0f);
            skyboxMat.name = "SkyboxFix_" + skyTex.name;
            RenderSettings.skybox = skyboxMat;
            Debug.Log(kTag + " skybox set: " + skyboxMat.name
                + " shader=" + cubemapShader.name
                + " cubemap=" + cm.width + "x" + cm.height
                + " srcTex=" + skyTex.name);

            // Occlusion culling off on the main camera (rules out an
            // occlusion-culling false positive masking the sky).
            try
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    bool wasOcc = cam.useOcclusionCulling;
                    cam.useOcclusionCulling = false;
                    // Belt-and-braces: if the skybox ever fails to
                    // apply, the fallback background is black, not gray.
                    cam.backgroundColor = Color.black;
                    Debug.Log(kTag + " camera: occCull " + wasOcc + " -> false, bg -> black"
                        + " clear=" + cam.clearFlags
                        + " skybox=" + ((RenderSettings.skybox != null) ? RenderSettings.skybox.name : "none"));
                }
                else
                {
                    Debug.Log(kTag + " Camera.main null at apply time (skybox still set)");
                }
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " camera tweak failed (fail open): " + ex.ToString());
            }

            s_applied = true;
        }
    }
}
