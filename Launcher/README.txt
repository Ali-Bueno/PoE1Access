PATH OF EXILE ACCESS - TEST VERSION

A test mod that makes Path of Exile 1 playable without seeing the screen. It reads menus,
items, panels and what is around you through your screen reader, and adds sounds to find your
way: a wall radar, unexplored-ground pips, and a guide to whatever you choose.

Items on the ground keep making a sound while they are within about a screen of you, from the
side they are on, higher when up the screen, and more often as you get closer. Each kind has its
own sound: currency clinks, scrolls rustle, gems ring like glass, flasks bubble, and equipment
thumps, with one rising note for magic, three for rare and a full chord for unique. Quest items,
maps, divination cards and jewels each have their own sound too. In Exile API's settings (F12)
you can turn them off (LootSounds), sound only magic-or-better or only valuable items
(LootFilter 1 or 2), or change their volume (LootVolume). The mod makes these sounds itself.

Enemies within about a screen growl from the side they are on (the Poe2Access enemy sounds,
rebuilt): a short low growl, repeated faster as they get closer, lower in pitch when they are
below you; magic and rare enemies buzz on top, uniques ring like struck metal. The four that
matter most sound (uniques first, then magic and rare, then the nearest); enemies behind walls
stay quiet, except uniques. When an enemy comes within melee reach, two quick rising notes play
from its side (ReachPing, ReachDistance); they play again once every enemy has stepped back.
The two nearest NPCs make a light wooden tap from their side, and
the nearest other player a quicker, higher double tap (NpcSounds). Life, mana and energy shield
are spoken as they fall below 75, 50 and 25 percent (AnnounceResources). Double tap L3 for your
level and how much is left to the next one, then life, mana and the area. Enemies are named once as they come into view, a
pack in one line with the way to its middle: "Zombie, 3, up right. Rhoa, rare, left". When the
game's target lands on a living enemy you hear two quick blips and its name, with its life when
hurt. Settings: EnemySounds, EnemyVolume, AnnounceEnemies, EnemyDirections, CrosshairBeep,
AnnounceTarget.

WARNING
This is an unofficial tool. It reads the game's memory through Exile API, but never changes it
and never presses anything for you. Grinding Gear Games has not approved it: use it at your own
risk. It is a test version and it can fail.

WHAT YOU NEED
- Windows 10 or 11, 64-bit.
- Path of Exile 1 (Steam or standalone), in windowed or windowed fullscreen mode.
- The Microsoft .NET 10 Desktop Runtime, x64. The mod tells you if it is missing and opens the
  download page: https://dotnet.microsoft.com/download/dotnet/10.0
- The Microsoft Visual C++ Redistributable (most computers have it).
- A screen reader. NVDA, JAWS and others work, with braille displays too; without one, the
  Windows voice is used.
- A controller is recommended (the game's controller mode is what the mod is built around).

GETTING STARTED
1. Unzip this folder anywhere.
2. Double-click PoE1Access.exe.
3. The first time, it asks to download Exile API, the program that reads the game, from
   https://github.com/exApiTools/ExileApi-Compiled. Exile API is not part of this package.
   It also moves Exile API's own extra plugins (some of which press keys or click for you) into
   app\ExileApi\Plugins\Disabled. You can move any of them back to Plugins\Compiled.
4. It starts Path of Exile through Steam if it is not running (with the standalone client,
   start the game first), then Exile API.
5. The login screen, character selection and character creation are read from the start. Exile
   API itself only starts once a character is in the game; until then the mod reads those
   screens on its own (app\reader). Password boxes are never read aloud or logged.

When you close the game, the mod closes too.

CONTROLS
In game (controller, L3 = left stick click / keyboard):
- Hold L3 + right stick up or down / Page Up, Page Down: scanner category
- Hold L3 + right stick left or right / Home, End: previous or next thing, nearest first
- Tap L3 / Insert: where the chosen thing is now
- Double tap L3 / F1: level and how much is left to the next, life, mana, energy shield, area,
  how much you have explored
- On the character sheet, hold L3 + right stick: up and down for each stat, left and right for
  each section
- Hold L3 + click the right stick / Delete: guide to the chosen thing on or off
- F9: repeat the last thing said
- F5, F6: previous, next item in your inventory; F7: inventory or equipment
Every key can be changed in Exile API's menu (F12), under PoE1Access.

LICENSE
Path of Exile Access is free software under the GNU General Public License, version 3 (see
LICENSE.txt). Prism keeps its own licenses (licenses\prism).

CREDITS
- Exile API (https://github.com/exApiTools/ExileApi-Compiled), downloaded on first run, which
  reads the game.
- Prism 0.18.3 (MPL-2.0 license), for screen reader and braille output:
  https://github.com/ethindp/prism. Its licenses are in licenses\prism.
- The navigation design follows Path of Exile 2 Access (https://github.com/Ali-Bueno/poe2-access);
  all sounds here are synthesized by this mod.

IF SOMETHING FAILS
Send these files to whoever gave you the mod:
- app\launcher.log
- app\reader.log and app\reader\PoE1Access_dumps\PoE1Access_log.txt (login and character screens)
- app\ExileApi\PoE1Access_dumps\PoE1Access_log.txt (in the game)
Email addresses are blanked out in these logs.
