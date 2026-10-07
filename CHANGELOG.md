# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [semantic versioning](https://semver.org/).

## [0.2.8] - 2026-10-07

### New in this release
- **The economy follows the map**: a trader placed on the Map gets its section in the project's `EconomyOverride.json`
  at once, listing its type's whole stock with the game's values (`-1` / `default`, as the game writes them), and a toast
  says it is ready; deleting or undoing the trader removes the section again (undoing the delete in the same session
  brings its edits back). The game's traders whose trade posts the project deleted (a whole outpost) leave the Economy
  page and their sections are left out of the exported file (the export report lists them); undo brings them back.
- **Stock editing with pictures** (Economy page): every row shows the trade menu's picture of the item (a drawing of its
  mesh where the game has none), a mark when changed (blaze) or added (olive) and off-sale / locked badges. **Add items**
  opens a drawer with every tradeable of the game (search by name, code or category, with pictures): tick several and
  add them in one go with the game's price, sale price and fame, written in full with `can-be-purchased` `true`. The
  remove button takes an added item away and puts one of the trader's own stock off sale. Trader cards show the type's
  glyph and the map cell. The project's file is written atomically; the file found before the session's first write is
  kept as `EconomyOverride.json.bak`.
- **Trader names numbered per sector and type**: a new trader is `<cell>_<type>_<n>`, counting the game's traders of that
  cell too and skipping taken names (B_4 has the game's hospital, so a placed doctor is `B_4_Hospital_2`; a farm's first
  armory is `A_3_Armory_1`). A copy of a placed trader is numbered the same way in the cell it is pasted into.
- **Brush paint mode** (Map › Brush › *Paint*): pick a palette of trees, bushes, rocks or any object (the selection's
  family first, each with its picture; saved with the project), set the spacing (1–20 m, 3.5 m by default), random
  size and random turn, then hold the left button and sweep: the objects are scattered at random inside the circle
  along the stroke, never closer than the spacing to each other or to a tree, rock or building already there, each
  standing on the ground or surface under it. A tree, bush or rock the landscape tile under the brush already has as
  foliage becomes a new tree of that foliage (chopped and collided like the game's own), anything else a new object or
  a copy of a placed Blueprint. The stroke shows as it goes; letting go is one History row ("Planted N objects").
- **Place a trader anywhere** (Map › Add object › Trader): pick the type (armory, general goods, mechanic, doctor,
  bartender, barber, harbourmaster, hunter, master hunter or bank), a name (default from the cell, e.g. `A_3_Armory_1`)
  and an outpost (`Outpost_A_0`/`B_4`/`C_2`/`Z_3` joins that outpost, any other name makes a new one); it is a copy of
  the game's own trade post of that type with its NPC, drawn where the camera aims, one undo step. The export gives the
  trader a personality of its own (its name and a stable persistent id, registered in AssetRegistry.bin), sets its
  outpost, lists it in the outpost manager's `_assignedTradePosts` (a new outpost gets a manager and a description of
  its own in that level), gives its quest giver an id of its own and adds its section to EconomyOverride.json; the
  export report says what the server needs. A copy of a placed trader gets a new name; delete removes it.
- **Economy page**: every trader of the game and of the project with what it sells (the game's `Table_TradeableDesc`
  and DLC tables): price, sale price, fame needed, on sale, after sale only; search, a category filter, +10 % / −10 %,
  take a category off sale or put it back, reset a trader or a row. Changes are kept in the project as the server's
  `EconomyOverride.json` (the schema the game writes itself) and written next to the paks by Export and Export mod
  (Server and Client folders). "Edit stock" on a trader in the Map opens its section.
- **Place a mechanic's lift** (Map › Add object › Lift): the game's car lift or bike lift, copied where the camera aims and
  tied to a mechanic of a loaded level (a stock one or a Mechanic trader you placed); the export points its
  `_assignedTradePost` at that mechanic's trade post, so the vehicle on it is repaired and upgraded through that mechanic.
- Paste never fails silently: "Nothing to paste" when nothing was copied, and a warning when a copy from a level the
  island streamed out could not be read or has nothing to draw (the copy is still in the project and exports).
- The "Local axes" switch moved from the Map toolbar to Settings (the map follows it live).
- Trader stand-ins now face the way the NPC faces in game (they stood turned half round), and their cards read
  "Trader <type> <sector>" ("Trader Armorer B_4", "Trader Bank"); the game's own name stays in the details.
