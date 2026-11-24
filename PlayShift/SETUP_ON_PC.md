# Set up PlayShift Grass on this PC (instructions for a Claude Code session running on the PC)

Goal: get the PlayShift Grass Unity project from GitHub onto this Windows PC, set it up with the installed
Unity editor, fix any errors Unity reports, push the fixes, and leave the test scene open ready to press Play.

Rules:
- Do NOT touch the folders `PlayShift-Launcher` or `PlayShift_Home_Dashboard_Launcher` (the owner's launcher).
- Put the Unity project OUTSIDE OneDrive (Unity's Library folder is huge and breaks OneDrive sync):
  use `C:\PlayShiftGames\ClearTheGrass`.

Steps:
1. Find the installed Unity editor(s): look in `C:\Program Files\Unity\Hub\Editor\*\Editor\Unity.exe`
   (also check Unity Hub's custom install location if that is empty). Report the versions. Prefer 2022.3.x;
   Unity 6 (6000.x) is fine too.
2. Get the code (branch `claude/grass-progression-levels-2ok54e`):
   `git clone -b claude/grass-progression-levels-2ok54e https://github.com/trentonbarrows02-cell/Discord-Server-Cloner-2x C:\PlayShiftGames\ClearTheGrass`
   If git is not installed, download and unzip
   `https://github.com/trentonbarrows02-cell/Discord-Server-Cloner-2x/archive/refs/heads/claude/grass-progression-levels-2ok54e.zip`
   into `C:\PlayShiftGames\ClearTheGrass` instead (then fixes can't be pushed; report them).
   The Unity project is the `PlayShift` subfolder: `C:\PlayShiftGames\ClearTheGrass\PlayShift`.
3. Run the setup in batch mode (first run downloads packages; can take 10+ minutes):
   `"<Unity.exe>" -batchmode -quit -projectPath C:\PlayShiftGames\ClearTheGrass\PlayShift -executeMethod PlayShift.Grass.Editor.FirstOpenSetup.BuildForBatch -logFile C:\PlayShiftGames\ClearTheGrass\unity-setup.log`
4. Read `C:\PlayShiftGames\ClearTheGrass\unity-setup.log`. Look for `error CS`, `Shader error`, `BATCH SETUP FAILED`,
   package resolution errors. Fix the causes in `PlayShift/Assets/PlayShift/...` (keep the design: GPU-instanced
   grass, 5 cm cut grid, first-person floating trimmer with no hands), then re-run step 3 until the log shows
   `BATCH SETUP OK` and no errors. Also check the URP/Built-in grass shaders compile (`PlayShift/InstancedGrass`,
   the project uses the Built-in pipeline by default).
5. Commit the fixes and push to `claude/grass-progression-levels-2ok54e`.
6. Open the project normally so the owner can play:
   `"<Unity.exe>" -projectPath C:\PlayShiftGames\ClearTheGrass\PlayShift` and make sure `Assets/PlayShift/Scenes/GrassTestArea.unity`
   is open. Controls: hold Left Mouse (or press Space) to run the trimmer, WASD to walk, mouse to look.
7. Report back: Unity version used, what was fixed, and anything still broken.
