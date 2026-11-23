# PlayShift: 5-level grass progression (Unity)

A drop-in Unity module for the lawn-cutting game. It has five levels of increasingly tall grass, exact
GRASS LEFT counting, LEVEL CLEARED and ALL LEVELS COMPLETED screens, and TikTok gift effects.

Copy `Assets/PlayShift/` into the game's `Assets/` folder. The `Tests~` folder is a .NET test project; Unity ignores it.

## Quick start: first-person test area

1. Requirements:
   - Unity 2021.3 or newer, with **URP** or the Built-in pipeline (not HDRP).
   - uGUI and TextMeshPro (default packages).
   - **glTFast** (Package Manager > Add package by name > `com.unity.cloud.gltfast`), so Unity can import the trimmer `.glb`.
   - A shader model 4.5 GPU (DX11+, Metal, Vulkan).
2. In an empty scene, run **PlayShift > Grass > Create Test Area (first-person)**. It builds:
   - a 24 x 24 m test lawn with 1,000,000 grass units, starting on Level 3 (60-80 cm)
   - a first-person player holding your trimmer (no hands, arms or body)
   - grass clippings and trimmer audio
   - a cloud sky, a sun with soft shadows, haze, and trees and bushes around the perimeter
3. Press Play.

| Control | Action |
|---|---|
| Hold Left Mouse | Run the trimmer |
| Space | Toggle the trimmer on/off hands-free (for streaming) |
| WASD / arrows | Walk |
| Mouse | Look |
| Shift | Sprint |
| Esc | Release the cursor |

To test other heights, change **GrassTestArea > GrassLevelManager > Start Level**. To test performance, change **GrassFieldRenderer > Quality** (Low, Medium, High or Ultra).

To add the grass system to an existing scene instead, use **Create Level Setup (existing scene)**. Then give your player a `GrassCutter`, plus `TrimmerBlades` and `TrimmerViewmodel` for first-person.

## How the cutting feels

- **Blades**: `TrimmerBlades` spins up and coasts down smoothly, timed by `deltaTime` so it is frame-rate independent. At speed, a soft motion-blur ring fades in over each head. It supports any number of heads.
- **Grass reaction**: this runs on the GPU in `GrassBlade.hlsl`, with no per-blade physics.
  - Uncut blades near a head lean away from it, more for tall grass and while the blades spin, and spring back when it leaves.
  - Wind gusts roll across the lawn, and tall grass sways more.
  - Roots never move: each blade is a constant-length arc, so tall grass keeps its upright height.
- **The cut**: the cutter sweeps a capsule from each head's previous position to its current one every frame. Cells under the blades are cut exactly once, GRASS LEFT drops by exactly their units, and each cell records the time it was cut. The shader then:
  - shortens those blades to stubble over 0.18 s, with a small kick away from the head. The grid is 5 cm (1 unit per cell on a 50 m lawn), so cut edges follow the round head in smooth curves, not square chunks.
  - leaves short, bright-green lawn with paler cut ends, never bare dirt
  - keeps a crisp border between cut and uncut grass

  Cut state lives in the logical grid, so it persists when you walk away and come back.
- **Clippings**: `GrassClippings` uses one pooled `ParticleSystem` to throw small green and yellow slivers off the blade, following the blade rotation and the head's movement. Thick, tall grass produces more and short grass fewer. They tumble, land and fade, with no snow, smoke or big debris.
- **Trimmer feel**: `TrimmerViewmodel` places the head on the ground about 1.4 m ahead, in the lower-middle of the screen. The long shaft runs diagonally out of the bottom-right corner; it is the model's shaft stretched 1.7x, while the head keeps its real size and cut radius. It adds:
  - smooth turning lag and a gentle left/right operator sweep
  - walk sway and an idle hum
  - stronger vibration and a slight pull-back in thick grass, which also slows walking

  The camera itself never shakes.
- **Audio**: `TrimmerAudio` synthesises the motor in code, so it is an original sound with nothing to license. Its pitch follows blade speed, which gives natural start-up and shutdown, and it bogs down and gets raspier under load. The cutting rustle is heavier in thick grass. Optional slots accept your own licensed clips for start-up, motor loop, shutdown, light and thick cutting, and clippings.

## Gameplay modifiers (for future gifts and power-ups)