- Deleting a child actor (a hangar door, a lamp a building spawns) also removes the component that spawned it; left alone
  the game would spawn a fresh one from its class when the level loads.
- Export removes an actor whole when every one of its instances was deleted (burning tyre stacks: fire, heat, smoke and sound go with the tyres).
- **Spawn stand-ins show whole people and glint**: a trader's or NPC's Blueprint is composed from every mesh component it and its parent classes carry (body, head, hair and beard cards cut out by their coverage atlas, gear on their sockets) into one half-transparent figure standing on the ground, posed from frame 0 of its idle animation (CPU skinning); a loot point whose item has no mesh shows the item's inventory icon on a small camera-facing card instead of the crate; stand-ins pulse slowly in opacity (0.4..0.6 over 1.5 s).
- **A real transform gizmo on the Map** (like the FiveM map editors, Unity and Unreal): three arrows with heads move
  along an axis, the squares between them move in a plane, three rings drawn as a globe turn about X, Y or Z and the
  yellow outer ring about the view, cubes at the arrow tips scale one axis and the centre cube all three; lines are
  thick with a dark edge, the handle under the cursor turns yellow (hand cursor), the dragged one stays yellow while the
  rest fade, and the live value ("+2.50 m", "35°", "x1.20") follows the cursor; Ctrl snaps moves, turns and scale steps.
- **Spawn places show the object itself**: a world vehicle spawn and a car shop's box draw the first vehicle of their
  group with its stock parts (doors, hood, wheels), a zombie point a zombie, a sentry and its patrol points the
  sentry robot, a trader its NPC, a razor point the razor, a loot point the first item of its preset that has a mesh
  (else the crate), each as a half-transparent 3D model standing on the place and turned its way; it picks, moves
  and deletes exactly like the pin did. Pins stay for zones, loot zones, hunting areas, drop zones, effects and markers.
- **Prefabs** (Map toolbar): save the selection (a whole building, any multi-selection) under a name in your own
  library, place it again where the camera aims as copies laid out as saved (one undo step), export it as a plain
  JSON `.ssprefab` file that lists the objects, meshes and positions, and import files others share (Map menu or
  Settings › Prefabs). **History** rows get a right-click menu: *Go to* flies the camera to the object an edit touched
  and selects it, *Select* only selects it.
- **Replace** (Map toolbar), the Replacer: two cards side by side, the selection's 3D picture and name on the left and the replacement on the right; the right card drops down a searchable list of the same family only (roads, bridges, walls, fences, a building's family, the foliage's trees, bushes and rocks) with a 3D picture on every row, the current one marked. The pictures of the first rows are made in the background as the menu opens and kept in the thumbnail cache. The selected object or multi-selection is fitted to its place, length, height and curve; one Ctrl+Z undoes the whole selection. The Landscape menu's tree swap uses the same two cards instead of two drop-down boxes.

### Fixes
- **Rocks you place are solid in the game** (owner, B_4 outpost filled with rocks: "half my body is inside the rock",
  "knocked out he falls under the rocks, shoots others and nobody can hit him"). Rocks and cliffs no longer bend: the
  game's rock collision is the rock's own triangles, which a bent piece cannot have, and a bent cliff got 15 cm slabs of
  boxes with no walls (players walked into it and fell through it). A rock bent with an older version is drawn and
  exported straight, with a "collision check" line in the export. The export's collision check also names every
  placed rock whose hollow underside stands above the ground (SCUM's rocks and cliffs are shells open underneath that
  the game always sinks into the ground; lifted, players get inside, see and shoot out, and cannot be hit), and every
  added object that lets players or knocked-out bodies through. Moved foliage instances of a level that already had no
  draw copy (a level this studio wrote) now also grow their culling clusters, and an imported mod that is the
  project's own earlier pak is reported (its edits were applied twice).
- **Flying over the island no longer stutters at each streaming step.** When levels stream in and out around the camera
  the 3D view keeps the levels that stay (their meshes on the graphics card, their objects, moves and selection) and
  only adds the new ones: their meshes and textures go to the graphics card a few milliseconds a frame while the view
  keeps drawing, their objects are built on a worker thread, and only the draw lists of the meshes that changed are
  rebuilt. Each step used to send and rebuild the whole scene in one frame: 35-320 ms frames while flying and 1.5-1.9 s
  for the first load; now mostly 7-30 ms. Short pauses remain while a new level's meshes are read (the .NET collector
  cleaning up after the reader).
