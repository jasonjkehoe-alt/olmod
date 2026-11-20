using HarmonyLib;
using Overload;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GameMod
{
    // Texture Motion Step 1 (Nov 20, 2026): EnergyCenter1 scrolling alone.
    // The D3 energy-center panel is a scrolling texture (table.gam:
    // slide_u=0, slide_v=1.0, speed=1.0) but the converter emits it as a
    // static image, so in Overload the panel sits frozen as a yellow
    // barcode. This manager scrolls it per frame, the way D3 does.
    //
    // Data: <level>-texturemotion.txt, built by the converter from
    // table.gam (one entry per texture: name;slide_u;slide_v;speed).
    // Loaded by DataReader.GetTextureMotionData (PresetData.cs) — same
    // pattern as GetRobotData, but ALSO tries the BARE filename, because
    // the zip lookup (Mission.LoadAddonData -> ZipFile[filepath]) does an
    // exact name match and a full Windows path NEVER matches a bare zip
    // entry (proven Nov 20 by indexing the real deployed mission zip with
    // the game's own DotNetZip.dll: full-path -> NULL, bare -> FOUND).
    // The full-path tries cover the loose case (ZipPath == null ->
    // FindAddonFile reads the loose file next to the .sp); the bare tries
    // cover the zip case. GetRobotData/GetProjData have the same zip-case
    // bug — fixing them is a separate decision (it would change the
    // robotdata loading the Goblin diagnosis is still investigating);
    // this job fixes the loader ONLY for texturemotion.txt.
    //
    // Hook: postfix on GameplayManager.StartLevel. By then the level is
    // fully loaded and the renderers/materials exist. The postfix loads
    // the data, scans the scene for materials whose mainTexture.name
    // matches an entry (case-insensitive), and creates a manager
    // GameObject whose Update() advances mainTextureOffset by
    // (slide_u, slide_v) * time. The manager is NOT DontDestroyOnLoad —
    // it dies with the scene, and a fresh one is created on the next
    // StartLevel. If a manager already exists (StartLevel called twice
    // in one scene), the old one is destroyed first, so the scan is
    // idempotent.
    //
    // Materials: per-renderer instances (renderer.materials), NOT the
    // shared material — the level materials are stock poc materials
    // shared across levels, and scrolling a sharedMaterial would leave a
    // shifted offset behind for the next level. Per-renderer instances
    // die with the scene. The list is captured ONCE at scan time;
    // Update() only touches the stored Material references (never
    // re-queries renderer.materials, which would instantiate a new copy
    // per frame).
    //
    // Emission: only _MainTex is scrolled, unless the material's
    // _EmissionMap is the SAME texture object as _MainTex (datapani /
    // bright antennas in Step 3) — then its offset is scrolled too.
    // EnergyCenter1's _EmissionMap is black (__d3_black_clear), so Step 1
    // scrolls _MainTex only.
    //
    // Rate: slide_v (UV/s) from table.gam, exactly as authored in D3.
    // EnergyCenter1: slide_u=0, slide_v=1.0, speed=1.0 — the panel
    // scrolls one full texture height per second. The offset is absolute
    // (rate * time-since-level-start, wrapped to [0,1)), so a restart or
    // re-scan always starts from 0. Time.deltaTime respects pause.
    //
    // Failure: if the data file is missing/empty, or no material
    // matches, the manager simply does not start — the level renders
    // exactly as before (frozen texture). A patch exception fails open
    // the same way. No data -> no manager -> no change; the level never
    // depends on this patch to load.
    [HarmonyPatch(typeof(GameplayManager), "StartLevel")]
    public class GameplayManager_StartLevel_TextureMotion
    {
        static void Postfix()
        {
            try { TextureMotionManager.Start(); }
            catch (Exception ex)
            {
                Debug.Log("[TextureMotion] StartLevel postfix failed: " + ex.ToString());
            }
        }
    }

    public static class TextureMotionManager
    {
        private const string kManagerObjectName = "TextureMotionManager";

        // One entry from <level>-texturemotion.txt.
        public class Entry
        {
            public string name;   // texture name, lowercase (e.g. "energycenter1")
            public float slideU;  // UV/s in U
            public float slideV;  // UV/s in V
            public float speed;   // table.gam speed (informational; the rate is slide)
        }

        // A material to scroll, with its entry's rates.
        public class ScrollingMaterial
        {
            public Material material;
            public float slideU;
            public float slideV;
            public bool scrollEmission; // _EmissionMap is the same texture as _MainTex
        }

        private static GameObject s_manager;

        public static void Start()
        {
            // Destroy a stale manager from an earlier StartLevel in this
            // scene (e.g. StartLevel called twice without a scene reload).
            if (s_manager != null)
                GameObject.Destroy(s_manager);
            s_manager = null;

            string text = DataReader.GetTextureMotionData();
            if (string.IsNullOrEmpty(text))
            {
                Debug.Log("[TextureMotion] no texturemotion data (stock level or missing file); manager not started.");
                return;
            }

            List<Entry> entries = Parse(text);
            if (entries.Count == 0)
            {
                Debug.Log("[TextureMotion] texturemotion data parsed to 0 entries; manager not started.");
                return;
            }

            List<ScrollingMaterial> scrolling = Scan(entries);
            if (scrolling.Count == 0)
            {
                Debug.Log("[TextureMotion] " + entries.Count + " entr(ies) but no matching material in the scene; manager not started.");
                return;
            }

            GameObject go = new GameObject(kManagerObjectName);
            TextureMotionBehaviour behaviour = go.AddComponent<TextureMotionBehaviour>();
            behaviour.Init(scrolling);
            s_manager = go;
            Debug.Log("[TextureMotion] started: " + entries.Count + " entr(ies), " + scrolling.Count + " material(s) scrolling.");
        }

        private static List<Entry> Parse(string text)
        {
            List<Entry> entries = new List<Entry>();
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2 || lines[0].Trim() != "TEXTUREMOTION_DATA")
            {
                Debug.Log("[TextureMotion] bad texturemotion header (expected TEXTUREMOTION_DATA).");
                return entries;
            }
            int count;
            if (!int.TryParse(lines[1].Trim(), out count))
            {
                Debug.Log("[TextureMotion] bad texturemotion count line: '" + lines[1] + "'.");
                return entries;
            }
            for (int i = 2; i < lines.Length && entries.Count < count; i++)
            {
                string[] parts = lines[i].Split(';');
                if (parts.Length != 4)
                {
                    Debug.Log("[TextureMotion] skipping malformed line: '" + lines[i] + "'.");
                    continue;
                }
                float su, sv, sp;
                if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out su) ||
                    !float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out sv) ||
                    !float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out sp))
                {
                    Debug.Log("[TextureMotion] skipping line with bad numbers: '" + lines[i] + "'.");
                    continue;
                }
                Entry e = new Entry();
                e.name = parts[0].Trim().ToLowerInvariant();
                e.slideU = su;
                e.slideV = sv;
                e.speed = sp;
                entries.Add(e);
            }
            return entries;
        }

        private static List<ScrollingMaterial> Scan(List<Entry> entries)
        {
            List<ScrollingMaterial> outList = new List<ScrollingMaterial>();
            HashSet<Material> seen = new HashSet<Material>();
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            foreach (Renderer r in renderers)
            {
                if (r == null)
                    continue;
                Material[] mats = r.materials; // per-renderer instances (not shared)
                foreach (Material m in mats)
                {
                    if (m == null || m.mainTexture == null)
                        continue;
                    if (seen.Contains(m))
                        continue;
                    string texName = m.mainTexture.name;
                    if (string.IsNullOrEmpty(texName))
                        continue;
                    foreach (Entry e in entries)
                    {
                        if (string.Equals(texName, e.name, StringComparison.OrdinalIgnoreCase))
                        {
                            ScrollingMaterial sm = new ScrollingMaterial();
                            sm.material = m;
                            sm.slideU = e.slideU;
                            sm.slideV = e.slideV;
                            sm.scrollEmission = m.HasProperty("_EmissionMap") && m.GetTexture("_EmissionMap") == m.mainTexture;
                            outList.Add(sm);
                            seen.Add(m);
                            Debug.Log("[TextureMotion] scrolling '" + texName + "' (material '" + m.name + "'): slide_u=" + e.slideU + ", slide_v=" + e.slideV + ", speed=" + e.speed + (sm.scrollEmission ? " (+_EmissionMap)" : ""));
                            break;
                        }
                    }
                }
            }
            return outList;
        }
    }

    // The per-frame driver. Created by TextureMotionManager.Start();
    // dies with the scene (not DontDestroyOnLoad).
    public class TextureMotionBehaviour : MonoBehaviour
    {
        private List<TextureMotionManager.ScrollingMaterial> m_scrolling;
        private float m_time;

        public void Init(List<TextureMotionManager.ScrollingMaterial> scrolling)
        {
            m_scrolling = scrolling;
            m_time = 0f;
        }

        void Update()
        {
            if (m_scrolling == null)
                return;
            m_time += Time.deltaTime;
            foreach (TextureMotionManager.ScrollingMaterial sm in m_scrolling)
            {
                if (sm.material == null)
                    continue;
                Vector2 offset = new Vector2(Wrap01(sm.slideU * m_time), Wrap01(sm.slideV * m_time));
                sm.material.mainTextureOffset = offset;
                if (sm.scrollEmission)
                    sm.material.SetTextureOffset("_EmissionMap", offset);
            }
        }

        private static float Wrap01(float x)
        {
            float r = x % 1f;
            if (r < 0f)
                r += 1f;
            return r;
        }
    }
}
