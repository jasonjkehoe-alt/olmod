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
    // Materials: per-renderer instances, NOT the shared material — the
    // level materials are stock poc materials shared across levels, and
    // scrolling a sharedMaterial would leave a shifted offset behind for
    // the next level. Per-renderer instances die with the scene. The list
    // is captured ONCE at scan time; Update() only touches the stored
    // Material references (never re-queries materials, which would
    // instantiate a new copy per frame).
    //
    // Scan scope (FIX Nov 21 — see Scan): the scan READS
    // renderer.sharedMaterials only (no instantiation). It instantiates
    // ONLY a material whose texture matches an entry, by hand
    // (new Material), installed via sharedMaterials with the stock name
    // preserved — every other renderer in the scene is left exactly as
    // loaded. The old code read renderer.materials on EVERY renderer,
    // which instantiated a copy of every material in the level and
    // renamed each "X (Instance)" — that silently broke Overload's own
    // exact-name glow-material swaps (PropEmLight, TimedSwitch, etc.),
    // which is what broke the Falcon and the key room (bisect Nov 21).
    //
    // Emission: only _MainTex is scrolled, unless the material's
    // _EmissionMap is the SAME texture object as _MainTex (datapani /
    // bright antennas in Step 3) — then its offset is scrolled too.
    // EnergyCenter1's _EmissionMap is black (__d3_black_clear), so Step 1
    // scrolled _MainTex only — and the panel rendered dull (lit only by
    // the room), not glowing.
    //
    // Glow (Step 1b, Nov 20): the panel is an energy center — it must
    // GLOW bright yellow. Standard-shader emission is _EmissionColor x
    // _EmissionMap, so setting the color alone would emit black x color
    // = black. Scan() therefore ALSO repoints _EmissionMap at _MainTex
    // (the barcode itself): the yellow bars emit, the dark bars stay
    // dark, and because _EmissionMap is now _MainTex, scrollEmission
    // goes true and the glow scrolls in sync with the albedo. The color
    // is (1.0, 0.75, 0.125) — warm yellow, at room-light level.
    // Set ONCE in Scan() at level start, never per frame. Scoping is the
    // same as the scroll: per-renderer material instances matched by
    // texture name, so other om_signs_01a signs are untouched.
    //
    // Emission mask (glow mask, Nov 21; INVERTED Nov 21 fix): at 1.5
    // the glow washes out the scrolling animation - the bright bar
    // cores clip to white under the HDR glow color and bloom into the
    // gaps, so the bars read as one uniform glowing slab. The mask is
    // derived at level start from the albedo's own pixels
    // (BuildEmissionMask): white only on the BRIGHT bar cores
    // (brightness >= threshold), black on the dim troughs and the
    // black field. _EmissionMap then points at the mask instead of
    // the albedo, so only the cores emit and the scrolling stays
    // readable. (The first mask version used a band-pass [100,450)
    // that made the DIM troughs glow and the bright cores not glow -
    // that INVERTED the contrast and the panel still read as a solid
    // yellow strip. Flight Nov 21 ~20:27.) The mask is the same size
    // as the albedo (pixel-for-pixel), its _MainTex scale/offset are
    // copied onto the _EmissionMap slot, and scrollEmission is set
    // true for masked materials, so the mask scrolls in sync.
    // Fail-open: if the texture is unreadable or the mask build
    // fails, ApplyGlow falls back to _MainTex (today's behavior).
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

        // Glow (Step 1b): warm yellow — base yellow
        // (1.0, 0.75, 0.125) x intensity 1.0. Reads as live energy,
        // at room-light level. Only applied to matched materials.
        private static readonly Color kGlowColor = new Color(1.5f, 1.125f, 0.1875f);

        // Emission mask (glow mask, Nov 21; INVERTED Nov 21 fix): the
        // mask selects the BRIGHT bar cores to glow (brightness >=
        // kMaskBrightnessThreshold) and the dim troughs stay dark.
        // The original band-pass [100,450) INVERTED the contrast: it
        // made the dim trough rows glow brighter than the bright core
        // rows (measured on the real 64x64 texture: trough rows render
        // at R~1.5, core rows at R~0.6), so the panel read as a solid
        // yellow strip with no visible scrolling (flight Nov 21 ~20:27).
        // The cores ARE the bars and the troughs ARE the gaps - only the
        // cores glow, so the bars stay bright, the gaps stay dark, and
        // the scrolling reads. Threshold 450: 71% of bright-row pixels
        // glow, only 7% of dim-row pixels leak (measured). Tuning
        // parameter - the flight test decides the final value.
        private const int kMaskBrightnessThreshold = 450;

        // Emission color for MASKED materials only. Lower and more
        // saturated than kGlowColor (1.5): the cores are already bright
        // yellow from the albedo, so a full 1.5 on top clips them toward
        // white. 1.0 amber keeps them yellow. Unmasked materials still
        // use kGlowColor (1.5) - see ApplyGlow.
        private static readonly Color kMaskGlowColor = new Color(1.0f, 0.65f, 0.0f);

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
            public bool scrollEmission; // _EmissionMap scrolls in sync: it IS _MainTex (plain glow) or a mask derived from it
        }

        private static GameObject s_manager;

        // One mask per source texture - the mask is per-texture, not
        // per-renderer, so every renderer sharing a texture reuses the
        // same mask object. Cleared (and the old masks destroyed) when
        // a new manager starts, so a level reload never reuses a
        // stale mask.
        private static Dictionary<Texture2D, Texture2D> s_maskCache = new Dictionary<Texture2D, Texture2D>();

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

            // Clear the mask cache from any earlier level (the masks are
            // per-level; the manager dies with the scene but statics
            // persist). Destroy the old masks so they do not leak.
            foreach (Texture2D old in s_maskCache.Values)
            {
                if (old != null)
                    UnityEngine.Object.Destroy(old);
            }
            s_maskCache.Clear();

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

        // Make a matched material glow. If a mask is supplied, _EmissionMap
        // points at the mask (only the bright bar cores emit - the dim
        // troughs and the black field stay dark) and the color is
        // kMaskGlowColor (1.0 amber, so the cores stay yellow, not white);
        // otherwise it points at _MainTex as before (the barcode itself -
        // the stock map is black, and black x any _EmissionColor emits
        // nothing) and the color is kGlowColor (1.5). When a mask is
        // installed, the _MainTex scale/offset are copied onto the
        // _EmissionMap slot so the mask stays pixel-aligned with the
        // albedo. Called ONCE per material at scan time, never per frame.
        // Returns true if the glow was applied. Fail-open: any missing
        // property or exception leaves the material still scrolling,
        // just not glowing.
        private static bool ApplyGlow(Material m, Texture2D mask)
        {
            try
            {
                if (!m.HasProperty("_EmissionMap") || !m.HasProperty("_EmissionColor"))
                    return false;
                if (mask != null)
                {
                    m.SetTexture("_EmissionMap", mask);
                    m.SetTextureScale("_EmissionMap", m.GetTextureScale("_MainTex"));
                    m.SetTextureOffset("_EmissionMap", m.GetTextureOffset("_MainTex"));
                    m.SetColor("_EmissionColor", kMaskGlowColor);
                }
                else
                {
                    m.SetTexture("_EmissionMap", m.mainTexture);
                    m.SetColor("_EmissionColor", kGlowColor);
                }
                m.EnableKeyword("_EMISSION");
                return true;
            }
            catch (Exception ex)
            {
                Debug.Log("[TextureMotion] glow failed for '" + m.name + "': " + ex.Message);
                return false;
            }
        }

        // Build an emission mask from a source texture: white where the
        // pixel brightness (R+G+B) is >= kMaskBrightnessThreshold (the
        // bright bar cores), black otherwise (the dim troughs and the
        // black field). Same size as the source, so the mask is
        // pixel-for-pixel aligned with the albedo. The mask is named
        // "<source>_mask" and inherits the source's wrap/filter mode, so
        // it tiles the same way. Returns null on any failure - the caller
        // fails open to the unmasked glow. Logs the coverage so the
        // flight log settles whether the mask was built.
        private static Texture2D BuildEmissionMask(Texture2D src)
        {
            try
            {
                Color32[] px = src.GetPixels32();
                Color32[] maskPx = new Color32[px.Length];
                int glowing = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    int brightness = px[i].r + px[i].g + px[i].b;
                    byte v = (brightness >= kMaskBrightnessThreshold) ? (byte)255 : (byte)0;
                    if (v == 255)
                        glowing++;
                    maskPx[i] = new Color32(v, v, v, 255);
                }
                Texture2D mask = new Texture2D(src.width, src.height, TextureFormat.ARGB32, false);
                mask.name = src.name + "_mask";
                mask.wrapMode = src.wrapMode;
                mask.filterMode = src.filterMode;
                mask.SetPixels32(maskPx);
                mask.Apply();
                Debug.Log("[TextureMotion] mask built for '" + src.name + "': " + glowing + "/" + px.Length + " px glowing (" + (100.0f * glowing / px.Length).ToString("F1", CultureInfo.InvariantCulture) + "%, threshold >=" + kMaskBrightnessThreshold + ")");
                return mask;
            }
            catch (Exception ex)
            {
                Debug.Log("[TextureMotion] mask build failed for '" + (src != null ? src.name : "?") + "': " + ex.Message);
                return null;
            }
        }

        // Return the cached emission mask for a texture, building it on
        // first use. Null if the texture is null/not a Texture2D or the
        // build fails - the caller fails open to the unmasked glow.
        private static Texture2D GetOrBuildMask(Texture2D src)
        {
            if (src == null)
                return null;
            Texture2D mask;
            if (s_maskCache.TryGetValue(src, out mask))
                return mask;
            mask = BuildEmissionMask(src);
            if (mask != null)
                s_maskCache[src] = mask;
            return mask;
        }

        // FIX (Nov 21, 2026 — bisect: Step 1 clean, Step 2 broken):
        // Scan() used to read r.materials on EVERY renderer in the scene.
        // In Unity, Renderer.materials INSTANTIATES a copy of every
        // material on the renderer and renames each copy "X (Instance)" —
        // so one scan silently renamed every material in the level, and
        // the live Step 2 log proves it: the matched material printed as
        // 'om_signs_01a (Instance)'. Overload's own code then breaks two
        // ways (decompile: Assembly-CSharp, Unity 2017.4.10f1):
        // (1) the glow-dupe pattern — AlienPower, AlienSecretDoor,
        // AlienWarp, PropEmLight, PropMonitor, PropReactorTurret,
        // TimedSwitch, RobotMatcen — matches materials by EXACT name in
        // Start() (which runs AFTER this StartLevel postfix):
        // sharedMaterials[k].name == m_glow_materials[j].name.
        // "X (Instance)" never equals "X", so the animated glow
        // materials are never installed and the game's per-frame emission
        // writes go to orphaned dupes no renderer uses — the level's
        // light fixtures, switches, monitors, forcefields and matcens
        // lose their bright emission and the level renders dark/flat
        // (the key room reads as "nothingness"); (2) dupes installed in
        // Awake(), before this postfix (Forcefield, SimpleShield — the
        // shield pickup in the secret pit), are orphaned the other way:
        // Scan copies the dupe, the game keeps writing hit-flash to the
        // original dupe, the renderer shows the frozen copy. The missile
        // spawns after StartLevel, so Scan never touches it directly —
        // it breaks through this shared-material side effect. The fix
        // scans r.sharedMaterials (read-only: no instantiation, no
        // renaming — the same pattern Console.cs already uses) and
        // instantiates ONLY the matched material, by hand (new Material),
        // installed via sharedMaterials so the stock name is preserved
        // (set explicitly — no " (Instance)" suffix) and any later
        // name-based game code still matches. Every other renderer is
        // left exactly as loaded. NOTE for later steps: if an entry ever
        // matches a material a glow script manages, the instance wraps
        // that script's dupe — the scroll still works, but if the script
        // later replaces the instance with its own dupe, scrolling stops
        // for that renderer (not the case for energycenter1: the panel
        // renderer has no glow script).
        private static List<ScrollingMaterial> Scan(List<Entry> entries)
        {
            List<ScrollingMaterial> outList = new List<ScrollingMaterial>();
            // Marker string (checked by the verifier): this build scans
            // sharedMaterials only and never touches r.materials.
            Debug.Log("[TextureMotion] scan mode: sharedMaterials-only (fix Nov 21: no r.materials instantiation)");
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            foreach (Renderer r in renderers)
            {
                if (r == null)
                    continue;
                // Read-only: sharedMaterials does NOT instantiate (unlike
                // r.materials) — non-matching renderers are untouched.
                Material[] shared = r.sharedMaterials;
                for (int i = 0; i < shared.Length; i++)
                {
                    Material sharedMat = shared[i];
                    if (sharedMat == null || sharedMat.mainTexture == null)
                        continue;
                    string texName = sharedMat.mainTexture.name;
                    if (string.IsNullOrEmpty(texName))
                        continue;
                    foreach (Entry e in entries)
                    {
                        if (string.Equals(texName, e.name, StringComparison.OrdinalIgnoreCase))
                        {
                            // Instantiate ONLY this one matched material.
                            // new Material() copies the properties; the
                            // name is set explicitly so it stays the stock
                            // name (no " (Instance)" suffix). Installing
                            // via sharedMaterials is the game's own idiom
                            // (see DuplicateGlowMaterials) — later
                            // name-based game code still matches.
                            // (A shared material on several renderers gets
                            // one instance per renderer, each scrolling —
                            // that is the correct behaviour.)
                            Material m = new Material(sharedMat);
                            m.name = sharedMat.name;
                            Material[] arr = r.sharedMaterials;
                            arr[i] = m;
                            r.sharedMaterials = arr;
                            // Emission mask (glow mask, Nov 21; inverted
                            // Nov 21 fix): derive a mask from the albedo
                            // pixels - white only on the BRIGHT cores,
                            // black on the dim troughs and the black field -
                            // so only the bars glow and the gaps stay dark.
                            // Built once per texture and cached. Fail-open:
                            // if the texture is not a readable Texture2D,
                            // or the mask build fails, mask is null and
                            // ApplyGlow falls back to repointing
                            // _EmissionMap at _MainTex (today's behavior -
                            // the panel still glows, just unmasked).
                            Texture2D mask = GetOrBuildMask(sharedMat.mainTexture as Texture2D);
                            // Glow (Step 1b): make the panel emit. MUST run
                            // BEFORE scrollEmission is computed below.
                            // Fail-open: if the material has no emission
                            // properties, the panel still scrolls, it just
                            // does not glow.
                            bool glowing = ApplyGlow(m, mask);
                            // Diagnostic: log whether the mask was installed
                            // or fell back to unmasked, so the flight log
                            // settles it without another build.
                            if (mask != null)
                                Debug.Log("[TextureMotion] mask applied to '" + texName + "' (material '" + m.name + "')");
                            else
                                Debug.Log("[TextureMotion] mask NULL for '" + texName + "' - unmasked glow fallback");
                            ScrollingMaterial sm = new ScrollingMaterial();
                            sm.material = m;
                            sm.slideU = e.slideU;
                            sm.slideV = e.slideV;
                            // scrollEmission: the _EmissionMap offset scrolls
                            // in sync with the albedo when the map IS the
                            // albedo (plain glow, as before) OR when a mask
                            // was installed (the mask is pixel-for-pixel
                            // derived from the albedo, same size/UVs, so the
                            // same offset keeps it aligned). THIS FLAG IS THE
                            // CRITICAL DETAIL: if it is false for a masked
                            // material, the mask stays static while the
                            // albedo scrolls and the emission lands on the
                            // wrong parts of the bars.
                            sm.scrollEmission = glowing && m.HasProperty("_EmissionMap") &&
                                (mask != null || m.GetTexture("_EmissionMap") == m.mainTexture);
                            outList.Add(sm);
                            Debug.Log("[TextureMotion] scrolling '" + texName + "' (material '" + m.name + "'): slide_u=" + e.slideU + ", slide_v=" + e.slideV + ", speed=" + e.speed + (sm.scrollEmission ? " (+_EmissionMap)" : "") + (glowing ? " (+glow)" : " (no glow)"));
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
