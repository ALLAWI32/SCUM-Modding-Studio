# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [semantic versioning](https://semver.org/).

## [0.2.6] - 2026-10-06

### New in this release
- **Place any Blueprint from Assets** (and the Map's Add object box): the app finds one the game placed somewhere on the
  island and copies it to where the camera aims, even when its level is not loaded. An item (a drill press, a chest,
  a lamp) is placed the way the game places fixed items: a world item spawner set to spawn it.
- **Machines in houses**: the drill presses, lathes, stoves and fridges a building spawns are drawn on the map, and
  their spawn point can be selected and moved (it was only a pin you could look at). Car shop vehicle spots select
  and move too.
- **Imported mods**: a per-mod **In my pak** switch (a big map can stay its own pak); a level in a game plugin folder
  goes into the pak; tools' JSON dumps and pictures are left out of the import.

### Fixes
- **Objects missing from the map**: meshes the game cooks without their finest detail level (chairs, wheelie bins,
  wall lights, speakers, cameras, lavender: about 193,000 placements) were dropped; they are drawn, picked, copied
  and exported now, and the Assets page exports them to glTF/OBJ.
- **The abandoned city** (378 levels of cell C_0) loads in the whole-island view and with its cell.
- **A copied door opens**: a click on a door's leaf takes the door (not a plain mesh of it), and a building's door
  duplicated in its own level is no longer tied to the original building.
- **Map header**: the loading text no longer runs into other text; it has its own line with room in every language.

## [0.2.5] - 2026-10-03

### New in this release
- **Landscape menu** on the Map page (the whole island, in the client and server paks):
  - **Ground look**: Game, Snow, Desert, Autumn or All grass. Soft ground (grass, forest floor, fields, soil, beaches)
    gets another ground texture of the game and the grass growing on it another grass type (snow plants, dry plants);
    roads, rocks, rivers and the sea floor stay. Faraway ground keeps the game's colour.
  - **Trees**: draw another tree everywhere the game has one (an oak becomes a pine, with the pine's collision);
    **Put back** undoes one swap.
  - **Fit to ground**: lays the selected building, or every object of the multi-selection, on the slope under it
    (tilted the way the ground runs, its bottom on the ground). Also in the Shape menu.
- A new edit, **replace asset**: the mod carries a copy of one game asset under another's path (what the ground looks
  and tree swaps are made of).

### Fixes
- No more freezes with hundreds of edits: after every edit the 3D view walked all of the map's ~155,000 parts once
  per moved object (seconds with ~980 edits). Moving the edited objects now takes 3 ms instead of 136 ms in the test
  level, copies move in place, and the history panel adds one row instead of rebuilding a thousand.
- Bending a house or church no longer drops the view to a few frames a second: only long, narrow objects (walls,
  fences, pipes, bridges) bend; houses and other wide objects tilt, turn and fit to the ground. The bend slider is
  hidden for them.
- The World panel on the left is narrower.

## [0.2.4] - 2026-10-03

### New in this release
- **Traders on the map**: every outpost trader (armorer, mechanic, doctor, barber, bartender, general goods, boat
  shop) is pinned where the NPC stands, with the name the server's EconomyOverride.json uses (`A_0_Armory` …). The pin
  selects the trade post: move it, copy it (`Ctrl+C` / `Ctrl+V`) or delete it like any object; a deleted one also
  leaves its outpost's list.
- **Traders sell your clones**: a clone of a car, weapon or item the traders sell gets its own trade row (its own
  name in the trade menu); the new **Trader** part sets its price, sale price and the fame points needed.
- **Import mods**: Projects page → **Imported mods** → **Import mod…** takes someone's pak (a map, a landscape,
  cars). The app reads it over the game, you edit on top of it, and Export mod carries it in your pak.
- **Paint for every weapon**: knives, axes, bats, the MP5, the AS Val and the other weapons that had no paint panel.
  Magazines have no panel any more (they wear their gun's material). **Default colours** is a highlighted button.

### Fixes
- Moved or deleted trees are drawn where the edit put them in the game (they stayed visible where they were, with
  no collision, so you walked through them).
- The game's far-view models (a blurred church merged with a car and walls, no collision) are no longer offered as
  objects; the export warns about ones placed before.

## [0.2.3] - 2026-10-03

### New in this release
- **Brush select** on the map: hold the mouse and paint a circle; everything under it joins the selection. Turn the
  brush off, `Ctrl+click` what should stay, and `Delete` removes the rest.
- **Tilt buildings**: houses, churches and halls no longer bend like bridges; the Shape menu leans any object front,
  back or sideways (up to 90°) and turns it.
- **Melee weapons** on the Weapons page (Melee filter) with their **hit damage**: the game's weapon table row
  (damage, stamina, cutting, stabbing). A cloned knife gets its own row, so it can be stronger than the original.
- **Engine power** for vehicles: the torque at each rpm. A clone has its own engine; the stock vehicle stays.
- **Paint for weapons and planes**, a **Game colours** button that puts the stock paint back, a resizable paint panel,
  and clones with a paint of their own.
- **What spawns at a pin**: click a loot pin to see the items that can spawn there and how rare they are; the
  **Colours** legend explains every pin colour and switches each kind on or off.
- **Island spawn places**: vehicle, zombie, threat-zone and hunting-area places of the whole island on the map.
- A click on a building picks the part under the cursor by default; **Select the whole building** goes back to it.

### Fixes
- The app no longer freezes after flying over the map for a while; streaming keeps the ground on the GPU.
- Buildings that come back into view keep their textures (a church drew plain white).
- `Delete` removes the whole `Ctrl+click` selection, not only the last object.
- Copied trees and objects collide in the game like the original.
- Planes show and take their paint like the game draws it.
- The map header stays still while loading; no overlapping text on the Vehicles page.

## [0.2.1] - 2026-10-03

### New in this release
- **Vehicle paint**: a Paint panel next to the vehicle's 3D view. Any colour or a finish (gold, rose gold, chrome, bronze,
  pink, candy red, metallic blue, pearl white, matte black), metal and clear coat; the model shows the paint with its
  shine as you pick, through the game's own colour mask (bare plastic, rubber and rust stay as they are). `Ctrl+Z` steps
  back. Apply paint records it; Export mod writes it into the client pak.
