# PlayShift Grass — handoff for the Claude session running on the owner's PC

You are continuing work started in a cloud session (no Unity available there). Read this whole file first.

## Where things are
- Repo: `https://github.com/trentonbarrows02-cell/Discord-Server-Cloner-2x`, branch **`claude/grass-progression-levels-2ok54e`**.
  (The repo's root is an unrelated Discord tool; ignore it. Everything for the game is in `PlayShift/`.)
- `PlayShift/` is a complete Unity project (Built-in render pipeline by default; URP shader also provided).
  `Packages/manifest.json` pins glTFast 6.10.1 (for the trimmer `.glb`), TextMeshPro 3.0.9, uGUI 1.0.0.
- On this PC: clone to **`C:\PlayShiftGames\ClearTheGrass`** (outside OneDrive — Unity's Library folder breaks OneDrive sync).
- **Never touch** the folders `PlayShift-Launcher` or `PlayShift_Home_Dashboard_Launcher` (the owner's launcher /
  TikFinity integration). The game must not modify or break them.

## What the game is
A first-person TikTok LIVE grass-cutting game. Five levels, each starts with exactly **1,000,000** logical grass
units; GRASS LEFT counts down as grass is cut; at 0: LEVEL CLEARED screen (time + grass cut, NEXT LEVEL), level 5
ends with ALL 5 LEVELS COMPLETED. Heights: L1 15–25 cm, L2 35–50, L3 60–80, L4 90–110, L5 120–150 (each level also
denser). TikTok gifts can add/remove/regrow grass, make it taller, and trigger timed trimmer modifiers.

Owner's non-negotiables (from their brief):
- First-person only. **No hands, arms, gloves or body ever.** The trimmer floats in its operating position: long
  shaft entering diagonally from the **bottom-right corner**, cutting head (3 blades) in the **lower-middle** of the
  screen, close to the grass. Blades spin (individually animated head, not the whole tool), spin-up/down, slight
  motion blur, vibration while cutting, no camera shake.
- Grass: dense individual 3D blades, mostly yellow-green and golden-green with darker greens mixed in, upright
  (keeps its real height), subtle wind, shadows. Not low-poly/Roblox-looking.
- Cutting: grass bends from the head, shortens in ~0.1–0.2 s, leaves **short vibrant-green lawn (never dirt)**,
  smooth curved cut paths (no square chunks), small green/yellow clippings (no snow/smoke), counter drops at once
  and accurately, no double counting, cut state persists.
- UI: big "GRASS LEFT" white heading, huge **green numbers with thick black outline** + shadow, no dark box,
  smooth count-down, small level indicator, scales with resolution.
- Bright outdoor scene: blue sky + clouds, varied trees/bushes, gravel paths, terrain variation.
- Performance: GPU instancing, chunks, culling/LOD, no per-blade GameObjects, quality settings, 60 FPS target.
- Show progress with **screenshots / short clips only** — never long video renders.
- The owner mentioned a TikTok reference screenshot; it was never received. Ask them for it if you need it.

## Architecture (all under `PlayShift/Assets/PlayShift/`)
- `Grass/Core/` (engine-free C#, asmdef `PlayShift.Grass.Core`, unit-tested):
  `GrassField` (units per 5 cm cell, heights, cut timestamps, swept-capsule cutting, add/remove/regrow, height
  gifts, dirty ranges), `GrassProgression` (5-level state machine, timer, results), `TikTokGifts` (rule router),
  `TrimmerModifiers` (speed/radius/slow/multi-blade timed modifiers), `GrassLevelDefinition` (levels + layout:
  default 1000×1000 cells on 50 m = 1 unit/cell).
- `Grass/Runtime/`: `GrassLevelManager` (entry point, sets `Field.Clock`), `GrassFieldRenderer` (blade buffer by
  area per m², per-cell `(height, cutTime)` buffer, 2 m chunks, LOD, quality presets, shadow distance, picks URP or
  Built-in shader), `GrassCutter` (multi-head capsule cutting, gated on blade speed, `Load`, `GrassCut` event,
  publishes head positions to the shader), `FirstPersonController`, `TrimmerViewmodel` (floating trimmer; shaft
  stretched 1.7×; head detached so it stays level; sweep/sway/vibration/pull-back; extra heads for multi-blade),
  `TrimmerBlades` (spin, blur ring, vibration), `GrassClippings` (pooled ParticleSystem), `TrimmerAudio`
  (synthesised motor/cut sounds + optional clips), `TestAreaEnvironment` (terrain, lawn, gravel path, trees),
  `TikTokGiftReceiver` (thread-safe gift queue, F5/F6/F7 debug gifts), `UI/GrassHud` (counter + screens),
  `GrassProgressionSettings` (ScriptableObject with all tunables).
- `Grass/Shaders/`: `GrassBlade.hlsl` (shared blade builder: cut animation, push-away, wind, colour),
  `InstancedGrass.shader` (Built-in), `InstancedGrassURP.shader` (URP), `SkyClouds.shader` (skybox).
- `Grass/Editor/`: `GrassSetupMenu` (PlayShift > Grass > Create Test Area / Create Level Setup),
  `FirstOpenSetup` (auto-builds `Assets/PlayShift/Scenes/GrassTestArea.unity` on first open;
  `BuildForBatch` for command-line use).
- `Models/Trimmer.glb`: owner's trimmer (Tripo model), cleaned (stray pieces removed), 1.85M → ~47k tris,
  origin = bottom centre of cutting head, faces +Z, child `CutterHead` spins.
- `PlayShift/Tests~/` — .NET xUnit tests for Core (39 passing): `dotnet test PlayShift/Tests~/PlayShift.Grass.Core.Tests`.
- `PlayShift/Preview~/` — three.js browser copy used to make screenshots without Unity
  (`testarea.html/js` = the first-person test area; `record.js` renders frames). Its tuned values are the
  reference for the look: VM head offset (0.05 right, 1.4 m ahead), shaft pitch 17°, handle swing 34°, shaft
  stretch 1.7, trimmer scale 1.85, cut radius 0.65 m, cut animation 0.18 s, stubble 4 cm.

## Permanent design decisions

These are owner decisions; keep them (details in the repo-root `CLAUDE.md`):
- The trimmer never lifts off the ground; camera pitch never moves the tool, the cutting height or the radius.
- Camera: default -24 deg, down to -34, up to +11, 360 deg yaw, vertical mouse speed = horizontal, eye 2.28 m.
- Cut lawn clearly lighter (own colour) than the dark, rich uncut grass at every distance.
- No sprint; walk 7.2 m/s.
- Grass bends only under solid steel (hub, shaft, stopped blades); no circular "force field".
- Clippings are thin blade-shaped mesh particles, only when grass is cut.
- Title screen uses its own shell-grass lawn (`MenuLawn`), capped at 60 FPS.

## Status
- Verified: Core logic + tests; all C# compiles against UnityEngine 2021.3 reference assemblies (stubs for UI/TMP).
- **Never run inside Unity.** Shaders, scene setup, lighting, glTF import, input, audio and real-time feel are
  untested. Expect first-open issues — that is your first job.

## Your tasks, in order
1. Follow `PlayShift/SETUP_ON_PC.md`: find Unity, clone to `C:\PlayShiftGames\ClearTheGrass`, run
   `-executeMethod PlayShift.Grass.Editor.FirstOpenSetup.BuildForBatch` with a log file, fix every compile /
   shader / package / import error until the log says `BATCH SETUP OK`, commit + push fixes to the branch.
2. Open the project in the Unity editor with the test scene, enter Play mode, and capture screenshots of the Game
   view (use the editor's screenshot API or `ScreenCapture.CaptureScreenshot` via a small editor script driving
   a scripted walk if needed): tall uncut grass, the first-person trimmer, spinning head, active cutting with
   clippings, cut edge, fresh-cut lawn, GRASS LEFT counter. Check: grass renders (not pink/missing), trimmer in the
   right place (bottom-right shaft, head lower-middle), cutting reduces the counter, cut lawn is bright green.
   Check frame rate (Stats window / profiler) at Low/Medium/High quality.
3. Fix whatever looks wrong vs the requirements above, re-screenshot, and show the owner. Iterate.
4. Then (owner's order): verify all 5 levels, LEVEL CLEARED / NEXT LEVEL flow, gift modifiers (F5/F6/F7),
   performance tuning. Known rough spots: meadow outside the lawn is textured ground (no grass); the trimmer's
   loop handle is large in the bottom-right.
5. Commit and push after each meaningful step. Keep the owner informed with screenshots.
