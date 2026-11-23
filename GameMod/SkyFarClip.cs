// SkyFarClip.cs -- runtime fix for the flat-gray sky (D3 L1/L2).
//
// Measured live (Nov 23 flight, 37 probe ticks): the camera far clip
// plane is 200.0 in every tick, and the sky dome (511.3 m radius,
// centre (556.9, 54.4, 518.4)) + Sun sit 468-540 m from the camera --
// beyond the far plane, so the whole sky is clipped and the level
// renders flat gray. RenderSettings.fog is False in every tick, so
// fog is exonerated; the far plane is the measured blocker.
//
// This patch (Harmony postfix on GameplayManager.StartLevel, in the
// style of SkyProbe.cs) sets Camera.main.farClipPlane to 800.0 at
// level load. 800 covers the worst case: the farthest in-level
// camera is ~174 m from the dome centre, so the far side of the dome
// is ~174 + 511.3 = ~685 m away, and the satellites were measured
// 465-629 m out. A small ticker re-asserts the value every 5 s if
// anything ever pulls it back below 800 (the probe showed it stable,
// so the ticker should stay silent; it only logs when it acts).
//
// Fog Mode Off: the .sp material stream has no per-material shader
// override, so a fog-immune sky shader is not expressible from the
// level data -- the sky materials are instead made unlit by
// full-strength emission (converter/editor side). As a runtime guard,
// when a D3 sky is present in the level (sm_* renderers with a D3
// sky texture or huge bounds) this patch also forces
// RenderSettings.fog off at level load if it is on (it is off in
// every measured tick, so this is a no-op in practice). D3 room fog
// (orange/pink fire-room fog) is a separate effect, not
// RenderSettings.fog, and is unaffected.
//
// Fail-open throughout: any failure is logged and the level loads
// unchanged. No gameplay behaviour is changed -- a wider far plane
// only ever shows more, never less.

using HarmonyLib;
using Overload;
using System;
using UnityEngine;

namespace GameMod
{
    public static class SkyFarClipPatch
    {
        private const string kTag = "[SKY FIX]";
        private const float kFarClip = 800.0f;
        private const float kBigBoundsMagnitude = 400.0f;

        // D3 sky textures as embedded in the .sp (L1: crazy sci-fi /
        // marsplanet / sun; L2: phobossky / marsplanet / mercsun11 /
        // sun). Matched case-insensitively, exact name. (Same list as
        // SkyProbe.cs, duplicated here so this fix does not depend on
        // the probe.)
        private static readonly string[] kSkyTextures = new string[]
        {
            "crazy sci-fi", "marsplanet", "sun", "phobossky", "mercsun11"
        };

        private static GameObject s_tickerGo = null;

        [HarmonyPatch(typeof(GameplayManager), "StartLevel")]
        public static class StartLevelPatch
        {
            static void Postfix()
            {
                try
                {
                    SkyFarClipPatch.OnLevelLoaded();
                }
                catch (Exception ex)
                {
                    Debug.Log(kTag + " level-load fix failed (fail open): " + ex.ToString());
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

        // True when the level contains D3 sky objects (the dome /
        // satellites / stars): a live-scene MeshRenderer named sm_*
        // whose material mainTexture is a D3 sky texture or whose
        // bounds are huge. Generic -- no level is hardcoded.
        private static bool HasSkyObjects()
        {
            MeshRenderer[] all = null;
            try { all = Resources.FindObjectsOfTypeAll<MeshRenderer>(); }
            catch (Exception ex)
            {
                Debug.Log(kTag + " renderer scan failed: " + ex.ToString());
                return false;
            }
            if (all == null)
                return false;
            foreach (MeshRenderer mr in all)
            {
                if (mr == null)
                    continue;
                try
                {
                    GameObject go = mr.gameObject;
                    if (go == null || !go.scene.IsValid())
                        continue;
                    if (!go.name.StartsWith("sm_"))
                        continue;
                    Material mat = mr.sharedMaterial;
                    string texName = (mat != null && mat.mainTexture != null) ? mat.mainTexture.name : "";
                    if (IsSkyTexture(texName))
                        return true;
                    if (mr.bounds.size.magnitude > kBigBoundsMagnitude)
                        return true;
                }
                catch (Exception) { }
            }
            return false;
        }

        // Set farClip to >= kFarClip. Returns true if it changed it
        // (and logs the change); silent when already sufficient.
        internal static bool EnsureFarClip()
        {
            Camera cam = null;
            try { cam = Camera.main; }
            catch (Exception) { return false; }
            if (cam == null)
                return false;
            if (cam.farClipPlane >= kFarClip)
                return false;
            float old = cam.farClipPlane;
            try
            {
                cam.farClipPlane = kFarClip;
                Debug.Log(kTag + " farClipPlane " + old.ToString("F1") + " -> " + kFarClip.ToString("F1") + " (sky dome/satellites un-clipped)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " farClip set failed: " + ex.ToString());
                return false;
            }
        }

        private static void OnLevelLoaded()
        {
            EnsureFarClip();
            // Fog Mode Off (runtime guard): only when a D3 sky is
            // present, and only if fog is actually on. Measured off
            // in every L1 tick, so this is a no-op in practice.
            try
            {
                bool skyPresent = HasSkyObjects();
                if (skyPresent && RenderSettings.fog)
                {
                    RenderSettings.fog = false;
                    Debug.Log(kTag + " RenderSettings.fog was ON with a D3 sky present -> set OFF (Fog Mode Off for the sky; D3 room fog is a separate effect and is unaffected)");
                }
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " fog guard failed (fail open): " + ex.ToString());
            }
            EnsureTicker();
        }

        private static void EnsureTicker()
        {
            if (s_tickerGo != null)
                return;
            try
            {
                s_tickerGo = new GameObject("SkyFarClipTicker");
                s_tickerGo.AddComponent<SkyFarClipTicker>();
                UnityEngine.Object.DontDestroyOnLoad(s_tickerGo);
            }
            catch (Exception ex)
            {
                Debug.Log(kTag + " ticker create failed: " + ex.ToString());
            }
        }
    }

    // Re-asserts farClip every 5 s while a level is loaded. The probe
    // showed farClip stable, so this should stay silent; it only logs
    // when it actually changes something. (Fog is a level-load guard
    // only -- it is not re-asserted here, since nothing in the game
    // changes it per-frame and re-asserting it could fight a level
    // that wants fog.)
    public class SkyFarClipTicker : MonoBehaviour
    {
        private const float kTickInterval = 5.0f;
        private float m_nextCheck = 0f;

        void Update()
        {
            try
            {
                if (GameManager.m_player_ship == null)
                    return;
                if (Time.time < m_nextCheck)
                    return;
                m_nextCheck = Time.time + kTickInterval;
                SkyFarClipPatch.EnsureFarClip();
            }
            catch (Exception ex)
            {
                Debug.Log("[SKY FIX] tick failed: " + ex.ToString());
            }
        }
    }
}
