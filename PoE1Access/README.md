# PoE1Access: menu reading for Path of Exile 1 (ExileApi plugin)

A read-only accessibility plugin. It never presses a key, moves the cursor or writes to
the game. It speaks what the game shows, through NVDA (or the Windows voice if NVDA is not
running).

**Status: compiles, not yet tried in game.** It builds with 0 errors and 0 warnings against
the current ExileApi-Compiled `ExileCore.dll` (.NET 10 SDK). It has not been run in game yet,
so expect a first round of fixes once it reads real memory.

## What it does

- **Hover reader.** Speaks the menu element, item tooltip or passive node under the cursor
  (or under the controller focus, if the game moves that focus). It waits a moment so you do
  not hear half-finished text, and does not repeat the same thing twice.
- **Passive tree.** A hovered node is spoken as: name, kind (keystone, notable, mastery,
  jewel socket, passive), whether it is allocated, and then its stat lines.
- **Panel announcements.** "Inventory open", "Passive tree closed", and the same for stash,
  vendor, shop, trade, dialogue, quest reward, crafting bench, map device, gem level up,
  world map and options.
- **Item browser.** Steps through your inventory (in reading order, with column and row) or
  through your worn equipment and flasks, one item per key press.

## Navigation (modelled on Poe2Access, rebuilt for PoE1)

All sounds are synthesized by the plugin; no sound files are copied from other mods or games.

- **Radar** (on the four screen sides, only when what lies ahead changes): a low tock = a wall
  closes the way, a soft breath = the way opens (rising ahead, falling behind), a bright tin =
  an exit in that direction. Left and right are in stereo; down is a little lower.
  After an open way or an exit, two short pips mean that way leads to ground you have not
  seen yet; one pip means the unseen ground there is a small dead end. Ground within 60 cells
  of where you have been counts as seen. The status line says how much you have explored.
- **Wall bump:** a short thud when you push the stick into a wall and do not move.
- **Scanner:** categories Exits, Waypoints, Characters, Enemies (on screen), Items, Chests,
  Objects, nearest first. Each entry: name, steps along the walkable route, the direction to
  push now and the next turn ("12 steps, right 3, then up").
- **Guide:** a chime panned toward where the route goes next, higher when it goes up the
  screen, lower when down, faster as you get closer. "Arrived at …" ends it.
- Silent while a panel, window or menu is open.

| Controller (L3 = left stick click) | Keyboard | Does |
|---|---|---|
| Hold L3 + right stick up / down | Page Up / Page Down | Previous / next scanner category |
| Hold L3 + right stick left / right | Home / End | Previous / next entry, nearest first |
| Tap L3 | Insert | Where the scanner's choice is now (in menus: repeat) |
| Double tap L3 | F1 | Life, mana, energy shield, area and level |
| Hold L3 + click right stick (R3) | Delete | Guide to the scanner's choice on / off |

The game also sees these controller presses; turn "Controller Commands" off in the ExileApi
menu if they get in the way.

## Keys (all can be changed in the ExileApi menu, F12)

| Key | Does |
|---|---|
| F5 / F6 | Previous / next item in the current list |
| F7 | Switch the list between Inventory and Equipment (says the item count) |
| F9 | Repeat the last thing spoken |
| F8 | Developer: save every inventory and equipment item with its data |
| F10 | Developer: save which panels are open, and the UI tree under them |
| F11 | Developer: save the element under the cursor, its tooltip, and what the reader says |
| F3 | Developer: text probe. Finds where the game stores on-screen text (run it after a game patch) |

Everything the plugin does is also written to `PoE1Access_dumps\PoE1Access_log.txt`.
UI text is read at offsets found by the text probe (see `UiText.cs`), because ExileApi's own
text and panel pointers are wrong on the current game version.

The developer files go into a `PoE1Access_dumps` folder next to `Loader.exe`.

## Install

1. Install the **.NET 10 SDK** (the SDK, not only the runtime). ExileApi builds plugin source
   with MSBuild; without the SDK it shows a "missing MSBuild" box. Also install the Visual C++
   Redistributable if you have not.
2. In the ExileApi folder (the unzipped `ExileApi-Compiled-master`), create the folder
   `Plugins\Source` if it is not there, and put this whole `PoE1Access` folder inside it. You
   should end up with `Plugins\Source\PoE1Access\PoE1Access.csproj` and the three `.cs` files
   beside it.
3. Start Path of Exile 1 (windowed or windowed fullscreen), log in to a character, then run
   `Loader.exe`.
4. ExileApi compiles the plugin when it starts. It either loads, or reports compile errors.
   You should hear "Path of Exile access plugin loaded".

**NVDA:** the plugin looks for `nvdaControllerClient64.dll` in the folder of the running NVDA,
then in `Program Files\NVDA`, then next to `Loader.exe`. It checks every couple of seconds, so
starting NVDA after the game works. I could not confirm that every NVDA install includes that
DLL, so if you hear the Windows voice while NVDA is running, copy `nvdaControllerClient64.dll`
(from NVDA's "controller client" download) next to `Loader.exe`. Without NVDA you get the
Windows voice. The first spoken line says which one is in use.

**If it does not compile:** ExileApi shows the errors and may write an `Errors.txt`. It will not
compile at all if the .NET SDK is missing or a file called `disable_plugin_compilation.txt`
exists in the ExileApi folder.

## First test, in this order

1. Does it compile and say it loaded? If not, send me the error text (ExileApi may also write
   an `Errors.txt`; search the ExileApi folder for it).
2. Open the inventory. Do you hear "Inventory open"?
3. Press F6 a few times. Does it read your items, with column and row?
4. Move the mouse (or the controller focus) over items. Is anything spoken?
5. Switch to controller input and move around a menu. Does the speech follow the focus? This
   is the most important unknown: it decides how the passive tree and menus work for you.
6. With the inventory open press F10; hover an item and press F11; press F8. Open the
   passive tree, hover a node and press F11. Send me the files from `PoE1Access_dumps`.

## Not covered yet

Login and character-select screens (the plugin only runs once you are in a zone), stash
contents, vendor lists, NPC dialogue text, quest rewards, skill gems, a passive tree finder,
and the parked area announcements and low-life warnings. The dumps decide how these are built.

## Policy note

GGG does not encourage third-party tools and cannot promise that any is allowed. This one
only reads and speaks, but it does read game memory through ExileApi. Consider asking GGG
about an accessibility tool before sharing it.
