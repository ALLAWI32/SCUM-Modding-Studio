# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [semantic versioning](https://semver.org/).

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
