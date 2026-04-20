DS1 Enemy Multiplier v2 (Aggression Mode - no param = Normal Mode)
=======================
By hungryhyena78


WHAT THIS MOD DOES
------------------
Multiplies every enemy and boss in Dark Souls Remastered by a number you choose.
Unlike the old version, this one fully patches event scripts so bosses have health
bars, triggers fire correctly, skeletons wake up, and all other event-driven
behavior works for every copy of every enemy.


REQUIREMENTS
------------
- Dark Souls Remastered (PC)
- .NET 8 Runtime (download from https://dotnet.microsoft.com/download/dotnet/8.0)
- Game must be unpacked with UnpackDarkSoulsForModding
  (https://www.nexusmods.com/darksouls/mods/1304)


INSTALLATION
------------
1. Unpack your game using UnpackDarkSoulsForModding if you haven't already.
   This is required — the mod cannot work on packed game files.

2. BACK UP your game files first! The mod creates its own backup automatically,
   but having a second backup never hurts.

3. Copy ALL files from this zip into your Dark Souls Remastered game folder
   (the folder containing DarkSoulsRemastered.exe).

4. Run "DS1 Enemy Multiplier.exe" from that same folder.

5. Enter a whole number when prompted:
   - Enter 2 for 2x enemies (double)
   - Enter 3 for 3x enemies (triple)
   - Enter 4, 5, 6... for more
   - Enter 1 to restore your vanilla files

6. Wait for it to finish, then launch the game.


HOW IT WORKS
------------
The tool patches three types of files:

  map/MapStudio/*.msb     — Enemy placement (clones enemies N-1 times per map)
  event/*.emevd.dcx       — Event scripts (patches boss health bars, triggers,
                            skeleton wakeups, fog walls, etc. for every clone)
  msg/ENGLISH/menu.msgbnd — Boss health bar names (adds #2, #3 etc. labels)

On first run it creates an "EnemyMultiplierBackup" folder with your original
vanilla files. On every subsequent run it restores from that backup first, then
applies the new multiplier. This means you can freely switch between 2x, 3x, 4x
etc. without reinstalling the game.


CHANGING THE MULTIPLIER
-----------------------
Just run the exe again and enter a different number. It automatically restores
vanilla files before applying the new multiplier, so you always get a clean result.


UNINSTALLING
------------
Run the exe and enter 1 when prompted. This restores all vanilla files.
Then delete the exe and the other files from your game folder.


NOTES
-----
- Use whole numbers only (2, 3, 4...). Decimals and letters will be rejected.
- Very high multipliers (10x+) may cause performance issues or crashes due to
  the sheer number of enemies the game has to track.
- Multiplayer behavior with this mod is untested.
- The mod only patches English text files.


DISCORD
-------
hungryhyena78