`GrassLevelManager.Modifiers` is a set of timed, stackable modifiers that knows nothing about TikTok:

| Modifier | Effect |
|---|---|
| SpeedBoost | Faster walking and blade spin |
| RadiusBoost | Wider cut |
| Slow | Slower walking and blades, never below 20% |
| MultiBlade | Extra heads on a crossbar, up to 3 |

Regrowing cut grass is `RegrowGrass` (the same as an Add Grass gift). Gift rules in the settings asset can trigger any of these (`SpeedBoost`, `RadiusBoost`, `SlowTrimmer`, `MultiBlade`, with a duration), and `GrassLevelManager` exposes matching methods for power-ups or UnityEvents.

## Performance

- No GameObject, Rigidbody or Animator exists per blade or per unit. Grass is one static blade buffer plus one small per-cell buffer.
- Cutting uploads only the changed slice of cells. Each head costs one capsule test per frame.
- Blades are scattered by area (blades per m2, separate from the 5 cm cut grid) and chunked 2 x 2 m, with frustum and distance culling and three distance LODs.
- Each level draws a density fraction (Level 1 = 62% up to Level 5 = 100%).
- Only chunks near the camera cast shadows.
- Quality presets (Low, Medium, High, Ultra) set blade density (380 / 600 / 950 / 1500 per m2), the LOD distances and the shadow-casting distance.
- Clippings use one pooled particle system.
- Rendering and gameplay don't depend on video capture.

## Trimmer model

`Assets/PlayShift/Models/Trimmer.glb` is a cleaned copy of the supplied Tripo model, `lawn trimmer 3d model.glb`:

- **Cleaned**: two stray pieces of the generated mesh were removed (a tiny duplicate trimmer and a detached handle fragment).
- **Optimized**: reduced from 1.85M to about 47k triangles, with textures at 2048 px (4.3 MB instead of 61 MB).
- **Re-oriented**: the origin is now the bottom centre of the cutting head, it faces +Z, and the handle is tilted up.
- **Spinning head**: the blade head is a separate child, `CutterHead`. `TrimmerViewmodel` detaches it so it stays level on the ground, and `TrimmerBlades` spins it.

Unity needs a glTF importer for `.glb` files: install **glTFast** (Package Manager > Add package by name > `com.unity.cloud.gltfast`).
The test area uses the model at scale 1.85 (about 1.5 m long). The cut radius is 0.65 m, which matches the blade tips at that scale.

## Levels and configuration

All values live in the settings asset:

| Level | Default height | Starting units |
|---|---|---|
| 1 | 15-25 cm | 1,000,000 |
| 2 | 35-50 cm | 1,000,000 |
| 3 | 60-80 cm | 1,000,000 |
| 4 | 90-110 cm | 1,000,000 |
| 5 | 120-150 cm | 1,000,000 |

- **Levels**: name, min/max height and starting units for each level. The last entry is the final level, so adding or removing entries changes the campaign length.
- **Lawn layout**: lawn size in metres and the logical cell grid. The default 250 x 250 grid gives 16 units per cell. Totals stay exact for any grid size because the remainder is spread across cells.
- **Absolute max height**: the upper limit for "taller grass" gifts (220 cm by default).
- **Gift table**: see below.

`GrassLevelManager.startLevel` lets you jump straight to a taller level while testing.

## How it works

```
GrassProgression  (Core, engine-free; 5-level state machine and level timer)
  └─ GrassField   (Core; logical units per cell, heights, cutting, gift operations)
GrassLevelManager (scene entry point; ticks time, binds the renderer and HUD, NEXT LEVEL / PLAY AGAIN)
GrassFieldRenderer + InstancedGrass.shader (GPU instanced blades)
GrassCutter       (on the mower; swept-capsule cutting)
GrassHud          (GRASS LEFT, timer, gift feed, LEVEL CLEARED, ALL LEVELS COMPLETED)
TikTokGiftReceiver (thread-safe gift queue, routes gifts through TikTokGiftRouter)
```

- **Logical units are separate from rendering.** GRASS LEFT counts units stored in a fixed grid of
  cells, using two arrays (units and height per cell). No GameObject is created per unit or per blade.
