# GlobalFog fix — disable the GlobalFog image effect on D3 sky levels (sky-circle fix, Option B)

## Flight / Bug Diagnosis Header

- **Log verified:** magenta flight log `output_log_20261124_magentaflight.txt` — `[SKY PROBE]` ticks show `FOG fog=False mode=ExponentialSquared color=(0.11,0.11,0.11,1.00) density=0.0050` on every tick and `farClip 200.0 -> 800.0` (SkyFarClip patch). `GlobalFog` = **0 references** in the `Assembly-CSharp` decompile (it lives in `Assembly-CSharp-firstpass.dll`, which is why it was missed). The big model fed the real L1 dome mesh (`d3_sp_level01_sky_dome.dmesh`) + textures through the disassembled `Hidden/GlobalFog` Pass 1 shader and reproduced the flight video shape-for-shape; Bob viewed the simulation image (`~/workspace/sky-fix/globalfog_proof.png`) — polygonal clear window following the view centre, dull gray-brown outside, terrain unchanged; with the shader pass off the sky is uniformly vibrant.
- **Trigger anchor:** Jason's Nov 25 ~11:08 flight on L1.sp 516a6382 (emission restored, sky vibrant again): the circle is still there, the headlight does nothing to the sky, post-processing on/off changes nothing, and (Nov 25 ~11:15) the circle has **no effect on the terrain at all** — sky-only. That last observation kills every full-screen image effect and points at the sky's own render path.
- **Control check:** the new build was verified on disk before deploy (see "Verification" below): the built DLL contains the `GlobalFog` string lookup, the `[SKY FIX]` disable/toggle log strings, the farClip code, and `SkyProbe.cs` intact; the old "building sky sphere mesh" log string is absent. The L1.sp (516a6382) and L2.sp (6e1a7918) hashes are unchanged by this job (DLL-only). The flight verdict is the control that matters now: circle gone = fix proven.
- **Proven vs. Open:**
  - Proven: `GlobalFog` (`UnityStandardAssets.ImageEffects.GlobalFog`) is permanently enabled on the Main Camera (`m_Enabled=1` in the player ship prefab), ignores `RenderSettings.fog=false`, and its shader snaps `fogFactor < 0.05` to 1.0 (zero fog) at 346.2 m planar view-space depth (density 0.0050) — so the 511 m dome is clear straight ahead and 70–95% gray-fogged off-axis. Terrain (20–60 m) never reaches the cliff. The post-processing toggle never touches it. The skybox build was immune because it rendered in the Transparent queue (>2500, after `[ImageEffectOpaque]` GlobalFog); the dome is opaque queue 2000, so GlobalFog runs on it. Stock farClip 200 never reached 346 m; the SkyFarClip patch to 800 exposed the cliff.
  - Proven: D3's own room fog (orange fire rooms 44/45, pink start corridors) is a **separate mechanism** — it is implemented as in-level fog-carrier entities (PROP:N0000_MINE index 5 ORANGE / index 6 PINK, fog prefabs placed from the .d3l per-room fog data; `converters/d3_mp_converter.py` ~L19110–19211), not via `RenderSettings.fog` and not via the GlobalFog image effect. This fix only flips the camera Behaviour's `enabled` flag, so D3 room fog is untouched. (Flight check still owed: fire rooms orange, corridors pink.)
  - Open: flight verdict — does the circle vanish on the next flight, and does the sky stay uniformly vivid on a 360° pan/roll?
  - Open: the F7 toggle is a debug key (unbound in Overload — F8/F9/F10 are default-bound game controls, F7 is not referenced by the game). It stays in the build as a diagnostic; remove it in a later cleanup if it ever conflicts.

## What this job did

DLL-only. One line of behaviour (Option B) in `SkyFarClip.cs` `OnLevelLoaded`, inside the existing D3-sky-present branch that already gates the farClip patch: when a D3 sky is detected (`HasSkyObjects()` — sm_* renderers with a D3 sky texture or huge bounds), look up the `GlobalFog` Behaviour on `Camera.main` by **string** (`GetComponent("GlobalFog")`, so `GameMod.csproj` needs no new assembly reference to `Assembly-CSharp-firstpass.dll`), log found/enabled, and set `enabled = false`. Stock levels with no D3 sky keep GlobalFog untouched. A debug key (F7) toggles it live mid-flight + logs, so Jason can prove the cause if the circle somehow persists. The ticker re-asserts the disable until it has run once with a live camera (covers a null `Camera.main` at level load).

P1 (included, clean): the parked 700 m magenta diagnostic sphere is no longer built (`SkyboxFix.cs` `Apply()` logs "sky sphere NOT built (magenta diagnostic parked -- GlobalFog fix)" and returns). The camera tweaks (occlusion culling off, black background) and the stale-skybox clear still run. `BuildSkySphereMesh` / `LogSphereProbe` / `SkyboxFixFollower` remain in the file unused so the diagnostic can be re-enabled if needed. The sphere was invisible/occluded dead weight (zero magenta in flight).

