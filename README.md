# Path of Exile Access (PoE1Access)

An accessibility mod that makes **Path of Exile 1** playable without seeing the screen. It reads
menus, items, panels and the passive tree through your screen reader (NVDA, JAWS and others,
with braille), and adds sounds to find your way and to  engage in combat.

It runs on [Exile API](https://github.com/exApiTools/ExileApi-Compiled), which reads the game's
memory. The mod only reads: it never changes the game and never presses anything for you.

> **Warning:** this is in beta, it could be unstable, and not all menus may read properly.   Grinding Gear Games has not approved it. Use it at
> your own risk. It can fail, especially right after a game patch.

## What it does

- **Menus:** login screen, character selection and creation, the pause menu and the options
  (rows, tabs, checkbox, slider and dropdown values), read as the controller selection moves.
- **Inventory and equipment:** each item with its mods, gem and currency descriptions; every
  equipment slot, including empty ones; what you pick up, place and equip.
- **Character sheet and skills panel:** every stat, stepped through one by one.
- **Passive tree:** each node's type, state, what it does, how many points away it is, and the
  nodes linked to it with their direction on screen.
- **Navigation:** a wall radar, unexplored-ground pips, a scanner of exits, waypoints,
  characters, enemies, items, chests and unexplored ground, and a guide with turn-by-turn
  prompts.
- **Sounds:** enemies (growl, buzz for magic and rare, ring for unique), NPCs and players,
  items on the ground by kind and rarity, a targeting blip and a melee reach ping. All sounds
  are synthesized by the mod.
- **Speech:** enemies coming into view with their direction, the targeted enemy, life, mana and
  energy shield as they fall past 75, 50 and 25 percent, your level and experience.

The full player guide, with every control and setting, is [Launcher/README.txt](Launcher/README.txt)
(it ships as README.txt in the download).

## Controls

The mod only reads the controller. It cannot stop the game from seeing a press, so its
controls use L3 (clicking the left stick) and the right stick while L3 is held, which the game
leaves free in play. Every keyboard key can be changed in Exile API's menu (F12), under
PoE1Access.

### Mod controls in play

- **Tap L3** (Insert): where the thing you chose in the scanner is now.
- **Double tap L3** (F1): your level and how far you are to the next, life, mana, energy shield,
  flask uses, the area and its level, how much you have explored, and the area's description.
- **Triple tap L3** (Delete): the guide to the chosen thing, on or off.
- **Hold L3 + right stick up or down** (Page Up, Page Down): the scanner's previous or next
  category (exits, waypoints, characters, enemies, items, chests, unexplored ground, quest,
  where you died).
- **Hold L3 + right stick left or right** (Home, End): the previous or next thing in that
  category, nearest first.
- **Hold L3 for half a second**: the game's button prompts for what is around you (pick up,
  talk, and so on).
- **Hold L3 for a second and a half** (hold F3 for a second): the mod's settings menu.
- **F9**: repeat the last thing said.

### Mod controls in windows and menus

- **Tap L3**: the next line of the selected item or tooltip. **Double tap L3**: the previous
  line.
- **Hold L3 for half a second**: the whole item or tooltip, or the window's controls when there
  is nothing to read.
- **Hold L3 for a second and a half** in the inventory: every item in the bag, or every
  equipment slot when the selection is on the equipment.
- **F5, F6**: previous, next item in your inventory. **F7**: switch between inventory and
  equipment.
- **Character sheet**: hold L3 + right stick up and down for each stat, left and right for each
  section.
- **Skills panel**: hold L3 + right stick left and right for each skill on your skill bar, up and
  down for its details.
- **World map** (from a waypoint): move the selection to hear each place; tap L3 for its
  description.
- **Passive tree**: each node says its links; tap L3 for the next line, double tap for the
  previous, hold L3 for everything about the node.

### Mod settings menu

- **Right stick up and down** (arrow keys): move.
- **Right stick left and right**: open a submenu, turn a switch on or off, or change a value in
  steps of 5. A volume plays its sound as it changes.
- **Tap L3** (Backspace): back up one level. **Double tap L3**, or hold F3 again: close.

### Game controls (controller)

These are the game's own controls, as it shows them on screen with a controller. The mod reads
the game's button prompts as they appear, so the prompts of each window and menu are said as
you open them. Hold L3 to hear them again.

- **Left stick**: move, and move the selection in menus and windows.
- **A, B, X, Y and the D-pad**: the skills on your skill bar, one per button.
- **R3** (clicking the right stick): skill binding, to choose which skill each button uses.
  - **Left stick**: move between the buttons; each one says the skill on it, or "empty".
  - **A**: the skills you can put on that button. Move with the left stick to hear each one with
    its details and its place in the list ("2 of 3"). A puts it on the button.
  - **B**: back, then close skill binding.
- **Start**: the game menu (options, log out, exit).
- **Left and right bumper**, in the character windows: switch between Passives, Character,
  Skills, Inventory, Cosmetics and World.
- **Inventory**: the left stick moves the selection; L3 compares items or opens more actions; the
  game names the buttons to pick up, place and close in its prompts, which the mod reads.

## Installing

1. Download the latest release zip from the Releases page and unzip it anywhere.
2. Double-click `PoE1Access.exe`.
3. The first time, it asks to download Exile API from its GitHub page. Exile API is not part of
   this package. On every start it checks for a newer Exile API and updates it, since Exile API
   is updated after each game patch.
4. On every start it also installs the newest PoE1Access release from this page, then starts
   again (versions 0.2.2 and later).

You need Windows 10 or 11 (64-bit), the .NET 10 Desktop Runtime (x64), and Path of Exile 1 in
windowed or windowed fullscreen mode. A controller is recommended.

## Building

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -ExileApi "C:\path\to\ExileApi-Compiled-master"
```

The Exile API folder is only used to compile against `ExileCore.dll`. The script builds:

- `PoE1Access/`: the Exile API plugin (`app\plugin`)
- `Reader/`: reads the login and character screens before Exile API starts (`app\reader`)
- `Launcher/`: `PoE1Access.exe`, a single self-contained file that sets up and starts everything

and writes `dist\PoE1Access` and `dist\PoE1Access.zip`.

## License

Path of Exile Access is free software under the [GNU General Public License v3.0](LICENSE).

It bundles [Prism](https://github.com/ethindp/prism) 0.18.3 for screen reader output, under its
own licenses (MPL-2.0 and those in `third_party/prism/LICENSES`). Exile API is not included.

## Credits

- [Exile API](https://github.com/exApiTools/ExileApi-Compiled), which reads the game.
- [Prism](https://github.com/ethindp/prism), screen reader and braille output.
- [Path of Exile 2 Access](https://github.com/Ali-Bueno/poe2-access): the navigation design, the
  controls and the sound designs follow it. Its sounds are rebuilt here from its documentation;
  no code or recordings are copied.
