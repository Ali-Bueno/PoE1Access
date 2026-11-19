# Path of Exile Access (PoE1Access)

An accessibility mod that makes **Path of Exile 1** playable without seeing the screen. It reads
menus, items, panels and the passive tree through your screen reader (NVDA, JAWS and others,
with braille), and adds sounds to find your way and to follow a fight.

It runs on [Exile API](https://github.com/exApiTools/ExileApi-Compiled), which reads the game's
memory. The mod only reads: it never changes the game and never presses anything for you.

> **Warning:** this is an unofficial tool. Grinding Gear Games has not approved it. Use it at
> your own risk. It is a test version and it can fail, especially right after a game patch.

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

## Installing

1. Download the latest release zip from the Releases page and unzip it anywhere.
2. Double-click `PoE1Access.exe`.
3. The first time, it asks to download Exile API from its GitHub page. Exile API is not part of
   this package. On every start it checks for a newer Exile API and updates it, since Exile API
   is updated after each game patch.

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