- **Cutting** sweeps a capsule from the mower's previous position to its current one each frame, so a fast
  mower or a low frame rate never leaves uncut strips. A cell's units are removed when the deck covers its centre.
- **Rendering**: one static GPU buffer holds every blade (about 500k by default). A second buffer holds one
  height per cell, where 0 means cut. Cutting uploads only the changed slice of that buffer. Blades are
  drawn per chunk with `Graphics.DrawMeshInstancedProcedural`, with frustum culling and distance LOD:
  distant chunks draw fewer, wider blades so the lawn still looks full. All five levels use the same
  buffers; starting a level only re-uploads the cell heights.
- **Natural bending**: each blade is a constant-curvature arc, so it keeps its length while it bends.
  Short grass stands almost upright. Taller grass curves more and droops, sways more in the wind, and gets
  only slightly wider (width grows with the square root of height), so 1.5 m grass does not look like giant
  rigid spikes. Cut cells show as short, paler stubble.
- **Level clear**: when GRASS LEFT reaches 0, the field freezes (no more cutting and no gift effects), the
  timer stops, and the LEVEL CLEARED screen shows the completion time, the total grass cut (mower and gifts)
  and a NEXT LEVEL button. NEXT LEVEL resets the same field with the next level's taller grass and GRASS
  LEFT back to 1,000,000. Clearing the final level shows **ALL 5 LEVELS COMPLETED** with a per-level
  results table and a PLAY AGAIN button.

## TikTok gifts

Gift effects come from the settings asset's gift table. Each rule maps a gift name to **Add Grass**
(units), **Remove Grass** (units) or **Increase Height** (cm). By default a rule is applied once per gift
in a combo. Gifts work on every level while it is being played; they are ignored on the clear screens.

- **Add Grass** first regrows cut cells as one round patch. Once the whole lawn has regrown, it thickens
  cells, up to 4x their normal unit count. GRASS LEFT goes up by exactly the amount added.
- **Remove Grass** clears one round patch. If it empties the lawn, the level is cleared.
- **Increase Height** makes all current grass taller and raises the height range for any regrowth. The
  bonus is limited by the absolute max height and resets when the next level starts.

Connect your TikTok LIVE connector to `TikTokGiftReceiver`. Both calls can be made from any thread:

```csharp
receiver.EnqueueGift(userName, giftName, repeatCount, isStreakInProgress);
receiver.EnqueueGiftJson(json); // TikTok-Live-Connector / TikFinity style gift payload
```

Streak gifts (giftType 1) arrive many times while a combo runs. Only the final event (`repeatEnd`) is
applied, so a combo is not counted twice. To test without a live stream, press **F5 / F6 / F7** in Play
mode to simulate add / remove / taller gifts.

## Tests

The engine-free core (levels, field, cutting, progression, gifts) has 33 unit tests:

```
dotnet test PlayShift/Tests~/PlayShift.Grass.Core.Tests
```

They cover these cases:

- exactly 1,000,000 units for any grid size
- each level's heights stay inside its range
- exact cut accounting, including no double counting
- fast-mower sweeps leave no gaps
- the clear event fires exactly once, and the counter and timer freeze afterwards
- level 1 through 5 progression and the final victory event
- every gift operation and edge case, including streak handling and the height cap

## Browser preview (progress videos)

`Preview~/` is a three.js copy of the module, used to record progress videos without a Unity editor.
`grass-core.js` is a port of the C# core, and the blade shader is a GLSL port of `InstancedGrass.shader`
with the same bending, wind and width maths. Its chunked LOD works the same way as `GrassFieldRenderer`.
It renders a scripted run through all five levels frame by frame and encodes an MP4:

```
cd PlayShift/Preview~ && npm install && node record.js out/progress.mp4
```

Pass `--query "plan=shot&level=5&cam=low"` to render a short clip of one level. The trimmer in the preview is a placeholder model.

## Known limitations

- The renderer, shader and UI scripts compile against the UnityEngine 2021.3 API, but this module has not
  yet been run inside the actual game project. Check the look and frame rate in your scenes and tune the
  material and `GrassFieldRenderer` density and LOD settings.
- Grass does not cast or receive real-time shadows. It uses its own lighting from the scene's sun, with
  wrap lighting and back-light translucency.
- In URP, grass is not written to the depth prepass, so SSAO and depth-of-field ignore it.
