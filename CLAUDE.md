# PlayShift "Cut the Grass" — rules for Claude sessions

The Unity project is `PlayShift/` (Unity 6000.6.10f1, Built-in render pipeline). Branch:
`claude/grass-progression-levels-2ok54e`. See `PlayShift/HANDOFF.md` for the architecture and how to capture/test.

## Owner's standing rules
- First person only. No hands or arms, ever.
- Never commit Easy Peasy First Person Controller files (the repo is public).
- Never touch the PlayShift-Launcher, PlayShift_Home_Dashboard_Launcher or Clear the Snow projects.
- Performance first: low-end PCs running OBS / TikTok LIVE Studio. Measure every visual change.

## Permanent design decisions (do not undo)
- **The trimmer never lifts.** No dynamic trimmer lifting from camera pitch (or anything else): the cutting head
  stays at the cutting height (3 cm blade clearance) over the ground at every camera angle. The camera's up/down
  look never moves the tool, the cutting height or the cutting radius. (The lift code was removed on purpose;
  do not reintroduce it.)
- **Camera:** default pitch -24 deg; look down to -34 (10 deg below default), up to -10 (14 deg above default; +11
  was "way too high"); unrestricted 360 deg left/right; vertical mouse speed = horizontal (2); eye height 2.28 m;
  FOV from the game settings (default 62). The whole trimmer stays on screen across the range. It never lifts or
  moves with the look. No bob/sway/roll.
- **Trimmer pose:** the handle is anchored just past the bottom-right corner of the view (`gripFollowsView` 1,
  `viewGrip` 0.85, -0.75, 0.5 in camera space), so the yellow body always runs out of frame as if held below/right
  of the camera (no hands, never a floating end cap). The cutting head stays on the ground under screen point
  (0.56, 0.32) at the default pitch (2.6 m ahead, 3 cm clearance); only the shaft angle follows the look. Blades
  flat and level, rotate rigidly about the hub; release = immediate stop.
- **No sprint.** One movement speed: walk 7.2 m/s (sprint speed is forced equal to it); acceleration 70 /
  deceleration 80 m/s2.
- **Grass interaction:** grass only bends where solid steel is (hub/plate, shaft, and the four blade strips while
  stopped). No lean-away field or flattened circle around the head. Grass disappears only when cut.
- **Clippings:** thin tapered blade-shaped mesh particles in the cut grass's colour, emitted only when grass is
  actually cut. No square/quad cards.
- **Blade spin look:** drawn at a steady 3.6 rev/s (max 26 deg per frame) with the blur disc showing speed; real
  9.5 rev/s drives cutting/sound. Do not let the drawn step approach 45 deg (wagon-wheel stalling).
- **Cut vs uncut contrast:** uncut grass is a dark, rich green (blade palette in InstancedGrass.mat, ground under it
  follows the blades' average); the mown lawn has its own light colour (`_CutColor` on PlayShift/GrassGround, about
  #79B744 on screen), driven by the cut mask. Keep the two clearly different at every distance.
- **Title screen:** its own shell-grass lawn (`MenuLawn`), no gameplay blades, untouched-lawn look, saves
  untouched, 60 FPS cap.
