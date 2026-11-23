# PlayShift: 5-level grass progression (Unity)

A drop-in Unity module for the lawn-cutting game. It has five levels of increasingly tall grass, exact
GRASS LEFT counting, LEVEL CLEARED and ALL LEVELS COMPLETED screens, and TikTok gift effects.

Copy `Assets/PlayShift/` into the game's `Assets/` folder. The `Tests~` folder is a .NET test project; Unity ignores it.

## Quick start

1. Requirements: Unity 2021.3 or newer, Built-in pipeline or URP (not HDRP), with uGUI and TextMeshPro (both are default packages). The GPU must support shader model 4.5 (DX11+, Metal, Vulkan, GLES 3.1+).
2. Select your mower in the scene, then run **PlayShift > Grass > Create Level Setup**. This:
   - creates `Assets/PlayShift/Grass/Generated/GrassProgressionSettings.asset` and `InstancedGrass.mat`
   - adds a `GrassLevel` object (manager, renderer and TikTok gift receiver) plus a ground plane
   - adds a `GrassCutter` to the selected mower
   - imports TMP Essential Resources if they are missing

   To try it in an empty scene, use **Create Level Setup + Test Mower** instead (WASD/arrows to drive, Shift for a speed boost).
3. Move `GrassLevel` so its origin sits at the lawn's corner. The lawn covers local X 0 to 50 m and Z 0 to 50 m by default.
4. Press Play. Level 1 starts with **GRASS LEFT 1,000,000**.

## Levels and configuration

All values live in the settings asset:

| Level | Default height | Starting units |
|---|---|---|
| 1 | 15-25 cm | 1,000,000 |
| 2 | 30-45 cm | 1,000,000 |
| 3 | 50-70 cm | 1,000,000 |
| 4 | 80-110 cm | 1,000,000 |
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

## Known limitations

- The renderer, shader and UI scripts compile against the UnityEngine 2021.3 API, but this module has not
  yet been run inside the actual game project. Check the look and frame rate in your scenes and tune the
  material and `GrassFieldRenderer` density and LOD settings.
- Grass does not cast or receive real-time shadows. It uses its own lighting from the scene's sun, with
  wrap lighting and back-light translucency.
- In URP, grass is not written to the depth prepass, so SSAO and depth-of-field ignore it.
