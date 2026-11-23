# PlayShift First Person

Reusable first-person player for PlayShift simulators (Cut the Grass, Snow Cleanup, future games).

## What is here

- `PlayShiftFirstPersonPlayer.prefab`:
  - CharacterController
  - `PlayShiftFirstPersonRig`
  - Easy Peasy input and controller
  - `CameraParent` → `PlayerCamera` (MainCamera, the only AudioListener) → `ToolAnchor`
  - `GroundCheck`
  - No body, hands or arms.
- `Runtime/PlayShiftFirstPersonRig.cs` is the game-facing side of the controller:
  - pauses it while menus are open (`PlayShiftFirstPersonRig.InputBlocked`)
  - takes over for scripted control (`Suspended`)
  - feeds it `SpeedMultiplier` (upgrades), `VerticalFov` and `MouseSensitivityScale`
  - exposes yaw, pitch, velocity and the camera-relative `ToolAnchor` for a held tool
- `Editor/PlayShiftFirstPersonBuilder.cs` builds the prefab and tunes the controller. To rebuild it, use **PlayShift > First Person > Rebuild Player Prefab**.

Tuning:

| Setting | Value |
|---|---|
| Walk / sprint speed | 3.5 / 5.5 m/s (unlimited sprint) |
| Eye height | 1.7 m |
| Vertical FOV | 65 |
| Head bob | very subtle |
| Turned off | strafe tilt, sprint zoom, jump, slide, crouch, ledge grab, swimming |

## Easy Peasy First Person Controller (Asset Store)

The movement comes from Easy Peasy First Person Controller (Eys Store). Asset Store licences do not allow its files in this public repository, so the asset is listed in `.gitignore`. Import it into each project yourself:

1. Open **Window > Package Manager > My Assets**.
2. Select **Easy Peasy First Person Controller**.
3. Click **Download**, then **Import**. Accept the Input System dependency.

The code finds the asset by type name. Without the asset everything still compiles, and the game's built-in movement drives the same prefab. Expect one "missing script" warning per player until the asset is imported.

## Using it in another game

1. Copy `Assets/PlayShift/FirstPerson` into the project and import Easy Peasy.
2. Run **PlayShift > First Person > Rebuild Player Prefab**, then place the prefab in your scene.
3. When your menus open or close, set `PlayShiftFirstPersonRig.InputBlocked = () => yourMenuIsOpen;`.
4. Every frame, set `rig.SpeedMultiplier`, `rig.VerticalFov` and `rig.MouseSensitivityScale` from your game's upgrades and settings.
5. Attach the game's tool to `rig.ToolAnchor` (bottom-right of the view).