- **The brush takes everything inside its circle**, wherever the cursor is. Over empty ground (or towards the sky) the
  circle stays on the ground under the cursor; it used to do nothing unless the cursor was on an object, so each one
  had to be touched with the circle's edge. A building, fence or part counts when what it draws reaches into the circle,
  not only its pivot; a fast sweep takes everything along the way between two mouse moves; moving off everything keeps
  the selection. A sweep through a whole cell stays under a millisecond per move.
- **Fit to ground stands things on whatever is under them**, not only the landscape: a crate over a house lands on its
  roof, a barrel over a road on the road, a box on a floor, a rock or another copy, the highest surface no more than
  30 cm above its bottom (a roof below counts, the ceiling above does not); buildings still tilt to the four corners,
  now each corner on its roof, floor or ground. Leaves, grass, water, glass and spawn pins hold nothing up.
- **Your projects are listed** at the top of the Projects page: every project in Documents\ScumStudio Projects and
  every one you opened before, as a card with its name, number of edits and last edit, **Open** (one click) and
  **Show in folder**; the open one comes first with an accent border and *open now*, and two projects with the same
  name show their folder. A project whose folder is gone is no card but a muted *Not found: …* line with **Remove**.
  The page is one aligned flow at 1280 and 1600 px in every language: the cards in two columns over *Current project*
  (its details, or **Open MyMapMod** when none is open) and *New project*, then the export (a one-line hint until a
  project is open) and the game-file dump. The PROJECT label in the top bar leads there, and *Open project…* starts in
  the projects folder and also accepts a file inside the project or its parent folder.
- **The last project reopens at start again** (Settings › *Reopen my last project*, on by default; a toast says
  *Reopened MyMapMod*). It had stopped opening at all: a brush delete that took an actor and then one of its parts was
  checked against the state before it, written to the journal, and failed half way, so the journal no longer replayed
  ("Opening last project failed … is already deleted"). Such a journal now replays exactly the part that was applied
  then; a batch is checked edit by edit before anything is written; and deleting an actor together with its parts
  takes the actor once.
- **A purple spin ring** on the gizmo: the ring about the object's up axis is purple, a little thicker and drawn whole
  (grab it anywhere), so turning a building, car or trader round is the first thing you see; the Z arrow stays blue and
  the label keeps counting the whole way round ("270°").

- **The gizmo stands on the object**, not at its root: on the middle of what is drawn of the selection (a building whose
  root is a corner, a spawn model, one tree or road piece, the middle of a multi-selection); a trader's on the trader
  instead of 10-15 m away among the car shop's vehicles; a long road, bridge, fence or wall piece's on the piece where
  the camera looks (it follows when the camera flies on). Moves, turns and size changes act about it; the saved
  positions are the same as before. A multi-selection's gizmo has no scale cubes (only one object grew).

- **Deleting an outpost leaves nothing behind in the game.** The Wild Hunter stall (its class lives at the DLC plugin's
  own root, `/WoodlandHunterPack/…`, which the studio could not find, so none of its 531 parts had a mesh and nothing
  could select it) now resolves like any Blueprint and goes with the brush; the mechanic's car and bike lifts (skeletal
  meshes, nothing to draw) get a pin and the brush takes pin-only actors too; and when a delete takes out the last
  drawn part or instance of an actor, the whole actor is deleted instead of leaving its fire, light, heat, sound,
  collision boxes, decals and child actors standing (fire barrels and burning tyre stacks burned on in the air).
- **No invisible walls where parts were deleted**: a part scaled to nothing also loses its mesh in the export, so the
  game builds no collision body for it (545 "Scale3D is (nearly) zero" warnings a session before).
- **A bulk delete can no longer stop halfway**: every edit of a batch is checked against the edits before it before
  anything is journaled, and a tree picked together with its whole actor is covered by the actor's delete instead of
  stopping the batch ("already deleted"), which had left 2000 of 2650 edits unapplied while the journal said otherwise.
- **A project whose journal no longer replays still opens**: edits that no longer apply are left out and listed in a
  warning instead of refusing the whole project.