Must-keep (verified): the SkyFarClip farClip 200→800 patch is untouched; `SkyProbe.cs` is untouched (still compiled in, `[SKY PROBE]` strings present); L1.sp 516a6382 and L2.sp 6e1a7918 untouched; no reconvert, no converter work.

## Build note (read this before the next GameMod build)

`dotnet build` on this machine failed with MSB3644: the .NET Framework 3.5 reference assemblies are not installed (no targeting pack, no MSBuild, no `mcs`). The fix, committed in this job, is a build-only `PackageReference` in `GameMod/GameMod.csproj`:

```xml
<PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.net35" Version="1.0.3" />
```

It provides compile-time reference assemblies only and does not change the compiled DLL's target framework or runtime behaviour. With it, `dotnet build GameMod\GameMod.csproj -c Release` succeeds (0 errors; 2 pre-existing warnings in `GuidebotSteering.cs` CS0162 and `MPLevelSelect.cs` CS0414, unrelated to this job). NuGet restore works on DarkCort (verified: the package restored in ~1.5 s).

One mistake made and fixed during this job: a CRLF-normalisation one-liner truncated all three edited files to zero bytes (the write handle truncated before the read ran). They were restored from git and the edit script re-run; the diff was re-verified before building. Do not run a read/write one-liner on a file in a single expression — read fully first, then write.

## Verification (all on disk, before deploy)

- Build: `dotnet build GameMod\GameMod.csproj -c Release` — RC=0, 0 errors.
- Built DLL (`GameMod\bin\Release\GameMod.dll`, 1,225,728 B): MD5 `5a608ff020dae10043c6b5f9300782a8`, SHA-256 `580b633836202bb3522d987b826305703b4323b5f427b4bee6e1b0b722c218e3`.
- String check (UTF-16LE literals in the binary): PRESENT — `GlobalFog`, `disabled Main Camera GlobalFog`, `F7 GlobalFog toggled`, `sky sphere NOT built`, `[SKY FIX]`, `[SKY PROBE]`, `farClipPlane`. ABSENT — old `building sky sphere mesh` log string.
- Live DLL backed up BEFORE overwriting: `C:\Users\admin\GameMod-backups\GameMod.dll.0ec297ef.bak`, MD5 `0ec297ef3e9c2d2366a868ea428a8b24` (the magenta DLL).
- Game confirmed NOT running first (tasklist clean — never killed).
- Deployed to `C:\Program Files (x86)\Steam\steamapps\common\Overload\olmod-0.5.14\GameMod.dll` ONLY; deployed MD5 `5a608ff020dae10043c6b5f9300782a8` = staged hash.
- L1.sp `516a6382` and L2.sp `6e1a7918` re-hashed after deploy — unchanged.
- Diff: exactly 3 files (`GameMod/SkyFarClip.cs`, `GameMod/SkyboxFix.cs`, `GameMod/GameMod.csproj`), +114/−10. Branch `globalfog-fix`, commit (see below). No merge to main.

## Flight list for Jason

Copy the game log before relaunching (it overwrites every launch). Fly outside in L1:

1. Pan 360° and roll so Mars and the nebula sweep from the crosshair to the screen corners — the entire sky must stay uniformly vivid with **zero** brightness drop or polygonal boundary at any angle.
2. Fire rooms 44/45 still orange and the start corridors still pink (D3 room fog intact).
3. Dome still fully visible (farClip 800 intact); terrain occlusion against the sky still exact.
4. The log should show `[SKY FIX] GlobalFog found=True enabled=True` then `[SKY FIX] disabled Main Camera GlobalFog (D3 sky present)` at level load, and no `SkyboxFix_Sphere` / magenta lines.
5. If the circle somehow persists, press **F7** mid-flight to toggle GlobalFog live and watch whether it vanishes — that settles the cause on the spot. (F7 is unbound in the game; it does nothing else.)

## Action Split Footer

- **You:** fly L1 outside and run the flight list above; tell Bob whether the circle is gone and whether the fire rooms are still orange / corridors still pink. If the circle persists, press F7 once mid-flight and say what happened.
- **Coding agent:** (done) Option B fix in `SkyFarClip.cs` + F7 toggle, magenta sphere parked in `SkyboxFix.cs`, build-only net35 PackageReference in `GameMod.csproj`; built, verified on disk, backed up the old DLL, deployed to olmod-0.5.14 only, committed + pushed on branch `globalfog-fix`. Do not touch: L1.sp/L2.sp, the converter repo, the sky-fix editor, Tier 2 work.
- **Muse:** verify the deployed DLL hash and the branch/commit on disk (done — see Verification above); hand Jason the flight list; after his flight, record the verdict and, if the circle is gone, close the sky-circle item (the magenta sphere removal and the F7 key are already in this build).