- **Armour in 3D and painted**: fit the light or heavy armour kit on the vehicle's model. The armour material has no
  paint of its own, so the mod adds it (colour, shine and a full colour mask); **Plain finish** swaps the scrap-metal print
  for one even colour. **Paint every part the same** keeps body, doors and armour on one finish.
- **Parts of buildings on the map**: the **Parts** button (or `Alt+click`) picks one part of a Blueprint building (a
  hangar's wall, shelf or lamp) instead of the whole building, to move, copy or delete it alone; only that building
  changes.

### Fixes
- The vehicle 3D view shows the whole stock vehicle (all doors and panels); armour plates sit on their sockets.
- Changing a colour no longer moves the 3D camera back.
- Bright gold panels no longer turn yellow in the preview.
## [0.2.0] - 2026-10-03

### New in this release
- **Updates inside the app**: the app checks GitHub when it starts; a newer release shows an **Update** button with the
  release notes. One click downloads it, puts it in place of the running copy and restarts; the new version then shows
  what changed. Optional, and your projects and settings stay.
- **Spawns page**: vehicles, planes and boats (fuel and battery at spawn with Empty / Half / Full, the chance each part is
  there, part condition, the server's limit per vehicle); zombies and NPCs (all 73 threat zones: spawn chance, first
  spawn, check interval, cooldown, distance, spread, group weights); server settings (zombie and horde multipliers,
  sentries, drones, dropships, animals) written straight into `ServerSettings.ini` with a backup.
- **Spawn places on the map**: every loot point read from the game files (the properties panel says what spawns there),
  loot zones, sentries with their patrol paths, bunker creature points, car-shop spots and drop zones as coloured pins;
  spawners move, copy and delete like objects; a Spawns switch in the map header hides them.
- **Placement accuracy is tested** against the game's own far-view models: on the levels checked, what the map draws
  covers 99.8-100 % of the game's geometry.
- **Far view follows your edits**: deleted or moved objects are cut out of the island's far-view models (towns,
  outposts, bridges) and the outposts' HLOD proxies, so they no longer show from far away.
- **Faster map**: the whole-island terrain is cached (0.6 s instead of 15-17 s at every start) and the map reopens where
  the camera was.

### Fixes
- Export no longer fails with "Implausible Actors count" on 15 levels whose actor list keeps empty slots for actors the
  game's cook removed (stone mines, Biomes, Threat Zones, the airport, Kotoriba and others).
- Fake lit-window boxes no longer count as part of a building.

## [0.1.0] - 2026-10-02

First public release: **SCUM Modding Studio**.

### New in this release
- **Automatic AES key**: the setup looks SCUM's key up on a public key list, tests it on your game files and stores it
  encrypted; you paste it yourself only when that fails.
- **8 languages**: English, Arabic, Russian, German, Spanish, Turkish, Bosnian/Croatian/Serbian and Simplified Chinese.
- **Colours**: 16 accent colours and a colour picker for any colour.
- **Bridges and roads**: bend, lengthen, widen, ramps and humps, pylon legs, welding ends, repeating pieces; bent pieces
  get box collision built from the mesh (no invisible walls, no holes for the wheels), on client and server.
- **Streaming**: the export grows each level's World Composition area over what you built, so a long bridge no longer
  disappears when you walk out of the original level's area.

### Map editor
- Whole-island view with levels streaming in around the camera, quality presets (Performance / Balanced / High / Ultra).
- Select, move, turn, snap, delete, copy and paste objects (also into other loaded levels); single trees, rocks and road
  pieces can be picked one by one; `Ctrl+A` selects every object of the same kind; `Backspace` returns to the last object.
- **Shape** menu: bend walls, bridges and road pieces left or right as one smooth piece (exported as spline meshes), and
  change length, width, height or size of anything.
- Objects browser with thumbnails, categories and building sets (walls, roads, bridges); place an object in front of the camera.
- Distant forests keep their leaves and colours; water, glass and translucent objects can be selected.
- Journal-backed undo/redo that survives restarts; one-click export of client and server mod paks.

### Vehicles and weapons
- Tune stored values and clone vehicles or items under a new name, registered for `#SpawnItem` / `#SpawnVehicle`.

### AI control
- MCP server (39 tools, local only, token protected): open levels, place objects on the ground and on other objects,
  scatter forests, copy buildings, tune items, export, screenshots.

### App
- English, Arabic, Russian and German UI with a description on every control.
- Command line `scumstudio` for scripting, renders and exports.