- **Deleted objects are really gone in the game**: a deleted actor left the level's list but its objects stayed in the
  package, and the game still created them and ran their code (B_4: the deleted trade posts kept spawning their traders
  in the empty field, the deleted quest books kept logging). The export now turns a deleted actor and everything under
  it into inert plain objects in place (no code, nothing to spawn; same indices, so nothing else in the level moves),
  for the client and the server pak alike. The export report counts them ("Objects of removed actors made inert").
- **A big delete no longer breaks the project**: a brush/multi-selection delete that listed a tree's instance or a
  building's part after the building's own deletion stopped half-way (69 objects stayed in the pak) and the project would
  not open again. A batch is now checked as a whole and applied as a whole (its undo too), a selection delete leaves out
  what the deleted actors take with them anyway, and the owner's existing project opens and exports every deletion.
- **The underside of a lake cannot be placed**: the `*_FN` twins of the lake surfaces (flipped normals, underwater
  material) are drawn by the game only from under the water, so placed as objects they never showed. The Map and the
  Assets objects list refuse them (the toast names the lake's top surface to place instead), Replace never offers them,
  one already in a project is marked in the entity list and its properties, and the export report says why. Placed
  rocks and other meshes were verified to land in the game exactly where the studio shows them (a real-file test reads
  the written level back the way the game does: < 1 cm, < 0.01°).

### Changed
- **Levels load 2-4 times faster** (16-core PC, real game files): the levels the island view streams in around the
  outpost 20-24 s → 4.5-5.5 s, the whole A_0 cell 9-12 s → 4 s, the island's terrain the first time 16 s → 9 s, the
  map's level list 1.1 s → 0.3-0.8 s, a 20-edit export 7.5-10 s → 5-6 s. Meshes, materials and textures are read on
  every core, levels side by side, new ground tiles coloured in one go, and the app uses .NET's server garbage
  collector (with dynamic heap sizing), which had been stopping the loading threads for half the time. What is drawn
  and every exported byte stay the same. Peak memory is about 0.5-1 GB higher while loading.

## [0.2.7] - 2026-10-06

### New in this release
- **Support and Report a problem** (the heart button in the title strip): PayPal and e-mail of the developer, and a
  report box. Write in any language: the text is translated to English and lands on the developer's Discord with the
  app's version, language, system and the last log lines (one report a minute, 1500 characters).
- **Discord**: a button in the title strip and a Community block in the Support card open the community server's
  permanent invite: help with mods, mods by others, bug reports and release news.
- **French and Vietnamese** user interface languages.
- **Local axes** (Map toolbar): the gizmo's arrows follow the selected object's own front, side and up instead of the
  world's; a drag along an axis is continuous, the grid snap applies only while Snap is on.
- **Effects and markers get a pin**: fires, lights, smoke, sounds, fog, NPCs, quest markers and other actors that
  have nothing to draw show an orange (effect) or grey (marker) pin that selects, moves and deletes the actor; two
  new legend layers switch them off.
- **DLC plugin levels**: the island's sublevels that the game's feature plugins add (the Wild Hunter traders'
  grottos) are listed with their cell, open with it and export to their own place in the pak.
- **Spawn points move, copy and delete**: a sentry's patrol points and a spawner group's loot points are each their
  own object on the map (drag it, Duplicate adds one beside it, Delete removes it; the export rewrites the stored
  array). Spawn places are drawn as half-transparent 3D stand-ins: a person-sized capsule where a sentry, zombie or
  trader spawns, a car shop's vehicle box at its real size, a crate at a loot point.

### Fixes
- **Fit to ground works on a multi-selection and on the game's foliage**: every selected object is set down; a tree,
  bush or rock stays upright with its foot on the ground, a building is tilted to the slope under it.
- **Narrow windows**: below 1420 px the title strip shows the tabs as icons only (names in the tooltips) so the
  search field never slides under the buttons on the right.
- **A copied tree can be chopped**: duplicating or pasting a tree, bush or rock of a foliage level adds a new instance
  to the same foliage component (exported into its `PerInstanceSMData`; the game rebuilds the foliage tree on load),
  so it is chopped, harvested and collided like the stock ones instead of being a plain mesh actor.
- **Levels shown on purpose stay shown** in whole-island mode: a cell picked in the world tree or levels an AI opens
  with `show_levels` were replaced half a second later by the levels under a camera that was somewhere else (and the
  selection went with them); streaming now resumes once the camera flies off.

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
