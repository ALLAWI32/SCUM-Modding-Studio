# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [semantic versioning](https://semver.org/).

## [3.5.0] - 2026-10-17

### New
- **Spawns: full control of each spawn place** (owner: "click a single vehicle spawn point on the map, see it, drag it,
  rotate it, change what it spawns, delete it, show it on the Map"): the Spawns map has a *Select point* tool. A click picks
  one vehicle point, zombie / NPC point, threat zone or hunting area and a card shows its name, coordinates, which way it
  faces and what it spawns. Drag it to move it (it goes onto the terrain where the island's terrain is known, 20 cm above it
  for a vehicle; indoor points keep their height; *Snap to the ground* turns this off), nudge it with the arrow keys (1 m,
  Shift 10 m, Ctrl 10 cm), turn it by dragging its arrow or with Q / E (15°, Shift 1°), type exact X / Y / height / turn, *Put on the ground*, pick
  what it spawns, or delete it (Delete key). *Show on the Map* opens the Map page with the camera on that place and the
  place selected (its sector loads when it is not shown yet). The map now zooms in to 2 cm per pixel, draws a metre grid
  close in, a scale bar, and the cursor's sector and coordinates. Every change is one undo step and Export mod writes it
  like the other spawn edits; checked on the game's files by moving a real vehicle spawn point, exporting, and reading the
  new position back from the written island data. Whether a vehicle then appears there, facing that way, only the game
  shows. The map's *Move* tool is now called *Pan* (it moves the view, not a place).
- **Each page as its own mod** (owner): the Vehicles, Weapons, Items and Spawns pages have *Export this page*, the
  Craftables page has step 6 *Export craftables mod*, the Map has *Export map only* under its History (its header's *Export
  mod* still builds the whole project in one pak) and the Economy page keeps its own *Export* (EconomyOverride.json, a server
  file, no pak). Each one writes only that page's changes as `pakchunk<N+k>-<Name>-<Page>_P.pak` for the client and, with the
  server folder set, the server, each with its `.sig`, and runs the package check on every pak. The game reads one
  `AssetRegistry.bin`, the highest pak's: every page pak carries its own records (it works alone), and the
  `pakchunk<N+19>-<Name>-Registry_P.pak` written after every page export carries the game's records plus those of every
  page pak in the export folder, so page paks installed together keep every clone known to the game (proven with the real
  game files: a Vehicles, a Weapons and a Craftables pak installed together). Each report's Install section says which
  paks to copy and which other page pak it uses (a Map pak placing a clone of the Items pak).
- **Craftables page in six steps** (owner: "too many options, unclear where it shows up, I could not find how to export"):
  1 what players get (pick from the game in 3D with *Add from the game*), 2 where it shows up (base building menu, crafting
  menu or both, with one sentence naming the menu and its section, e.g. "base BUILDING menu, section Furniture > Chairs"),
  3 ingredients (*Add ingredient*, *Fill in for me* from a material), 4 the picture in the menu, 5 file names and folder,
  6 Export. The station, electricity and the copied game object's values are folded under *More options*; every control
  has a short tooltip.

### Fixes
- **Objects the Map drew pure white are painted again** (owner: "the light-tower generator trailer, a white trailer, white
  fences inside a hangar are white on the Map but textured in the game"): many of the game's colour textures are streaming
  virtual textures, kept as tiles instead of regular mips, and the Map could not read them, so it drew those objects in
  their material's plain white. It now puts them together from their tiles: the light towers and generator trailers,
  1,293 textures of the game in all, show their paint. The storage shelves in the hangars took a packed utility map for
  their colour and are drawn in their own paint now too.
- **No more white and blue blotches over the far land** (owner, from high up over the forests): the game's own far view
  of towns, outposts and forests drew most of its pieces white for the same reason (967 of its 1,043 textures). From high
  up the far land is green with its forests, towns and coast, as the game shows it.
- **No more brown puddles with dark jagged rims on the ground** (owner, near the coast): on a coast tile the close-up
  ground detail went to the seabed under the water, so small patches painted on the land (dry grass in an outpost's
  gravel) were drawn flat and cut out with a dark ragged edge. The detail now goes to what is painted on the land and
  fades out softly where it ends. Gravel roads and dirt runways, which the game only paints into the ground, are now two
  soft wheel tracks over the ground instead of a flat grey or brown sheet, and *Fit to ground* over them sets things on
  the ground where the game draws the road (3.4.0 set them on the hidden road piece, up to 30 cm above it).
- **No white diamond inside the B_4 hangar, no white walls by the outpost** (check of the Map at Game-like): the diamond high
  on the hangar's back wall was the Map's own pin for a zone the game never draws (SCUM's `VolumeZone`, a brush and nothing
  else); an actor whose root is a brush is now taken for the volume it is, with no pin. Four engine cubes by the B_4 outpost
  (white walls 27 m and 5 m long) are hidden in the game on the actor itself (*Actor Hidden In Game*), which the Map did not
  read; actors hidden in the game are no longer drawn.
- **Roads in the far view are whole again** (check of the Map at Game-like): from high up the roads were pale broken dashes.
  Each landscape tile was drawn twice, by the island's terrain and by the game's far model of the tile (its ground under one
  island-wide texture, too blurred to show a road), each poking through the other. A tile's far model now keeps only its
  distant forest; the ground is the terrain's, roads and all.
- **No flat brown smudge on the B_4 airfield** (check of the Map at Game-like): a ground component with more paint layers on
  its land than the four the close-up ground draws left the smallest patch without grain (dry grass in front of the hangar,
  a blurred brown sheet; no road piece lies there). The four now go to the layers that leave the least ground without
  grain, and the ground they leave takes the grain of a fifth layer, the largest patch left.
- A project with a vehicle or item clone **and** craftables could not be exported: the Craftables pak's registry keeps
  the project pak's clone records, and the package check reported them as missing. It now finds them in the project pak.
- A map edit that deletes objects and copies as many others in the **same level** (delete one fence, duplicate another)
  could not be exported: the level's list of objects keeps its length then, every object after the deleted one moves up one
  place, and the package check read each moved place as a reference to another object ("…points at …; the game's points
  at …, which is still there"), so the export was refused. The list is a set to the game; the check now holds only its
  first place (the level's settings) to the game's and still checks every listed object (once, of this level, not deleted,
  none of the game's left out). Checked on the game's files: the whole project's paks and every page's pak (Spawns, Map,
  Vehicles, Weapons, Items, Craftables, Registry) of a project with edits on every page pass the check.
- **"It won't export": BP_Prisoner imports PP_LowLightVision_Inst** (report from a project called ServerMods, 3.4.0: "I
  have only cloned 2 vehicles and added stickers"): cloned cars and stickers never put BP_Prisoner in the mod (checked on the
  real game with the Rager, Laika, WolfsWagen and the Kinglet Duster plane). The same two cars then got no server pak on a
  real dedicated server: its game files keep every texture without pixels (a server never draws), writing a sticker into
  the server's copy failed, and the package check refused the server pak for the car materials that use the stickers. A
  new texture's server copy is now the server's own texture under the new name; checked with a real dedicated server's
  files, the Rager and the Laika with stickers get both paks from *Export mod* and from the Vehicles page's *Export this
  page*, each passing the check. A prisoner like that can only come
  in with an imported mod, one made before SCUM's update that removed `PP_LowLightVision_Inst`; when the game files the app
  had open had not read that mod, the package check held the mod's prisoner to the game's own and stopped the export. Now the export always reads the
  imported mods it carries (it opens the game files again when an earlier re-read failed), and a package carried from an
  imported mod is held to the mod as its author made it: a reference to something this game no longer has is a warning
  that names the mod ("made for another version of SCUM; ask its author for an update"), not a stop. It still stops when
  the mod's objects are built from what is gone (their class or template), and a broken import the export writes itself
  still stops it. The server's pak is checked knowing the client's game files: what only players' games use (the
  dedicated server's cook leaves it out) is no longer a hole there. Whether the out-of-date mod itself behaves in the
  game is up to that mod.
- **Laika and WolfsWagen: paint and stickers land where you put them** (bug report: "the Laika and WolfsWagen textures
  aren't syncing to the body correctly, and if you place a sticker on them anywhere it gets distorted and moved around the
  cars"): SCUM's car material reads the colour texture through the body's first UV set but the colour mask through the set
  its *Color Mask UV Channel* names, and that is the second one on the Laika and the WolfsWagen (the Rager uses the first for
  both). The studio read both through the first: the 3D view painted the wrong places, and a baked sticker took the paint
  off somewhere else on the car, so in game it stayed hidden under the paint while bare patches showed on other panels. The
  3D view, the live sticker preview and *Apply decals* now read and write the mask through its own UV set. A sticker is
  pressed onto the panel you click and runs on onto neighbouring panels only where their texture space is their own; when
  another place of the car uses the same texels (the left and right front doors of all three cars share some), a warning
  says once, right after you place or move the sticker, that it shows there too. Checked on the game's files: a sticker on a
  front door and on the hood of each car changes what the game's paint rule shows only under the sticker (3.4.0: the
  Laika's door sticker showed on none of its 3,873 sample points and changed 6,119 points on the body's other side and roof);
  the Rager bakes as before. How it looks under the game's own lighting only the game shows.
- **Page paks installed together keep every page's changes** (check before release, on the game's files): the Vehicles,
  Weapons and Items paks each sell their clones in their own copy of the traders' table, and the highest one hid the
  others' rows, so the traders stopped selling those clones; the Registry pak now carries that table with the rows and
  prices of every page pak in the folder. The Craftables page's pak, which gives an ingredient a tag in the item's own
  setup, now builds on the Weapons or Items page pak's copy instead of the game's, so their edits of that item stay; a
  page exported again under a higher page pak that ships the same package says so and names the page to export again
  (also the Map pak over newer Spawns edits). Melee damage goes into the Weapons pak, a trader price into the pak of the
  page whose item it prices, a stock vehicle's paint into the Vehicles pak, and the Spawns page's vehicle presets and
  animals into the Spawns pak (they went to the Map's or the Vehicles' pak, and that page's own export had nothing to
  export). When a page's server pak cannot be built, its older server copy is removed and the server's Registry pak is
  written again without it; the server's cook of a page reads the project's imported mods like the players' does. Copying
  page paks into the server's mods folder removes the same project's whole-project paks there, and the other way round:
  the two must never be installed together.
- Smaller fixes from the check before release: *Export this page* on the Spawns, Vehicles, Weapons and Items pages saves
  their unsaved changes first (it said there was nothing to export, or left them out). At the smallest window the 3D
  view's *Sun* / *Scale* / *Person* switches wrap instead of running out of view, and the Craftables step 2 rows (*Build it
  like the game's*, *Place in front*) wrap in long languages. The Craftables step 2 texts, the paint list's texts and the
  Spawns point card follow a language switch. The *Name in game* box no longer shows the game's text-table reference
  (the Rager's); it stays empty with its hint until a name is typed.
- **Vehicles decals: a whole-vehicle decal keeps its size** (check before release): placing or removing another decal,
  *Undo decal* or opening the project again rebuilt the decal list without *Stick to the surface*, and just picking the
  15 m decal in the list again then cut it to 3 m and turned that off. The list keeps both now. At the smallest window
  (1024x640) *Apply decals* and *Undo* were squashed to a few pixels under the designs; they keep their place at the bottom,
  and the designs and the decal list scroll above them.

### Changed
- **The Map flies and looks steadier in motion, like the game** (owner: "camera motion is not smooth like the game", trees
  and bushes "shimmer" while it moves). *Smooth camera* (Settings, on): each frame moves the camera by the average frame
  time instead of the uneven measured one (step spread 15.6 % → 5.5 % on 60 Hz frames that start 0-6 ms late), the first
  frame after a stop no longer jumps up to a tenth of a second ahead, a slow frame is spread over the next ones, and mouse
  turns are taken once per frame and eased over about two frames (turn spread 20.8 % → 12.6 % with a 125 Hz mouse). While
  the view flies the studio asks Windows for a 1 ms clock, so the 60 Hz timer that paces Avalonia's frames no longer runs a
  whole frame late now and then (2 % of its ticks on this PC, none with it). *Steady foliage in motion* (Settings, on):
  each frame is blended with the last one where the camera saw it (temporal anti-aliasing without the jitter): a slow pan
  over a forest of leaf cards flickers about half as much and stays closer to a 64-sample picture than a plain frame; at
  rest the view is drawn exactly as before. Far impostor trees blend into their next picture as the camera passes instead
  of snapping (one 1° step made the whole change, now at most 5-7 % of it), a cluster of trees near a LOD threshold keeps
  its LOD instead of swapping back and forth, and the Smooth switch no longer swaps foliage distance in and out every 2.5 s
  on a PC just at its limit (43 → 9 changes a minute; it waits longer after a raise that did not hold).
- **Weapons studio looks like the game** (owner's 3.4.0 test): the Weapons page opens on the **AKM**, guns are sorted by
  kind (assault rifles first with the AKM and AK-47 on top, then machine guns, SMGs, sniper rifles, rifles, shotguns,
  pistols, bows, launchers, improvised guns; melee and spare parts last; a clone right after its gun; the kind shows under
  each name). Guns are drawn **opaque with their own metal, wood and occlusion** (the game's packed AOMEC map): 3.4.0 cut
  them out by their roughness, so the AK magazine came out ghostly. **Paint blends with the gun's own texture** (the game
  multiplies it by the colour) at a gentle default **Strength** of 70 % with a slider, lifted at most 3x; the flat even coat
  is now an option, not the default, and the metal and roughness stay where the gun has them. The **magazines a gun's mesh
  carries** (the AKM: its AK magazine and the RPK drum) are laid out under the gun instead of inside it, each its own
  "Magazine" row to colour (a switch hides them); every paint row now says which part it paints (Gun body, Stock, grip and
  handguard, Magazine, Whole weapon …), its eye button shows that part selected on the gun, and the switch reads "Same
  colour on every part". A part can only be coloured on its own where the game gives it its own material. The 1.8 m person is a separate toggle, off on
  the Weapons page. A **Name in game** card sets the selected item's in-game name and copies its spawn command, and the
  clone card shows the clone's own `#SpawnItem` command before it is made. Only the game can confirm the in-game colour of
  a painted gun.
- **Vehicles studio, after the owner's test of 3.4.0** (an airplane and the Rager): the box man beside vehicles is gone; the
  game's own prisoner (orange jumpsuit, read from your game files, arms down) stands there for scale, with a *Person*
  toggle beside *Scale* to hide him. *Textures* says which material (and which parts wear it), what each slot is (colour,
  bumps, paint mask, shine…), shows a picture of what it holds now and whose it is; a replaced picture shows on the car at
  once and Ctrl+Z takes it off again. The decal library is redrawn from smooth curves: 98 designs in 11 categories (new
  Camo and Stars), racing numbers in a real broad-pen face instead of clock segments, baked at 1024 px. Drag a design from
  the library onto the car and let go where it goes. *Stick to the surface* projects a decal onto every part under it,
  each part taking its piece, up to 20 m (a whole vehicle). Added stickers are listed under *Stickers* and armed at once;
  an unreadable file says so. *Parts*: Remove, Game position and Ctrl+Z now show in the 3D view, an added part is drawn on
  the car (and can be removed again), and the panel says why when a part cannot be taken off or no model is chosen. The
  clone name box offers the game's name and a number (Rager1, Rager2 …) with its `#SpawnVehicle` command; Rager_1 typed is
  saved as Rager_v1 (the engine would read Rager_1 as "Rager" number 2).
- **Craftables: base buildings like the game's** (owner's "Base Cabin 03"): the game ships no recipe for its cabins
  (`BP_Base_Cabin`, `_02`, `_03`), so they were built on the improvised chair (20 health, not on the highest ground point under
  it so a 15 m cabin sank into a slope, allowed to tilt). An element without a recipe is now built as itself, with its own
  placement (highest ground point, upright, turned only around its vertical axis), 3000 health, 672 decay hours and repair
  values; a copy of the chair's recipe makes it (same asset names, ingredients, section and picture). A craftable made
  Furniture from such an element shows *Build like the game's* (one click, with the warning that what players built changes
  behaviour). New for every built craftable: *Needs a flag* (the element's `_canBePlacedWithoutFlag`, on every level) and
  *Repair cost %* (one repair-tool use restores `HealthRepairedPerUse` x 100 / %, so the uses follow the damage). A furniture
  or station craftable of 3 m and more is placed like the game's large buildings. Game code, not data: where the placement
  ghost appears (the game centres it on the aimed point; its own cabins too), Shift + mouse wheel distance, and what one
  repair-tool use consumes (the tool's data, not the recipe's materials).
- **Craftables: Place in front (experimental)** (owner: a 15 m cabin appears around him): a built craftable can be placed
  ahead of the player instead of around the aimed point. Its recipe gets `PlacementLocationOffset` (a recipe value two of the
  game's own recipes use, the Woodland pack's curtains, with the same placement ghost), which moves the placement point
  itself: the ghost and the element built from it move together, collision, snap points and ladder with it (the element is
  one mesh component; the ghost is another actor, so no element offset could move both). Off by default, suggested from
  3 m; the distance is automatic (half its size + 1 m, at most 15 m: the 15 m cabin 8.5 m) or set in metres, along +X
  unless a game test shows another direction. With the switch off the export is byte for byte the one before.

## [3.4.0] - 2026-10-17

### New in this release
- **Replace a sentry robot with NPC guards** (igor8802: "when creating patrols at points of interest, the robots can be
  replaced with NPCs"): on the Map, a selected sentry robot spawner has *Replace with NPC guards*. Pick the armed NPCs (one
  of the game's NPC encounters, guards or drifters of a level), how many posts along each route, and whether every robot of
  the place goes. In one undo step the robot is removed (its guarded zone forgets it), a group of NPC spawn points stands on
  posts along its route, and a new threat zone over the posts gets its own copy of zone settings in which only that NPC
  encounter has a weight. SCUM's NPCs cannot walk a route: their AI (the guard and drifter controllers) has idle, alerted,
  combat, investigate and return-to-post states and no patrol, and no spawner or spawn point stores a path for them, so
  they stand guard at the posts. The panel lists what happens before anything changes. Checked on the real barracks: the
  client export, and the server export made from the same game files, has the robot gone, its manager listing the other
  seven, the posts and the guard zone in the island's spawn data, and the zone's settings weighting only the guards. Whether the guards appear at those posts in a
  running game, and how the guard zone shares the place with the place's own zone, only the game shows: try it on a local
  server first.
- **NPC group points**: the island's groups of zombie / NPC points (1,883 of them, where an encounter that spawns a group,
  as the armed NPC ones do, puts it) are spawn places on the Map and in Spawns › Zombies and NPCs. They move, copy and
  delete with all their posts.
- **Craftables: your own asset names and folders** (HexTer: "customize the names of crafting recipes and furniture items
  in the crafting section before exporting the mod ... organize things neatly"): each craftable has an *Asset name* box
  (`CR_` + it is its recipe, `BP_` + it what is built) and a *Folder* box (Furniture, Lights, Storage … or your own, sub-folders
  with `/`). Its recipe and element go to that folder inside the game's recipe and element folders
  (`…/Recipes/Placeables/Furniture/CR_SS_Chair`, `…/BaseElements/Furniture/BP_SS_Chair`), the way the game keeps its own
  Asian decor pack in `Placeables/AsianDecorDLC`; a station's ingredient tag stays in the game's tag folder, where recipes
  look for it. A new building craftable gets a folder from what it is (Furniture, Lights, Storage, Beds, Stations); an
  item recipe stays beside the game's unless you choose a folder: the game keeps placeable recipes in sub-folders of its
  own (`Placeables/Skins`, `Placeables/AsianDecorDLC/Upgrade`) but none of its item recipes, so an item recipe in a
  sub-folder is not tried in game yet. Both can change until the first export: a written Craftables
  pak records its craftables in `craftables.json` (`Exported`), and from then on their asset name and folder stay, with a
  badge and a tooltip that says why (the game saves what players built by its asset path). The name shown in game still
  changes at any time, and Undo does not step back over an export. A name is checked as it is typed: letters A-Z, digits
  and `_` (a letter first, at most 64), never `_` and a number at the end (`Shelf_2` is `Shelf` #3 to the game; `Shelf_v2`
  is suggested), no name another craftable's files would get, and none the game has in any folder (the game knows a recipe
  or an element by its last name only); the export stops with the reason if one gets through anyway. Craftables of a
  project from an earlier version keep their names and stay beside the game's files: nothing is renamed, and since those
  files did not record exports, they are treated as exported.
- **A large fridge for food** (defolt_0999: "It would be great if we could install large refrigerators for storing food"):
  the game's fridge, picked in the Craftables gallery, becomes a built element: the game's large wooden cabinet spawning a
  copy of the game's fridge instead of the cabinet, placed and owned like a player's chest. It is never a world item
  spawner, so it cannot leave phantom fridges behind (D02). Its *Storage* card sets rows, columns (1–255) and the most it
  holds; the game's fridge is 30 × 15 and 500. Keeping food cool, and needing a generator nearby for it, is the code of the
  game's fridge class (`RefrigeratorItem`): the copy keeps it and a pak cannot change it. Not yet tried in game. The same
  card sets the size of any chest, cabinet or wardrobe element that spawns a container. The fridge is only built: it fits
  in no inventory, so it is never crafted into the bag.
- **Vehicle decals like the game's tattoos, and more control** (owner: "decals like the game's tattoos, and more
  control from outside"; m7ammed: "importing textures"):
  - *Game tattoos*: a new chip in the decal library shows SCUM's own tattoos with their real pictures (78 of the 90 in
    the game's list: the disabled ones and the two of the Supporter Pack DLC are left out, so a mod does not hand paid
    pictures to every player). They are read from your own game files when shown and kept in the thumbnail cache; the
    studio carries none of them. One goes on the car like any design, tinted with one colour as the game tints a tattoo
    (black ink by default), with opacity, size, turn, flip and Other side.
  - *Placing like a tattoo*: click on the car to put it there, then on the 3D view Ctrl+wheel sizes it, Shift+wheel
    turns it, the arrows move it along the surface (Shift: 10 cm), Q/E turn, +/− size, X flips, M or Other side puts the
    same design mirrored on the car's other side where the surface is (a layer of its own), Delete removes, Ctrl+D copies.
    No handles are drawn in the 3D view: the wheel, the keys and the fields do it.
  - *Layers and exact values*: an eye (a hidden decal is not baked) and a lock (the 3D view cannot pick, move or turn
    it) on every decal in the list; exact width, height (stretches the design), turn and position in cm (the position
    goes onto the surface along the decal's normal).
  - *Your own pictures*: Add sticker takes a PNG with its transparency, a JPG, or an SVG drawing; an SVG gets a colour
    picker per colour of the drawing (filled shapes, groups and transforms; strokes, gradients and text are not drawn).
  - *Presets*: save a clone's decals as a preset of the project, add a preset to another clone, or copy another clone's
    decals onto this one. Checked on two Rager clones: the same bake byte for byte. Every action above is one undo step.
  - Only the game can confirm: a game tattoo baked into a clone looks right on the car in the game's light and under
    the paint the player sprays, and Other side sits where expected on a vehicle that is not symmetric (checked on the
    Rager: 0.07 cm from the mirrored point).
- **Items page: clone any item, not only weapons** (CwHedgehog: "duplicate some in-game items, such as copper coins"):
  a new Items page lists every other inventory item of the game (2,351 in 29 folders: coins, medicine, food, resources,
  clothes, weapon attachments …) with the same 3D preview, search and folder categories as the Weapons page. Clone one under a new name
  and it gets its own item class, entity setup and registry records, so `#SpawnItem <name>` finds it; its name,
  description, weight, stack size or uses (`MaxQuantity`), inventory size and its trader row (when the traders sell the
  original) are editable, and so is every other stored or inherited value. Copper coins have no stack size in the game
  files, so theirs is not offered.
- **Materials per skill level** (CwHedgehog: "adjust the materials required for each level of Engineering/Survival"):
  every ingredient of a SCUM recipe stores its amount for each level of the recipe's skill (no skill, basic, medium,
  advanced, above advanced; Engineering for buildings, Survival, Demolition, Rifles or Medical for items). On the
  Craftables page an ingredient's **Per level** switch shows the five amounts to set yourself (off, the higher levels pay
  less, as in the game's recipes, and the page shows what they pay). **Game recipes** opens the game's own recipes: search
  one (the workbench, a bow …), add it and set what each level pays for every ingredient, with the game's own values
  shown and a Game values button. This is an explicit override: the Craftables pak ships that recipe at the game's path
  for client and server, with only those amounts changed, and the export warns that another mod changing the same
  recipe conflicts with it. A recipe whose ingredients a game update changed is not exported and says so. Every change is
  one Undo step.

### Fixes
- **A drawn bridge blocks at its railings, not only its road** (Discord Blondiiile: "I got too close to the edge and
  fell through, the bridge railing has no collision"): a bridge drawn with the studio only stopped a car on its deck, so
  a wheel near the edge dropped past the railing. The collision a drawn piece carries now follows the mesh's railings,
  posts and curbs as well as its deck (a wall at each steep face, standing on the deck or curb under it), fills the thin
  gaps between a plank deck's boards, and does so on both straight and bent pieces; players, vehicles and shots are
  stopped the same, and the server pak gets the same collision as the client. The deck itself is unchanged.
- **Crafted things are the game's own** (seviscache, #10: "craft the Animal Bait Feeder ... it should work as it normally
  does"; "if we wanted to craft clothing; once created, we should be able to equip it as usual"): checked on the game
  files, a *Craft* recipe makes the game's own class (`Camouflage_Jacket_01_C`, `AnimalBaitFeederItem_C`), never a copy,
  so a crafted jacket is the real jacket and a crafted feeder the real feeder. Wearing and baiting them in game is not
  tried by the tests.
- **A craftable like the game's element says it is built**: a bed, a chest or the large fridge made *Like the game's*
  showed "Made in: Crafting" and checked a chosen section against the crafting menu's item sections; it is built from the
  game's placeable recipe, so it now shows as built and takes the building sections, and its "Shows in" default is the
  game element's own section (it named the improvised chair's recipe).
- **Clothes in the Craftables gallery and the ingredient picker** (owner: clothes in 3D with their textures, in a Clothes
  category): items of a DLC are now listed in the category of what they are, by their class, instead of under their pack:
  a DLC jacket is under *Clothing & armor* with the others, a DLC bow under the ranged weapons. Every clothing item
  already showed its own model with its textures; all 848 were checked on the game files (backpacks, vests, helmets and
  shoes included).
- **A bed shows a bed, a fridge a fridge** (Jenni, Discord D07: her sofas, beds, fridges, cabinets, stoves and tables all
  showed the improvised chair in the base building menu; owner: "a bed must show a bed, a fridge a fridge, a table a
  table"): a craftable the game has no picture of (one made from a static mesh) now gets the real picture of its object,
  drawn from its 3D model with its own textures at export like the game's icons: the object alone on a transparent
  background, seen from the front left and a little above, filling the square, at the size of the game's icon. It is
  written as a new texture of the mod next to the craftable's recipe (`ICO_<asset name>`), in the stock icon's format
  (BC7, sRGB, one mip in the package, UI group), registered like the stock icon, and the recipe's `Icon` names it (a power
  craftable's generator item too: an item recipe has no picture of its own). The Picture box keeps the game's pictures and
  adds *3D model* (draw it even when the game has a picture) and *Import…* (a PNG or JPG of yours, kept in the project's
  `icons` folder and fitted at export). The name typed is the recipe's caption and description and the element's name,
  and a rename changes only that text, never an asset name. Checked with the game's reader on a sofa, a bed, a fridge and a
  table: four different pictures, none the chair's, each with a transparent background. Only the game can confirm the
  crafting menu showing them. A PC without OpenGL 4.3 keeps the template's picture and says so in the export report.
- **Dragging a decal no longer stutters**: the 3D view baked the decals on the UI thread, 70-190 ms for every step of a
  drag over a 2048 px texture. The bake now runs on a worker, 1024 px while the drag lasts and full size on release, and
  the 3D view shows the newest finished bake: under 2 ms of UI thread per drag step in the timing test.
- **Every export is checked before it is packed** (owner ALLAWI32: "with the mod the game crashes when I leave the
  server or press Alt+F4; I want everything safe, 100%"): every package of the client and the server pak (and the
  Craftables pak) is read back against the game's own copy of it, object by object. The export stops and writes no pak
  when an object does not read exactly as the game's does, a reference points outside the package or at an object the
  export deleted (a door's or switch's target, a group's member, an attach parent), the level's actor list holds a
  deleted object, a component or an actor twice, two objects share one name, an attach chain loops, something is
  imported that neither the game nor the mod has, a light or built-lighting id repeats, a sound event is the other cook's
  (a dedicated server's stub, or the game's whole event), or a registry record names a missing package; the list is in
  `package-check.txt` next to the staging folder, and the export summary says "Validator: N packages checked, 0
  problems" (defolt_0999's 43-level Z_4 project: 83 packages, 24,510 objects in 2.3 s). `scumstudio project validate`
  runs the same check on a staging folder. What it found and the export now fixes:
  - a landscape tile whose grass was cleared kept its heightmap and weightmap textures' stored skip offsets at their old
    place (31 textures in defolt_0999's Landscape_Z_4_1b and _1d); moved textures now take them along;
  - a deleted actor stayed the target of the kept actors that pointed at it (the game nulls such a reference with a
    warning and keeps a null entry in a list, which code tearing the level down can walk into): single references are
    now written null and lists lose the entry (real levels: power poles' and mounts' `_connectionTarget`, which the game's
    own poles leave empty too);
  - a deleted vehicle trader left its bike and car lifts pointing at nothing (`_assignedTradePost`; every lift of the
    game's outposts has its trader): the lifts now go with their trader, with a line in the report.
  - a copied building kept its lamps' `LightGuid` and its reflection captures' `MapBuildDataId` (the abandoned city hall:
    9 lamps and a capture keyed the same built data twice in one level): copies now get ids of their own, as the editor's
    duplicate does;
  - texture clones (snow ground) were refused by the clone check once their textures' skip offsets moved with them;
  - the large fridge was refused: its element (the game's cabinet, made to spawn a fridge copy) still listed four of the
    cabinet item's parts among its imports (`BuriableComponent`, `ThrowingComponent` and two component templates) that a
    fridge does not have. Nothing in the element uses them, so they now name their class's own default object, which
    always exists, and the fridge exports.
  After a server export the two paks are compared: a level changed in one pak only, actors removed or added on one side
  only, or a clone in one pak only is listed with the server's warnings (players and the server would disagree about
  what stands where or what can exist).
  The cause of the crash on leaving or closing is not proven yet: players can send the game's crash folder and log as
  `docs/CRASH-REPORT.md` explains.
- **Import… on the Economy page shows EconomyOverride.json again** (Игорь: "The economy file is not visible because of
  the extra dot"): the picker asked Windows for `*..json`, so the file was hidden; every file picker now builds its
  pattern from the extension with or without its dot.

### New (experimental)
- **Holes in the ground for bunkers** (owner: "pick a square or a circle on the ground, make it bigger or smaller, press
  Delete, and it opens a hole, like the game's own bunker entrances"). Switch it on in Settings ("Hole tool for bunkers
  (experimental)"), then in the Map open Landscape > Hole tool: a red outline follows the cursor on the ground, a click
  puts it down, the mouse wheel or the Size slider makes it bigger or smaller in steps of 1.5 m (the landscape's own
  grid), a square can be turned, and Delete (or Cut hole) cuts it. Ctrl+Z puts the ground back. The export opens the
  ground's collision there, in the client pak and in the server pak, exactly the way the game marks its own 173 bunker
  and cave openings, so a player walks into it and falls through; nothing else in the landscape changes. In the Map the
  cut holes show crossed in dark red and clicks go through them. On the 174 landscape pieces that already carry one of the
  game's own hole layers the export now also **draws** the hole (opens the visibility layer), so there the opening is
  see-through like the game's bunker entrances; everywhere else only the collision opens and the game still draws the
  ground, so cover the opening with your own model (a hatch, a frame, a bunker entrance). The Map shows which is which
  before you cut: the ground glows green where the game can draw the hole and amber where only the collision opens, and a
  line under the toolbar says the same for the hole under the cursor. Drawing a hole on ground without the layer needs
  landscape shaders that only the Unreal editor can build, and a pak cannot add them. A hole replaces the whole landscape
  tile it is in: export again after every SCUM update. The first time the tool is used in a project it shows a warning
  list (re-export after every update, other mods that change the same tile, client and server need the same pak, cover
  amber openings, keep a backup, test on your own server), with a short reminder at export; "Do not show again" per
  project.

## [3.3.0] - 2026-10-16

### New in this release
- **Spawns by category** (owner: "categories: vehicles, zombies, NPCs, animals, players"): the Spawns page has six
  categories, Vehicles, Zombies, NPCs, Animals, Players and Server. Zombies and NPCs show the same threat zones, each with
  its own types: every zombie, drifter, guard or drone type a zone picks from is listed with its chance in its list and
  its share of everything the zone spawns, as real percentages that add up to 100 (0 % = that type never spawns), in a
  grouped list with a search box and a 3D preview of the selected type. Animals lists the hunting biomes with each
  animal's share. A list other zones use too says how many threat zones it changes ("changes every threat zone that
  uses it (12)"), a zone's own encounter weights included; a list the low-threat settings read says it changes the
  whole island. The save asks first, and the save of the whole page is one undo step.
- **NPCs instead of zombies, per type** (igor8802: "assign NPCs instead of zombies"): a zone's groups are named after
  what they spawn (two zombie groups read "Zombies (1)", "Zombies (2)"), every threat-zone folder is listed (events and
  DLC zones too, grouped by folder), and a zone that has no armed-NPC group says that a zombie weight of 0 turns it off
  rather than spawning NPCs; a list whose weights are all 0 shows a warning.
- **Players** (owner: "a first-spawn place for a new character"): SCUM's ServerSettings.ini has no key that chooses where
  a new character first appears. Whether the game's own spawn data (the first-play game mode, the spawn screen's
  settings) holds such a place is not checked yet; if it does not, "the first time at place X, then as usual" is server
  logic, which a mod pak cannot carry. The Players category says so and shows the respawn rules your server's
  ServerSettings.ini has (sector, shelter and squadmate respawn, their prices, first waits, cooldowns and cooldown resets).
- **The island map of the Spawns page** (owner: "a 2D map of the island (the real in-game map picture, zoom with the
  mouse wheel, pan) inside the Spawns page where I select an area: a whole sector, or any area I draw/resize myself"):
  the game's own map picture, read from your paks once and kept in the app's cache (it is the game's artwork, so it
  never ships with the studio); a picture you choose yourself wins over it, and without either the terrain is drawn
  from above. The picture is laid over the landscape's 20 x 20 tiles: its coast matches the terrain at 97.5 % and the
  A_0 outpost lands on its compound. The wheel zooms about the cursor,
  a drag pans, the A-D, Z x 0-4 cells are drawn with their names, and each kind of spawn place has its own glyph and
  colour (car, skull, shield, paw, flag; threat zones as their real turned ellipse), so the layers read even without
  colour; zoomed out, more than 3,000 places become counts per kind. An area is a click on a sector, a rectangle with
  eight handles (drag inside to move it) or a polygon (click the corners, double click or Enter closes it, corners can
  be dragged; Esc drops a shape in progress). The places inside an area are found among 30,000 in under 20 ms.
- **New spawn places, not copies** (owner: "create a NEW spawn (not a clone) and remove a spawn"): Export mod writes a
  new vehicle point, zombie / NPC point, tagged point, threat zone or hunting area into the island's spawn data. Where
  it stands, its size and what spawns there (the vehicle group, Exterior or Interior, a tagged point's tags, the zone
  settings, the biome) are its own. The rest is laid out as a stock place of the same settings and kept as that one has
  it: a zone's activation reach and low-population settings (a zone of the project's own settings uses those at a low
  player count too), a hunting area's priority, spawning switch and visitor distance. A new zone has no sub-zones and no
  drone approach points: those stand around the stock zone. A vehicle group or a tag the game does not know, and stock
  data with a member this version does not know, are refused with a line in the export report. Once made, a new place
  moves, copies, deletes and undoes on the Map like any spawn place.
- **A zone's own settings** (igor8802: "assign NPCs instead of zombies"): a threat zone or hunting area can now point at
  settings the island data does not use yet, such as the project's own copy of a zone's settings with NPCs in it; the
  export adds the reference the game needs to load it. Settings that are neither in the game files nor one of the
  project's copies (an undone copy, a typo) are refused with a line in the export report, not written as a zone that
  loads nothing.
- **What spawns in a zone, read from the game's data** (owner: "choose exactly which zombie or NPC types spawn there";
  igor8802: "assign NPCs instead of zombies"): an encounter now says what it spawns from the character presets it draws
  from (Zombies_*, Drifter_* / Guard_*, animals, dropships), no longer from its class name, so a zombie group named "NPC"
  or a numbered group is told right; encounters that take their presets from a parent class are followed.
- **Spawn points under the ground** (seviscache: "spawners below the terrain"): a check lists the vehicle points more
  than 30 cm and the zombie / NPC points more than 50 cm below the terrain (indoor points, bunkers and tunnels, the sea,
  threat zones and hunting areas left out) and sets them back on it, cars 20 cm and characters 5 cm above, in one edit
  that one Undo takes back. A check over the whole island keeps at most 24 landscape tiles in memory.
- **The Spawns page around the island map** (owner: "inside the Spawns page where I select an area ... create a NEW spawn
  (not a clone) and remove a spawn"): Vehicles, Zombies, NPCs and Animals show the map on the left and the selected area
  on the right: its places of the category, found among 25,000 while the page stays responsive. Check several to remove
  them or to change what they spawn (vehicle group, zone settings, biome) in one undo step; New place puts a new spawn
  place, not a copy, where you click, on the ground when the terrain is known; a threat zone gets its own settings in
  one step, so its weights change no other zone. The under-ground check (seviscache: "spawners below the terrain") runs
  over the area or the whole island, shows each point on the map and puts the checked ones, or all, on the ground in one
  undo step. The page reads the game's list of presets, zones and biomes in the background.
- **Projects with the new spawn edits need this version**: new spawn places, zone copies and the choice of what a place
  spawns are new journal records; 3.1.0 refuses a project that has them, so open such projects with this version or later.
- **Economy: Numbers and Code** (owner: "two views, numbers and code, full control"): the Economy page keeps its traders,
  pictures, prices and fame as *Numbers* and adds *Code*: the project's EconomyOverride.json exactly as the export writes
  it, in the game's own layout. Edit it or paste a whole file: it is checked as you type, each problem listed with its
  line and column (click one to go there), and applied as one step once it reads without errors. With errors nothing is
  applied and your text stays until you fix or discard it. A change in one view shows in the other at once.
- **Import… on the Economy page** (Discord: "Good day! The economy doesn't have a way to upload its own economy file..."):
  pick a server's live EconomyOverride.json, or any older list, and it becomes the project's economy in both views; a
  card says what changed (settings, traders, items, the traders' names) and that Ctrl+Z undoes it. A file with errors
  is not imported: it opens in Code with its problems listed, ready to fix.
- **Every value of the file is editable**: price, sale price, delta price, fame, on sale and sold-only-after-a-sale per
  item, each with its own Reset to the game's value, and the file's server settings (restock, rotation, price rules,
  gold) in a Server settings card, each with its own Reset. The file has no stock per trader or per item: stock is
  server-wide (traders-unlimited-stock, fully-restock-tradeable-hours).
- **A crafted bed is a bed** (owner: "if he adds any bed from the game as a Building, he can build it and it becomes his
  own; it must behave like a normal bed"; seviscache and igor8802, #12: crafted things must work like the real thing): a
  craftable made from a base element of the game, or from its mesh, is now a copy of that element's own family instead of
  the improvised chair: its recipe, the element, the game's whole upgrade chain (each level with its own recipe) and, for
  a chest, a lamp or a fridge, the item it spawns. A crafted bed keeps the game bed's respawn flag (`_isUsedForRespawn`);
  respawning at it is not yet tried in game. A chest is a container, a lamp a lamp. Health, decay hours and placement
  rules can be set per level; a value the game's data does not store for a level is reported, never dropped in silence. Both
  also adds an item recipe for the item a chest or a lamp spawns. The respawn time is not a value of a bed: it is the
  server's (`ServerSettings.ini`) and the game's `RespawnSettings`, the same for every bed. DLC and modular elements
  (walls, floors, doors) are not offered yet, and an element with mesh variations keeps its own look. Not yet tried in
  game: respawning at a crafted bed and upgrading a crafted element.
- **Craftables: ingredients picked in 3D** (owner: "what he pays for them also chosen in 3D, not typed"; CwHedgehog:
  "Can I add items as new crafting materials?"; seviscache and igor8802, #12): Add ingredient and Change open a picker
  with every item a player can carry, the game's and those of imported mods, as 3D pictures in the catalogue's
  categories (ammunition, weapons, food, medical, clothing, tools, crafting materials …), plus the game's ingredient
  groups (CI_) shown by their item; search, amount (1–9999) and Tool. Each ingredient shows how the recipe asks for it:
  *Own tag* (exactly this item), *Group only* (any item of its group) or *No tag*. *Only this item (new tag)* is off by
  default: ticked, the export adds a new tag to that item's entity setup, a file of the game, so other mods that change
  the same item conflict with it.
- **The element on the Craftables page** (owner: "all in 3D: I choose the type from pictures, I do not type names"): a
  bed, chest or lamp picked from the gallery is added as *Like the game's* element; the page shows the values each level
  of the game's upgrade chain stores, each with the game's value beside it and Reset, lists the ones a level keeps from
  its parent class, marks a respawn point, and says in its tooltip that respawn delay and price are the server's
  settings, not the bed's. Use picks Build, Craft or Both (Both only for an element that spawns an item). Changing the
  kind of a craftable that may have been exported warns that the objects players placed change behaviour with the next
  export; an existing craftable is never switched by itself.
- **A graphics-card check for crash reports**: `scumstudio render level <paks> A_4 --island --samples 4 --frames 120
  --gl-debug -o a4.png` draws a whole cell as the Map does (4 samples a pixel with `--samples 4`) with the graphics driver
  in its strict checking mode and prints every message the driver sends, errors and undefined behaviour first (exit code
  4 when there is one). On an AMD card this shows what AMD's own driver objects to. On the RTX 5080 here: the 56 levels
  of A_4 with the island, 121 frames, no problem.
- **Settings › Graphics card**: the graphics card the 3D views draw with (name, maker, OpenGL version), a warning when it
  is the chip built into the processor, when Windows draws without a driver or when a feature was turned off for it, and
  the 3D quality, spawn models, water, lighting and anti-aliased foliage switches in one place. **Use the high-performance
  GPU** tells Windows to run the studio on the faster graphics card of a laptop with two (Windows' own per-app setting,
  only when you click it; it applies after a restart and Windows Settings › System › Display › Graphics undoes it).
- **Get closer to things in tight rooms** (Discord, seviscache: "the camera clips the mesh, making it impossible to see
  exactly where you want to place the item"): Settings › *Near clip* chooses how close to the camera the Map still draws
  (1 to 50 cm, 10 cm by default), and Ctrl+Shift+N on the Map steps through the choices.
- **Go to coordinates on the Map** (Discord, defolt_0999: "Is there a way to find it using its coordinates?"): the new
  *Go to* button takes X Y and, if you know it, Z, in metres as the Entities list shows them or in the game's
  centimetres (X=… Y=… Z=…), says which it read, and puts the camera 25 m from the spot looking down at it. Coordinates
  typed in the Entities filter list the objects within 50 m of the spot, nearest first, so something you cannot see can
  still be found and picked.
- **Esc cancels a move, turn or scale** (seviscache: "ESC to cancel a move like Blender"): pressing Esc while dragging
  the gizmo, while dragging an object freely, or while dragging a Shape, scale or legs handle puts the object back where
  it was, and nothing goes into History or the export. A short note says the move was cancelled. Without a drag, Esc
  works as before (it leaves mouse look, or clears "all of this kind").
- **Views along an axis, like Blender and Unreal** (seviscache): the numpad turns the camera to look from the top (7),
  the front (1, along +X) or the right (3, along +Y); Ctrl with the same key looks from the other side, 9 looks from the
  opposite side of the current view and . frames the selection. The view turns around the selection, or around the
  ground in the middle of the screen when nothing is selected. A View menu in the Map header does the same. The keys work
  with Num Lock off too: Numpad 3, 1 and . no longer nudge the object down, drop it to the ground or delete it there.
  The orthographic view (Numpad 5) is not in this update.
- **Wireframe view** (seviscache): a header switch, F3 or Shift+Z draws the level as the edges of its triangles, to see
  through walls and find small or hidden pieces. The sky, the grid and the gizmo stay as they are, and a click still
  picks what is drawn. It is not kept when the studio closes.
- **Several levels at once, and Isolate** (seviscache: "load only the levels I choose"): in the World tree, Ctrl+click
  adds a level (or a cell) and Shift+click a range; after a short pause exactly those levels load together (for example
  all of Blato, its interiors and the Landscape_A_4_1 tiles), with nothing streaming in around them. "Show selected"
  loads them at once. The new Isolate switch in the header hides the island's terrain around what is loaded and stops
  the island streaming; Whole island turns it off. A plain click still loads one level, and Ctrl+A in the tree still
  selects all objects of a kind. At most 64 levels load at once.
- The Map toolbar settings (the gear at the end of the header, and Settings › Map toolbar) list the new View, Wireframe
  and Isolate items.

- **Vehicles studio: textures, decals, materials and parts of a cloned vehicle** (Discord, m7ammed: "could you add tools
  for editing materials and importing textures?"; owner: "full control: textures, materials, every part like Blender,
  tattoos and stickers like drift cars"): beside a vehicle's 3D view the paint panel is now a studio with four tabs, and
  the 3D view's left mouse button follows the tab (turn the view, point at the body, or drag a part's gizmo). Everything
  is changed on a clone only; the game's own car, textures and materials never change, and every step is one Ctrl+Z.
  - *Textures*: every texture of the clone's paint materials with its size and format. Replace… takes any PNG or JPG:
    at export it is fitted to the texture's size and written as a cooked texture in the game's own layout and format
    (BC1, BC3, BC4, BC5 or BC7 with every mip, sRGB as the original, small mips in the package and large ones in the
    .ubulk) under the clone's own name; the clone's material points at it and its streaming data names it, so it does not
    stay blurred. Save PNG… saves a texture for a paint program, Game texture takes the original back. The pictures are
    kept in the project's `textures` folder. Checked with the game's reader on a Rager clone: its body texture replaced by
    a picture reads back at 2048 x 2048 DXT1 with all its mips and the picture's colours.
  - *Decals and stickers*: a library of designs of our own, drawn in code (tribal, flames, stripes, racing and stencil numbers, army
    stars and camouflage, anime-style speed lines and petals, skulls, shapes, blank badges) plus any PNG or JPG as a
    sticker. Pick one and click on the car: it is projected onto the body where you click, along the surface there, so it
    wraps round curved panels and never reaches the far side. Click it again to drag it (Escape puts it back); size in
    cm, rotation, opacity, flip, a mirrored copy on the other side, layer order, one colour, a colour per part of the
    design, or a random pick from your palette that stays the same for the same variation. The 3D view shows the decals
    under the paint as you place them; they are saved with the project (`decals/<clone>.json`). Apply decals writes them
    into the clone's own colour texture and colour mask (a picture put on those two slots in the Textures tab is
    replaced), so the paint the player sprays shows round them; a decal on a spot of the texture another part also uses
    is reported.
  - *Materials*: each material slot shows which parts of the car wear it and gets Roughness (M_Car_01's paint roughness
    under the clear coat), Brightness (0-400 %: a gold over 100 % is stored brighter than white paint) and Saturation, with
    the finishes Bright gold, Dull gold, Chrome, Diamond and Plain, for one slot or every slot at once; Game colours puts
    every value back.
  - *Parts*: pick a part on the car and move, turn or scale it with the gizmo or the numbers, hide it (it stays on the car
    and keeps working), take it out of the spawn, show another model of the game in its place (an animated part only
    swaps for an animated one) or bolt any model of the game onto the car where you click. The clone gets its own copy of
    the part's socket on its own copy of the car's mesh, which the engine looks at before the shared skeleton, so the
    stock car and other clones never move. Wheels roll on the car's physics and stay where they are.
  - Only the game can confirm (`#SpawnVehicle` the clone, then look): the replaced texture close up and sharp at 50-100 m;
    each decal where it was put, with its colours and opacity, and nowhere else; the paint colours on the same areas as in
    the studio (the colour-mask rule); how a very bright gold or Diamond looks; a moved part at its new place, its
    collision, a moved door opening on its new hinge, a hidden part's collision; an added part spawning, and the item it
    gives when taken off (expected: a roof rack's); the stock car unchanged; a server with the mod and a client without
    it, neither crashing.

- **Far forests drawn as cards, as games do** (owner: "the game is very smooth, our app is not … high FPS and far
  render distance at the same time"): every tree and bush the foliage tools placed (3 m across or more, with lighter
  models) is pictured once from eight sides when its level loads, and past its lightest model it is drawn as one card
  turned toward you, still only up to the game's own cull distance. The card shows the side you look from, is cut out
  where its leaves are (clicks and the brush go through the gaps) and is lit like the leaves. A test forest of 20 000
  trees over 3 km draws 0.32 million triangles instead of 2.16 million (the 12 000 trees past 600 m: 24 000 instead of
  1.8 million); from each of the eight sides the card covers the tree's pixels to within 2 % and its colour to within
  2/255. Every model also turns its normals with three cross products a vertex instead of a matrix inverse, which a
  dense forest pays for millions of times a frame.
- **The game's own far view in whole-island mode** (owner: "high FPS and far render distance at the same time … smooth,
  optimized like the game"): every place not loaded in detail is now drawn with the merged far model the game itself
  draws there (the island's 1,523 far models: a building, an outpost, a quarter of a town), so towns and outposts stand
  on the horizon instead of bare ground. A place's far model gives way to the place in the same frame it streams in and
  comes back when it streams out. The models are prepared once per session in the background (the island shows at once,
  they appear when ready). Grass and bushes now thin out between the game's own start and end distances, as the game
  fades them, instead of all stopping at one line. `scumstudio render level --around X,Y,Z --far-models
  --render-distance 2 --frames 300` measures a view: frames over 33 and 50 ms, garbage per frame, GC pauses, and which
  meshes carry the triangles.
- **Smooth view and a far view in sectors** (owner: "The game is very smooth; our app is not. I want high FPS AND far
  render distance at the same time"): Settings › *Smooth* (on) keeps the Map's 3D view near 60 fps. When frames take
  longer than 16.7 ms for half a second it draws less, one step at a time: trees and grass first, then coarser models,
  then shadows, then small objects and the farthest sector; after two seconds with room to spare it brings them back.
  Texture detail never changes (that would load the levels again). Settings › *Render distance* (1 to 4 sectors around
  the camera, 2 by default) sets how far the island's far view reaches; the levels close by still load in full detail.
  A new quality, *Game-like*, draws what the game draws: its cull distances, its own model detail levels, everything
  down to one pixel. The line under the view now reads placements, drawn, triangles, draw calls, CPU and GPU time,
  frame rate, garbage-collector pauses per second and full collections, and *Smooth -N* while the view is lowered: a
  line to screenshot when it stutters.
- **Values a weapon inherits from its parent can be changed** (CwHedgehog: "I want to change this weapon's MaxHealth. It
  seems to be inherited from a parent?"): a cooked Blueprint stores only what differs from its parent, so a katana's
  entity setup has no MaxHealth (it is the game code's ItemEntitySetup default) and 2H_Katana1 has none of the values it
  shares with 2H_Katana. The Weapons and Vehicles pages now list them under the asset's own values, marked "inherited
  from ItemEntitySetup, editable" (a Blueprint parent's numbers and switches with the parent's value; MaxHealth of the
  game code as "game default", since it is in no file). A change writes the value as a tag of the parent's type into
  that asset's own defaults, so a clone gets it and the parent and every other item keep theirs; empty keeps the game's
  value. The CLI's item stats/set and the MCP item tools list and set them too. Melee weapons: the Hit damage part (their WeaponDesc_Table row) now comes right after the weapon, and the
  weapon's own damage values are titled "When thrown, not the hit damage" (CwHedgehog set a cloned katana's thrown
  Damage to 999 and nothing changed when hitting).
- **Your own 3D models on the Map** (Pheidian: "Is there a way to import custom models into the editor?"): Add object ›
  My models lists the project's imported 3D models with their 3D picture. They are the Craftables page's models: one
  import, used in both (the Craftables Add box offers them too). Place puts one where the camera aims as a new object of
  the project: the gizmo moves, turns and scales it, Ctrl+Z undoes it, and typing its name in the Add object box works
  too. Import 3D model… is the Craftables page's import (FBX or OBJ, cooked by Unreal Engine 4.27); without the engine
  the button says what to install instead of failing. Players collide with the model as the import made it: they can
  walk on it and into it. Export mod puts the model into the pak with its placement, in the client's pak and the
  server's. A placement whose model files were deleted from the project is left out, with a warning. A shared mod
  (Share your mod) does not carry the model files, as with an imported craftable: whoever imports it gets that warning. Tested
  in the studio with a stand-in model (the game's table renamed into an import); a model cooked by Unreal Engine 4.27
  standing on the island is not yet confirmed in the game.

### Fixes
- **Every craftable shows its own picture** (Jenni: "every craftable shows the improvised chair"; she replaced the icon
  and it did not show): the copied recipe's `Icon` was never written, so each kept its template's picture. It now shows
  the item's own inventory icon, or the recipe picture of the game element it is, and a Picture box on the Craftables
  page picks any of the game's pictures (empty = its own). A mesh with no picture of its own still shows its template's
  (pick one in the Picture box). A picture rendered from the mesh is not made yet.
- **Buildings show in the base building menu again**: the wooden wall's recipe stores no menu section (the game files a
  modular piece's section only on its twig tier; wood and metal are its upgrades), so a craftable made from a building
  had no section to default to. It now uses the walls' section, and an upgrade tier finds its first tier's.
- **Craftables review**: an item's entity setup that an imported mod or another mod pak overrides is never given a new
  tag (its bytes would ship); the Craftables export ships exactly the opted-in item setups at their game path and still
  refuses any other; a crafted element's upgrade recipes lose their skin links (a stock skin upgrade would turn it
  back into the stock element); element values are no longer clamped to 0..1,000,000 on display; the Kind box offers
  Element once the source's element family is read.
- **Copies keep everything that was changed on the original** (seviscache: "after pulling an object with the orange
  handle, Duplicate gives a copy without that change although the scale values are equal"; igor8802: "a duplicated
  sentry's patrol route cannot be changed"): Duplicate, Copy/Paste, Extend, group copies, prefabs and the AI's duplicate
  now carry the bend, handle pushes, ends and legs, moved or hidden parts, replaced meshes, spawn and patrol points, loot,
  the grass setting and the scale you just pulled; a building pasted into another level keeps its repainted walls (owner:
  "some placed buildings have textures in the wrong place"); a copy of a copy still works after the original is deleted;
  the loot panel of a copy shows the loot the game gets; a copy set back to its own loot keeps it; a copy's moved, hidden
  or replaced parts show on the Map as the game gets them, and its parts are picked where the copy stands; the AI's copy
  of a locker into another level keeps its loot.
- A duplicated or pasted sentry's patrol route can be changed and is exported; a copy's moved or deleted parts, replaced
  meshes, road sways and loot points reach the game too (igor8802, seviscache).
- Pasted and bent buildings keep their painted materials in game (the owner).
- Indoor loot points that come from a building's Blueprint are written when moved (seviscache).
- **Spawn points on copies and indoor loot points** (igor8802: "a duplicated sentry's patrol route cannot be changed";
  seviscache: "indoor loot points show no gizmo when selected"): the patrol and loot points of a copied sentry or spawner
  are drawn where the copy stands and go with it, and moving one no longer snaps back; a house's indoor loot points, which
  its Blueprint keeps, are points of their own with a gizmo, Duplicate and Delete, like a spawner group's; the gizmo of a
  point of a moved or turned spawner stands where the point is drawn; Fit to ground (End) on a point sets down that point
  instead of the whole building or spawner, and on a loot point the building's class keeps it says why nothing moves.
- **Spawns keeps your unsaved changes**: changing a setting no longer throws away the moved sliders (only another server
  folder reloads the server's rows); after undo or redo a save starts from the project's current values instead of
  failing as "out of date"; one slider over several values (the chance of every part) is one undo step; two presets of
  one vehicle share the server's limit row instead of writing the same key twice.
- **Values as the game and your server store them**: a weight of 0.75 is no longer shown as 1, 500 zombies or a 500 m
  distance no longer stop at the slider's end (the slider widens and says the value is beyond the usual range), and
  ServerSettings.ini values written by hand (`2`, `true`, `1`, `1000000`, `-1`) no longer count as changes. Every row has
  a back-to-the-game-value button and shows the game's value in its hover text; "the game decides" (-1) is its own switch.
- **ServerSettings.ini stays as you wrote it**: saving changes only the values of the keys you moved; comments, spacing,
  line endings and a byte order mark stay byte for byte, and a new key goes after the last value of its section. The
  first save ever keeps your original file as ServerSettings.ini.bak and never overwrites it; each session's first save
  keeps the file as it was then as ServerSettings.ini.session.bak.
- **Spawns messages**: the part-chance slider changes only the parts (not the preset's own chance) and says how many
  parts use the game's default; a zone that cannot be read says so in your language instead of an empty page; the units
  are translated; the save message names the ServerSettings.ini it wrote and says to restart the server, and the page
  says to press Save before Export mod.
- **Spawn points inside placed houses**: Export mod removed every vehicle and zombie point inside the box of any placed
  object, so a placed hangar or house emptied its floor. Only rocks and cliffs remove the points in them now, and the
  export report names each point it removed.
- **Spawn place edits that were dropped without a word**: a turn of a place whose game data stores no rotation, the
  scale of a point, and edits of places the game build does not have (another update, the server's files) now write or
  say why not in the export report.
- **Spawn place edits on the wrong places in the server pak**: the edits name stock places by their number in the
  client's lists. The server pak now gets the removed, moved, re-chosen and copied stock places only when it is exported
  together with the client, its lists are as long and each edited place stands where the client has it. Otherwise it
  gets only the new places, and the export report says why.
- **Fit to ground on a spawn place** (seviscache): a vehicle or zombie spawn place of the island stood where it was with
  "No ground under it"; it now goes down onto the ground like any object.
- **Fit to ground on one point of a spawner** (audit): with one patrol point of a sentry (or one loot point of a spawner
  group) selected, the whole spawner was set down; now that point goes down alone. A loot point of a building cannot be
  fitted (it moves with its building), and the button says so by being off.
- A landscape tile that cannot be read (a mod's or a plugin's) no longer stops the ground checks; it has no ground.
- **The Economy page keeps the file's own values**: a value the file pins (even to the game's own) or leaves at -1 stays
  that way when you edit another value of the same item, and an item your game files do not know (a mod's) keeps the
  file's values and is left alone by the bulk buttons; such items used to be written out as 0. -1 in a box is the game's
  value (it became 0), +10 % can no longer turn a huge price negative, and a box is read when you leave it or press
  Enter: "12,000" and "12 000" are 12000, and a typo says so and changes nothing.
- **Every section of the file is on the page**: a trader that is neither the game's nor placed on the map shows as "not
  on this map" with its items instead of being invisible, and new game files rebuild the shown rows with their prices.
- **Without a project the Economy page says so**: every editor is off instead of taking edits it could not keep. The
  export toast says that the file replaces the server's own (import it first), the row marks are named by what they
  mean, not by their colour, and game tables that cannot be read say so with a Retry instead of loading for ever.
- **EconomyOverride.json kept exactly as read, in the game's own layout** (owner: "export writes it back unchanged where
  not edited"): the project's file is now written byte for byte as SCUM writes its own (an object's brace on a line of
  its own, tabs, CRLF, no newline at the end), and nothing read is lost any more: keys the app does not know, a price
  written `12,000`, a delta with six decimals or a `yes` stay as written until you change that very value; an edit
  rewrites only the value it changes, and a reset never drops what the app cannot read. A server's live file or an
  older list (with or without its `economy-override` wrapper) can be imported into the project: it becomes the
  project's economy, the placed traders keep their sections, the project's file is backed up first and the change is
  one Ctrl+Z. Files with a key twice, a section twice or a `"traders": []` open instead of failing, and a code edit is
  checked first (line and column of each problem: unknown trader, unknown item, bad number).
- **Ctrl+Z undoes economy edits**: every economy change (a price, a whole trader, a setting, an import) is one step of
  the project's history, so Ctrl+Z and Redo undo the economy instead of the last map edit, also after reopening the
  project.
- **A placed trader's prices survive undo, reopen and redo**: a placed trader that leaves the map (deleted, undone, or
  placed by an edit that a later one dropped) keeps its section in the project, hidden and left out of the export,
  until an undo or redo brings it back; its name stays taken meanwhile, so a new trader cannot take it.
- **Economy files are never lost**: a file the app could not read, one it writes differently, or one changed by hand
  while the project was open is kept as a timestamped `EconomyOverride.json.<date-time>.bak` before the next write (one
  `.bak` was overwritten every session), and the message names that copy even before the Economy page was opened. A
  file that could not be read is only replaced by an edit or an import of yours, never by a trader placed on the map.
  One "Economy not saved" message instead of one per keystroke.
- **Export with a damaged economy file** goes on without the file and says so in its warnings instead of stopping, and
  never writes the game's defaults in its place (an economy-only export and the Economy page's Export say why and write
  nothing); a project that only changes prices can be exported (it writes the economy file, no game files needed);
  Export mod uses the Economy page's file; a trade post copied and pasted into another level keeps its trader's
  section. The note and the export report now say that the file replaces the server's own and to back that up or
  import it first.
- **Cloned vehicles no longer crash the game after a restart** (Discord, jenni_1129: "frist time u can spawn them in the
  game .if u close the game and go in the game will crash"; m7ammed: "if you restart the game … the game crashes"): a
  clone copied the vehicle's trunk (its item container) but not the trunk's entity setup, which every stock vehicle has,
  so the saved car came back with a trunk the game had no setup for. A clone now takes the setup too, under its own name
  (`RedRager_Item_Container_ES` for `BPC_RedRager`), and exporting a project that cloned a vehicle with an earlier
  version adds it. Export again and spawn new cars; a car saved with an earlier export may still fail to load.
- **Copied lights, fire, smoke, Niagara effects and sounds no longer break the level they go into**
  (seviscache: "I got a "corrupted data" error when I launched the game" after copying light emitters and
  particle/Niagara Blueprints; thezombie_97's crash in Samobor may be the same fault). Three faults of the copier are
  fixed. A Niagara effect's own settings (the moths around most street lamps and reflectors, flies, smoke) were copied
  with names and a type that belong to the level they came from, so the game read wrong names or broken data; they are
  now carried over properly. A copied object did not wait for its new level while the level loads, as every object the
  game itself cooks does, so a plain light, a smoke or fog emitter or a Niagara effect could be made before its level
  existed. And a copy shared its light's and its meshes' baked-lighting keys with the original: it now gets keys of its
  own, as the editor gives an object it moves, so a copy never borrows the original's baked light or shadows. Copy,
  Duplicate and Paste on the Map copy lamps, fires, effects and sound emitters. A volume (fog, sound or post-process
  area, item spawner zone) is still not pasted into another level, because its shape cannot be moved there yet: the Map
  says so at the paste, and a group paste leaves it out and pastes the rest. A copy into another level is also refused
  when a body, foliage or landscape part holds data the studio cannot move between levels, or a value it cannot read
  (the export report names the part); what is safe is carried over properly (a body's collision format, field path
  owners, string table texts).
- **The server pak no longer crashes the dedicated server with sound events of imported mods** (taishan_song): the
  dedicated server only loads its own 12-byte empty sound events and died with "Serial size mismatch" on the ones a car
  mod brings. The server pak (and the server's Craftables pak) now gets the server's own empty event in their place, read
  from the server's game paks; when none can be read, the server pak is not built and the message says why. A client pak
  that would carry such server-only events (a mod imported from a server's mods folder) is named in the warnings.
- **Every export says where its paks go** (Qraox, taishan_song): every player needs the client paks, the server the
  server paks, Craftables included, and both restart. A Craftables pak built for one side only is named, an old server
  pak no longer stays beside a newer client pak (it is removed from the export folder, and one left in the server's mods
  folder is named), and the export report has an Install section.
- **Setup refuses the dedicated server's Paks folder as the game folder** (Markus6789: the map drew nothing): it names
  the game's folder to pick instead, `...\steamapps\common\SCUM\SCUM\Content\Paks`.
- **Item spawners warn that the server keeps what they spawned** (defolt_0999, O Piqueno: "phantom fridges"): a fridge,
  stove or other fixed item placed with Add object, a prefab or a paste is a world item spawner, and the server saves the
  item it spawns, so deleting or undoing the spawner later leaves the item until an admin removes it in the game or the
  server is wiped. The Map now says so when one is placed or deleted, and the export lists (report and message) the
  spawners it no longer has, also those an earlier export placed. A pak cannot remove an item the server already saved:
  the game's spawner has no "spawn once" or "do not save" setting (only the item and its chance to be usable).
- **No more flickering trees and bushes after deleting or moving them** (Discord, defolt_0999: "the flickering bushes and
  trees are still there"): deleting or moving a tree or bush threw away the game's ready-made draw data of every tree or
  bush of that kind in the landscape square, so the game had to rebuild it, and a moved tree that was turned or resized
  stuck out of the box the game culls it by. The export now writes the edit into that draw data and remeasures only the
  boxes that hold the changed trees, the way the game's own tools build them; everything else stays exactly as the game
  has it. Export the mod again to get the fix. Copying a tree into a square still makes the game rebuild that kind there.
- **AMD graphics cards: the studio no longer goes down when the 3D view hits a graphics error** (reported by Pheidian, RX
  9070 XT: "LOT of crashes ... when loading areas", most at military POIs; other AMD users at the cities). A frame that
  fails is now caught: the 3D view puts the graphics state back, turns off the features 3.2 added (anti-aliasing and smooth
  leaf edges, then compressed textures) and draws on, and after five failures in a row it stops with a message instead of
  the app closing. Anti-aliasing now takes only a sample count the driver lists for both of its formats, and a driver that
  refuses the anti-aliased buffers, their resolve or the water's copy of them gets one sample. A compressed texture the
  driver refuses is decoded instead, and an error left over from another upload no longer stops a whole area from loading.
  The log now names the graphics card, its driver and OpenGL version, everything turned off and why, and the driver's own
  error messages, so a crash inside the driver still leaves a trail.
- **Cliffs, shelves and cabinets no longer turn purple-blue on the Map** (Discord, seviscache: "There are some interior
  and rock models that load the "normal" map—the relief texture—instead of the color texture"; defolt_0999: the rocks
  around Prvo Selo): where a material's own colour slots hold placeholders, the studio took the snow layer's texture
  from SCUM's master material, which is a normal map. 471 materials did (the cliff rocks, Big_Rock_01, the market
  shelves and freezers, file cabinets, metal fences, road sides), and the far hills took their normal map too. Snow,
  moss and dirt layers and textures named as normal maps are never taken for the colour now: the cliffs draw the rock
  master's rock texture, the furniture its own base tint. Untextured surfaces also stop taking an emissive or
  subsurface colour as theirs, so they are not drawn black instead. The Assets page's thumbnails are made again once, so
  none keeps the old colour.
- **Flying back over the island no longer stops the Map with "Cannot access a disposed object"**: when the camera turned
  before the next sector was shown, the objects already sent to the graphics card for it kept textures that the card's
  cache then freed, and coming back to them stopped the drawing. Those textures now stay as long as the objects that use
  them. (Found while checking the purple-blue report above; the Map's textures were otherwise each drawn on their own
  objects, also after being freed and sent again.)
- **Zooming in no longer cuts the Map away after you load another sector** (Discord, seviscache: "If I zoom out, Blato
  A4 becomes visible again, but if I zoom in, it gets cut off"): the camera shot of a newly loaded sector fitted the near
  clip to the zone still drawn, kilometres away, and nothing reset it while the island was behind, so everything closer
  than that was cut. The near and far clip are now worked out for every frame from what is drawn.
- **A failed update says why, in full** (Discord, seviscache: the update window showed "La actualización falló:" and
  nothing after): the reason now has its own row under the progress bar, wraps, shows in a tooltip and has a Copy button
  for a bug report; an error without a message names its kind, and inner errors are added.
- **The console opens again after errors** (Discord, seviscache: "got five errors, but I couldn't open the console until
  I restarted the program"): after the Not-a-Number errors the Map's saved camera held NaN, saving the view state threw on
  every change, and opening the console (which saves that it is open) stopped before the panel showed. Saving the view
  state never throws now: a camera that is not a number is not kept, and one view that fails to follow does not stop the
  others.
- **The Craftables gallery finds every item the Assets page finds** (Discord, seviscache: "when I try to search for
  "AnimalBaitFeeder," the Craftables search doesn't return it, but the Resources search does"): an item whose entity
  setup drops the "Item" from its name (AnimalBaitFeederItem has AnimalBaitFeeder_ES, and its Ridgeline copy) was not
  seen as an item, so it was in no list. It is now listed under its category, becomes an Item craftable, and the
  gallery's search ranks its matches in the same order as the Assets page.
- **Phantom fridges: Clean saved items removes what a removed item spawner left in a world, without a wipe**
  (defolt_0999: "It continues to exist in the world even after I delete it"; O Piqueno). A fridge, stove or other fixed
  item placed with Add object, a prefab or a paste is a world item spawner, and the game saves the item it spawned in
  the save (SCUM.db), so deleting or undoing the spawner left the item until a wipe. New on the Map: **Clean saved
  items**. Stop the server (or quit the game), choose its SCUM.db, Find: it lists the saved items of every item
  spawner this project placed and no longer has (undone, reverted
  or deleted, from the whole history) and of the game's spawners it deleted; optionally also the same items around the
  object selected on the Map. Untick what stays, Remove ticked: a backup `SCUM.db.studio-backup-<date>` is made next to
  it first, and Restore backup puts it back. Players, inventories, containers, vehicles, bases and items of any other
  spawner are never touched; it refuses while the game or the server has the save open. Placing or deleting a spawner,
  the Delete list and the export (message and report) point to it. A pak alone still cannot do this: the game's spawner
  has no "spawn once" or "do not save" setting (only the item and its chance to be usable).
- **Copied textures keep valid offsets**: a texture written in place of another (Replacements: snow on the ground, a
  ground texture for a ground texture) or copied with a clone kept the original's absolute file offsets of its small
  mips and its platform data, wrong as soon as the new name made the header longer or shorter (FORMAT_NOTES: "never
  rename-clone" a texture). Every copied texture now gets the offsets of its own header.
- **Apply paint is one step, and keeps working after Undo and Redo** (audit): applying a car with five painted parts made
  about twenty journal entries, each reading every paint again, and one Ctrl+Z took back one value; it is now one entry.
  A Redo while new paint was being chosen left the panel on the old values, so Apply was refused ("edit is out of
  date"); the panel now takes the applied values and keeps the paint being chosen. Picking an armour kit twice quickly
  could end on the first one; the last kit chosen now wins.
- **Flying over the island no longer stalls while new places load** (owner: "flying inland over dense forest stutters
  badly", his log: "read 9172 ms (6337 MB allocated), prepare 16090 ms (14391 MB)"): reading a place read every
  package its objects borrow from (a building's parts, a material's parent) again for each object, so the military
  base alone made 9.2 GB of garbage in 10 s, and the collector then held up the view. Each package is now read once
  per place: the base reads in 0.3 s with 0.4 GB, to the same result. On a 4 km flight at Ultra from the B_4 outpost
  to A_3 (real game files, RTX 5080), the work per streaming step fell from 1.6 GB to 0.6 GB on average and from
  13.9 GB to 3.7 GB at worst, and frames over 100 ms during the flight from 15 to 7. Drawing a frame no longer makes
  garbage (54 KB → about 0.1 KB a frame), the overlay is drawn once instead of once per scene, a click or the brush
  looks only at what is under the cursor, a place streaming in rebuilds only what it touches (20,000 of 100,000
  objects in a test instead of all), and textures read only the sizes they keep.
- **The island no longer freezes the app when it appears** (owner: "heavy lag, very low FPS"): the island's terrain was
  sent to the graphics card in one frame, about 0.9 s for an island-sized test with far models; it now goes over many
  frames, a few milliseconds each (4.4–7.3 ms at most on the test PC), while the view keeps drawing, and an island
  that only gains far models sends just those. Moving the camera also gets the short loading budget from its very first
  frame (the first frame of a move used to spend the 30 ms meant for a camera at rest).
- **Lighter ground textures on Performance and Balanced, and a quiet view at rest**: every quality baked the ground at
  1024 × 1024 per landscape piece (4 MB each); Performance now bakes 256 and Balanced 512. A still view with the spawn
  layers switched off no longer redraws 25 times a second; the glint runs only while a spawn model is shown.
- **AMD graphics cards: undefined drawing state found and fixed** (Pheidian's crashes, D03). The 3D views were checked
  with the driver in its strict checking mode, which reports what one graphics card lets through and another may crash
  on. Every frame without sun shadows (Performance quality, lighting off, the 3D previews in studio light, tile
  pictures) asked the shadow sampler to read a unit with no depth texture on it, which OpenGL calls undefined behaviour;
  it now reads a small "no shadow" texture made for it. Every texture slot a shader reads now holds a real texture in
  every pass, the pick pass no longer dithers its object numbers and clears its whole buffer. A flight from the B_4
  outpost into the A_4 airfield on the real game files (4 km, 40,000 objects streaming in and out, anti-aliasing and
  shadows on) now ends with no error or warning from the driver. Only an AMD card can confirm the crashes are gone.
- **AMD graphics cards: each 3D frame is finished before the window copies it** (D03). The window's own renderer, running
  on another thread, copied the 3D view's picture as soon as the studio had sent its drawing commands, not once the card
  had carried them out; OpenGL only promises a finished picture after the card has finished. NVIDIA cards run the two in
  order anyway; AMD cards need not, and the copy could read a picture still being drawn, which is likeliest when areas
  stream in and frames are heaviest. The Map and the 3D previews now wait for their frame to finish before handing it
  over (measured on an RTX 5080: a heavy test frame handed over after 1.6 ms with 11 ms of drawing still to do, now after
  12.6 ms, complete). Each streaming step also writes to the log how many meshes and textures the studio holds on the
  graphics card; a flight there and back holds as much at its end as at its start. For AMD testers who still crash:
  starting the studio with the environment variable `SCUMSTUDIO_UI_THREAD_COMPOSITOR=1` makes the window draw on the same
  thread as the 3D views, so all of the studio's graphics run on one thread (a test switch, off by default; the log says
  when it is on).

## [3.2.0] - 2026-10-12

### New in this release
- **3D previews closer to the game** (owner: "any 3D asset you show me in the program I want at the ORIGINAL resolution …
  the REAL colours"): every 3D view is now drawn at twice its size and filtered down, so edges are smooth and leaves and
  fences shimmer less, and the Assets preview has an Expand button that makes it as tall as the details panel and the
  panel wider. A Sun switch shows the model in the island's sun at 13:30, with its sky and its shadow on a floor, as it
  stands in the game (the soft studio light stays the default, every side readable). A Scale switch, on by default, puts
  a 1.8 m person beside anything from 50 cm up and lays a 1 m grid under it (10 cm or 1 cm cells under small items), and
  the Assets preview reads out the size as length × width × height; its line of parts and triangles is now in the app's
  language. The Vehicles and Weapons 3D tab loads the textures at full resolution, as the Assets page does (it stopped at
  1024 px); paint still works on them. A model shows at once with small copies of its textures and the full-size ones
  land over the next frames (a vehicle's ten materials at 4096 px are about 430 MB, which took 120 ms in one frame), and
  while the colour picker is dragged the paint is baked at half size, then whole once it rests.
- **3D previews like in the game** (owner: "any 3D asset you show me … the ORIGINAL textures, the REAL colours"): foliage
  types (ScotsPine_FT, European_White_Birch_01_FT and the other trees, bushes and grass) now show in the 3D view and get a
  tile picture; Blueprints draw their components' override materials, a child Blueprint's changed parts, child actors and
  every instance of an instanced component; vehicle parts such as BPC_WolfsWagen_Door_FrontLeft find their mesh; glass
  and water are see-through as on the Map; SpeedTree colour textures and the master materials' own colours are used; a
  craftable without a model shows its inventory icon. Tile pictures are made again once.
- **Smoother trees and grass** (owner: "no shimmering/noise in the trees"): the 3D view now draws with 4 samples a pixel.
  Leaves, grass and fences are cut out by the share of the pixel they cover instead of all or nothing, so far trees no
  longer sparkle while the camera moves (about 40 % less frame-to-frame flicker on a test forest) and keep the density
  they have up close (a far card used to fill up to 84 % where its leaves cover 35 %); every edge of the scene is
  smoothed too. Measured at 1080p on an RTX 5080 (median frame): a forest of 20 000 leaf cards 0.94 ms → 1.00 ms, a
  grid of 4 096 boxes 0.29 ms → 0.35 ms; the extra buffers take about 66 MB of graphics memory at 1080p.
  Settings › *Anti-aliased foliage* (on) turns it off for the previous look.
- **Delete takes everything that belongs to the object** (owner: "when I delete it, everything of it must be deleted:
  the fire object itself, its sound, its particles"; Hektor before him: "if you delete a camp, fire animations remain
  behind"): every delete in the Map (the Delete key or button, a part, a Ctrl+click or brush selection, Ctrl+A /
  Ctrl+Shift+A, Delete all of a mesh or class) also deletes the object's fire, light, smoke, sound and decal at its spot
  and the actors attached to it. Attached and child actors go at once; anything else is first listed by the 3D view,
  highlighted, with what it is, why it goes, how far and in which level: **Delete all (N)** (Enter) or **Only this
  object**, Esc deletes nothing. A sound or light nearer to another object is greyed as belonging to it and stays unless
  ticked. Deleting a part of a kept building (its fireplace) also deletes that part's own fire, light and sound. It is
  one edit: one Ctrl+Z brings everything back. Shift+Delete deletes the object alone, and Settings › *Delete with
  everything attached* (on by default) switches it off. The AI's `delete_actors` does the same by default (below).
  - What a running server already saved stays there: items a deleted spawner already spawned and fires players lit. The
    list says so; they go only when the game removes them.
  - A fire or light component with another part of the building hanging under it (a damage box under the flames) stays,
    so that part keeps its place; the message after the delete names it. Nothing it hangs on ever stops the delete.
  - Projects with a deleted part of a building (the new `deleteComponents` edit) cannot be opened by older studios.
  - Not yet seen in game: the export side (inert components, attached actors following their parent) is proven by
    reading the written packages back; a fire deleted this way still needs a look on a local server.
- **The map shows what the game will have**: a deleted building's stored child actors and the mesh-less lights and
  sounds attached to a deleted object are hidden (the export takes them along), and a lake's or river's mesh-less sounds
  and effects in the shared water level now show over the loaded area, so they can be picked and deleted with their
  water.
- **AI delete takes what belongs to the object** (owner: "If I delete a fire place, the fire stays burning in game ... When
  I delete it, EVERYTHING of it must be deleted"): the MCP tools `delete_actors` and `delete_all_of_kind` now also delete
  the fire, light, smoke, sound and decal actors at the object's spot and the actors attached to it, in the same edit, so
  one undo brings everything back. A sound or light nearer to another object stays with it and is listed as kept, as
  is a trade post, spawner or volume hung on the object; a building's stored child actors still leave with it in the
  export, so restoring the building brings them back. The answer also says that what a running server already saved
  (spawned items, fires players lit) stays there.
  `companions: false` deletes only the named actors, as before.
- **Level dump shows effects and volumes**: `level dump --json` names what each particle, Niagara, Wwise, sound and decal
  component plays (`assetPath`), the box of trigger shapes and volume brushes (`localBounds`) and the volumes players swim
  in (`waterVolume`). A child actor's Wwise sound that stores no place of its own now stands at its fire instead of at
  the world's origin.
- **A clear square behind the icon under the pointer** (owner: "a clear square behind the icon when I hover"): icon and
  toolbar buttons (title strip, status bar, page headers, the Map toolbar) show a raised square with a hairline frame
  and a bright glyph on hover, and a soft accent while pressed; nothing moves when the pointer comes and goes. A
  greyed-out button stays flat and dim, and its hover text still shows, so it says why it is off.
- **"Shows in" on the Craftables page** (owner: "every craftable gets a 'Shows in' category … sofas/chairs/tables
  default to Furniture, houses to Building"): each craftable picks the section of the game's crafting menu its
  recipe appears in, from the sections the game's own recipes use (read from the game files, named in plain words),
  and a "Made in" badge says whether it is built (placed in the world with resources, like base building) or crafted
  into the inventory. Without a choice a sofa goes where the game's sofas are, a building (a mesh of the game's
  buildings) where the game's walls are, and everything else where its template recipe is. Only the game's sections
  exist: a new section name would need the game's tag list and menu, which a mod cannot add, and a section of the
  other kind is refused at export with a warning.
- **Choose what the Map header shows** (owner: "show only what I want"): every item of the Map header (Maximize, Pop
  out, Quality, Time, Spawns, Snap, Frame, Shape, Paste, Add object, Export, the status chips …) has a switch, in the
  settings button at the end of the header and in Settings › Map toolbar. Both stay in step and the choice is kept for
  the next start; Show all and Defaults put it back (the default hides only Pop out). The header makes room for what is
  left and nothing overlaps from 1280×720 to 2560×1440.
- **No Drone mode any more** (owner: "flying is simply how the 3D view works"): the Drone button is gone. Hold the right
  mouse button to look around and fly with W/A/S/D (Q/E down and up, Shift faster); the wheel sets the speed while you
  hold it and zooms otherwise. Tab still keeps looking without holding (Esc lets go), and its hint now reads in the
  right order in Arabic.
- **Map panels lined up**: the World tree, the 3D view and Properties start on one line; the entity list and History
  share one row that the splitter moves together and end with the World tree; the four panel headers are one height.
- **Quality names translated**: the quality pickers of the Map header and Settings show Performance, Balanced, High and
  Ultra in the UI language (they showed the English names in every language), and the Properties panel's x, y, z /
  pitch, yaw, roll hints follow the language too.
- **Load the whole island or only one sector** (owner: "load only this sector"): the Map no longer opens the whole island
  by itself; it asks. *Whole island* streams the levels around the camera as you fly, as before. *Only this sector*
  loads one cell (the one you flew into or last picked in the World tree, named beside the switch, or any picked in
  the list) completely in one phase, with a progress bar and Cancel, and then loads nothing else while you fly inside
  it; *Load more* streams the rest of the island again. The header switches between the two at any time.
- **Share your mod: the package and Get** (owner: "Share your mod … ONLY the project's own edits … NEVER game assets or
  paks"): a shared mod is a small zip of the project's own edits for the chosen sectors (or the whole map), its
  craftables and the economy sections of its traders, with a picture. Nothing else can go in: no game file, no pak, no
  imported 3D model, no folder of your PC; a package that holds one, names a file outside itself, is too large or
  unpacks to more than it says is refused. What you built and deleted again stays out, and a copy of an object you
  added in another sector brings that sector along. Get applies the mod to your project as one step (Ctrl+Z takes it
  back): an object, a copied tree or a trader whose name your project already uses is renamed, edits that clash with
  yours (you moved the same building) are reported before anything is written, and a different game build is pointed
  out. Remove takes the mod back again, its craftables and economy sections included.
- **Share your mod: the gallery connection and the moderator key** (owner: "Share your mod", "upload your work"): the
  app talks to the mod gallery (a small Cloudflare Worker the owner runs) to upload a package of up to 3 MiB, read the
  gallery, fetch a mod's picture and download a mod. A download stops at its size limit whatever the server claims, and
  no gallery address, no network or a refusal comes back as a reason the page can show, never as a crash. The app
  carries no secret (the report webhook was the lesson): uploading needs none, and removing a mod needs the moderator
  key, a file only on the owner's PC that `scumstudio modshare keygen` creates. The Worker keeps only its public half;
  a removal is signed, is refused five minutes later and cannot be sent twice.
- **Share your mod** (owner: "Share your mod", "upload your work"): a Share button next to Discord and Support opens a
  card with Share, Gallery and Added to this project. You type a name once (kept on this PC, no account; names may
  repeat, and Remove my name forgets it), tick the sectors to share (each with its number of edits, or Whole map), and
  when the project has them its island-wide edits, vehicle and weapon edits, craftables and the economy of those
  sectors' traders. Only the project's own edits travel, in a small package: never game files or paks, and craftables
  made from an imported 3D model stay on your PC. A name, a description of up to 600 characters and a picture go
  along: Take a picture in the Map steps the card aside, you fly the camera to the spot and press Capture for that
  view drawn again at 1080 p (up to 2560 wide, without the grid), or Use an image file. The Gallery shows each mod's
  picture, maker, description, sectors, size and date; Get downloads it (in memory, nothing is left on disk) and first
  shows what it adds per sector, which objects get a new name because yours use theirs, a different game build, or a
  clash with your own edits (then nothing is added). Add to my project adds it as one step: Ctrl+Z takes its edits
  back, and Remove under Added to this project takes all of it back, craftables and economy too. The Moderate tab
  appears only when the owner's key file is on the PC.
- **Share your mod: the gallery's hosting** (owner: "Share your mod", "upload your work"): `tools/modshare-worker` is
  the small Cloudflare Worker (free tier: one R2 bucket, one KV namespace) that keeps the shared mods, with a README that
  walks the owner through deploying it. It takes only the project's own edits: a package of up to 3 MiB whose files are
  the manifest, the edits, craftables, economy and two JPEG pictures, nothing else; a game package or pak inside, bytes
  hidden between the files or a zip bomb are refused before anything is stored. Five uploads per address an hour and
  300 a day; the gallery lists the newest first. Removing a mod needs a signature from the moderator key on the owner's
  PC; the Worker holds only the public half, refuses a signature older than five minutes and one it has already seen.

### Fixes
- **No more flickering trees and bushes near a placed bridge** (Discord, defolt_0999: "flickering trees and bushes near
  the bridge"): "Clear grass under it" also removed the bushes under a placed object, and to do so rewrote whole foliage
  components of the landscape square (every bush, acacia, olive, fig, aloe or cactus of that kind in it, without the
  game's ready-made draw data). In the game the trees and bushes of that square blinked as you walked. The option now
  clears only the landscape grass and the spawns under the object; the bushes and trees there stay exactly as the game
  has them, and the Map no longer hides them.
- **Far trees switch to their lighter models where the game does** (owner: "graphics like the game"): a cluster of
  spread-out trees measured its distance from the nearest corner of a box that could span kilometres, so far trees were
  drawn at full detail (and shimmered) while a turned object switched too late; each copy now uses its own size and
  distance.
- **Dusk and dawn without light from below**: between 21:00 and 21:13 and between 05:47 and 06:00 the sun kept lighting
  the island from under the horizon and then jumped to the moon; the light now fades out and hands over smoothly.
- **No freeze when the quality is raised, graphics memory freed, no crash after a paste or a bend**: raising the
  quality re-sent every texture in one frame (now over the next frames while the old ones stay on screen); popping the
  3D view out and docking it back kept the old textures in graphics memory; and a texture shared with a pasted or bent
  copy could be freed under it ("disposed texture" error).
- **Clicks and the brush go through the gaps between leaves**: a click between the leaves of a tree selected the tree,
  and the brush planted on its invisible card; now only the leaves themselves are hit.
- **Distant trees cast their shade**: far from the camera a canopy was drawn but cast no shadow (its leaves were cut
  by a different rule in the shadow pass).
- **No light line across leaf cards; two-sided surfaces lit by UE's rule**: leaves lit from one side showed a
  dark/bright line that slid across them as the camera moved. Every surface seen from both sides now takes its light
  as UE does: the front of each triangle with its normal, the back with the normal turned around (mirrored objects
  keep their front). Grass whose normals point up is lit on its front and darker from behind, as UE draws a two-sided
  material, and the rims of rounded objects no longer flip at the outline.
- **Wwise sounds are orange effect pins on the map** (owner: "If I delete a fire place, the fire stays burning in game:
  the flames, the heat, the smoke, the fire light and the sound, without the object"): a mesh-less `AkAmbientSound` (or
  any actor with an `Ak*` or `*Sound*` class) is now an orange effect pin like the fire's lights and emitters (it was a
  grey marker) and shows and hides with the Effects pins. Behind it, a companions finder says what belongs to an
  object being deleted: its stored child actors and the actors attached to it, the mesh-less lights, emitters, sounds,
  fire, smoke and decals at its spot, the fire, light and sound components of a deleted building part, a lake's other
  side, and the volumes around it (off by default, except a lake's swim volume, which goes with the lake when the
  deleted water covers most of it; a post-process volume is never offered). A trade post, spawner, sentry spawner or
  volume hung on the deleted object is offered unticked with a note, never taken by default. A kept object nearer to
  an effect keeps it, a campfire's sound by a deleted lake stays unless ticked, an effect hanging
  from a kept lamp post stays with the post, and a kept house round a fireplace never keeps the fireplace's fire.
- **A fire Blueprint whose last drawn part is deleted goes whole**: a hidden collision box, a helper or an editor-only
  mesh the map never draws no longer keeps it standing, so its flames, light and heat go with it instead of the part
  only shrinking to nothing. An editor-grid part the placement repaints is drawn, so it still keeps its building.
- **Ctrl+Shift+A, Delete on the whole island**: a level the reader cannot take no longer loses the whole delete with
  "Looking for ..." left in the status line; that level is left out and logged, the rest is deleted.
- **A deleted fireplace takes its fire along in the export** (owner: "If I delete a fire place, the fire stays burning
  in game: the flames, the heat, the smoke, the fire light and the sound, without the object"): a part of a kept
  building can now be deleted for real (its fire, light and sound components become inert in the pak, one Ctrl+Z brings
  them back), and one delete of an object and what belongs to it is a single undo step. An object whose every part the
  map draws is gone now leaves whole even when it also has hidden, helper or editor-grid meshes, or instances beside its
  parts (a burning barrel or tyre stack kept burning). A fire, light or sound actor hung on a deleted object goes with
  it in the export, even from a project made before this. What the studio did not delete with it (an object with a mesh
  of its own left unticked or deleted alone with Shift+Delete, a lamp on a shelf, a bridge's fence on a bent deck, and
  any trader, spawner or volume, which the export never deletes on its own) is written where it stood, a mesh socket's
  offset and scale included, instead of round the world's origin; so is a bent piece hung on a socket. The export report names any fire, light, sound or decal still standing at a
  deleted object, and says plainly that items a deleted spawner already gave out stay in running worlds (only the game
  removes those). Not yet seen in game: the written levels are proven by reading them back; a fire deleted this way, and
  a detached lamp's place, still need a look on a local server.
- **A deleted sentry spawner leaves its zone's list**: its guarded zone manager no longer lists an empty spawner (and a
  copied spawner no longer joins behind one). Read back from the written level; not yet seen on a server.
- **The brush stops when you let go** (owner: "it plants randomly, a huge number of trees very fast"): pressing the right
  or middle button during a stroke, another control or window taking the mouse, Alt+Tab or another app in front no
  longer leave the stroke running, which kept planting on every hover and every timer tick with no button held. The
  stroke now ends once, after its last dab, and stays one Ctrl+Z. The brush circle lies over the ground, slopes and
  hills included, instead of a flat disc cut by them.
- **The brush plants cleanly** (owner: "with a short interval it plants at random, a huge number of trees very fast, in
  some places and not in others, sometimes on half the road"): the spacing is now measured at each tree's foot on the
  ground, not at the height under the cursor, so a cursor resting on a canopy no longer stacks trees on top of each
  other. A sweep spreads the palette evenly inside the circle; the timer plants exactly one object per interval at the
  free spot nearest the centre and says when the circle is full. Nothing is planted on roads, tracks, paths, runways,
  yards, water or rocks: the landscape's paint layers (EraseFoliage included), the road pieces, runway and concrete
  meshes, lakes, rivers and the sea level are checked; pebbles and debris under 50 cm no longer leave a spot out. Trees
  and bushes planted as objects keep others off their trunk instead of their whole crown, and trees pass over
  undergrowth (keeping 1 m from a bush's heart), so empty ground between them gets planted. The spacing also holds up
  and down steep slopes at wide spacings. In a level opened without its landscape a cursor resting on a canopy or a
  roof plants at that tree's or house's foot, not up in the crown, and no two plants come closer than the spacing at
  any height. Hovering marks every spot a click plants (green) and leaves out (red); a circle too large to mark them
  all marks an even share and the bar says how many a click plants. The brush bar shows
  how many were planted and left out, and the notification says why ("12 on roads or paths, 3 in water"). The new "On
  objects too" option also plants on roofs and floors. One Ctrl+Z still undoes a whole stroke.
- **The update no longer fails on a held .old file, and keeps your files** (Discord, seviscache: "Access to the path
  …\ScumStudio.App.exe.old is denied", also as admin; restarting the app fixed it): Windows cannot delete a program
  that still runs, and an older copy kept running from the `.old` file of the previous update (a second window, or
  Claude Desktop's connection `scumstudio.exe mcp --connect`), so the update stopped there. Such a file is now left
  alone and the backup gets a name of its own. The new version is first unpacked and checked beside the app (each file
  complete, the right version) and only then swapped in by renames, so closing the app midway never leaves a broken exe.
  The next start deletes only the backups the update made, in the app's own folder, where every start used to delete
  every `.old` file below it, yours included. A download that receives nothing for 60 seconds stops with a message, and
  Cancel stops it at any time. The AI connection and then the project are closed before the new version starts, so it
  reopens the project at once; if the new version cannot start, the card says so with a Close now button instead of
  offering to install again. This takes effect from the next update: the update to this version is still installed by
  the old version's own updater, so if it shows "Access to the path … .old is denied", close the other window and
  Claude Desktop (or restart the app, as seviscache did) and update again.
- **One app window, and a project open elsewhere says so** (Discord, zarkos6175: "Two copies of the app open → project
  not saved"): the second copy could not read the project the first one had open, showed a raw "used by another
  process" error and then refused every edit, and on Linux nothing stopped two windows from writing the same journal.
  Starting ScumStudio while it is already open now brings the open window to the front instead of opening a second
  one, also while it downloads or installs an update. A project is locked while it is open (a hidden `.lock` file in its folder, held only by the open window, so a
  crash leaves no stale lock), and another window or a `scumstudio` command that edits it is told the project is open
  elsewhere ("This project is open in another SCUM Modding Studio window") while the window keeps the project it had
  open. Opening the project that is already open in this window keeps it as it is, and `scumstudio project export`
  only reads the project, so it works while the app has it open; an AI that asks `scumstudio mcp` to open the project
  it already has keeps working on it instead of being refused by its own lock. After an update the new version waits
  for the old one to close, and closing the app lets go of the project first.
- **A failed write can no longer break the project's history**: when the disk filled up or a USB or network folder
  dropped while an edit, undo or redo was being saved, the failed record stayed behind under a number the next edit
  reused, and the project no longer opened (or, after closing, the edit that had failed came back). A failed write is
  now cut off again before the error is shown, and a project an older version damaged this way opens again: the failed
  records are dropped with a warning and the original journal is kept as `journal.jsonl.bak`.
- **A click while levels stream in no longer edits another object** (owner: "a mod made with the studio must never
  change or remove anything the game or the players already have"): while the next levels were being sent to the
  graphics card, a click, Delete or drag on a level that was going away could select, delete or move an unrelated
  object of the level that came in its place. A new level never takes the number of a level still on screen, and a
  click or drag on a level that is going away does nothing (the object snaps back).
- **Vehicles and Weapons keep their value names on a small window**: from 1024 to 1280 px the value names got no room
  and the item's name vanished behind the spawn command; the list and the clone column now give way first. Every value
  box says what to type, with the reason first when the game cannot read the value.
- **Shell texts in every language**: the console counters ("3 warn", "1 err") and the search field's Ctrl K chip were
  English in every language (German now reads Strg+K, also in the shell's hover texts).
- **Craftables page**: the kinds and materials (list chips and pickers) are named in the app's language instead of
  their English code names, and the list has the same header strip as the other panels.
- The Map's loading line could end on a stale "Preparing …" step instead of the load's result: the progress reports ran
  on the thread pool and a late one overwrote the final status. They now arrive on the UI thread, and reports of a load
  that was replaced or has finished are dropped.
- **Your own exported paks are no longer read as game files** (Qraox: "it also completely changed all the built items on
  my server"; jenni_1129: "after pak is not work in game crash"): an earlier export copied into `Content\Paks`, as the
  Build page says, was read as part of the game, so every craftable it shipped was left out of the next export as
  "already exists in the game files" and its registry came back as the game's. The open project's paks, its Craftables
  pak and every `pakchunk9xx-*_P.pak` are now left out when the game files are opened, by the app, the server export, the
  command line and the MCP server; the status bar names the paks it left out. An export under a mod name of its own
  leaves out that name's paks from the client's game files as well as the server's, so both cooks see the same game.
  Other mods' paks are still read.
- **Names ending in a number are written as the game stores them**: the engine reads `ConcreteBlock002_1` as the name
  `ConcreteBlock002` with the number 2, the rule behind the checkpoint-46 crash, but clones, craftables, added imports,
  material parameters and new DataTable rows wrote the whole text with no number. So an item craftable made from a bow
  kept the game's recipe name (`CR_Improvised_Bow_20`) inside its new package, a furniture craftable on a numbered mesh
  pointed at a mesh name the game does not have, the numbered parts of a weapon family (`SK_Winchester_1897`) kept the
  game's names inside the copy, and an AK-47 given AK-15 magazines imported their mount under another name than the
  magazines use. All of them now use the game's rule, and so do the names a copied package's own properties hold (a
  name, a soft path, an enum value). Where the data cannot be read (a Blueprint's compiled code), a name whose number
  would change is refused with its name, and a name without a number follows the rename whole, as before, with a
  warning in the export report.
- **Clone names that end in `_` and a number are refused** (`Hunter_2` is the name `Hunter` with a number to the game):
  `Hunter2` or `Hunter_v2` work; a numbered part of a family gets `_v<number>` (`SK_Winchester_v1897`). When a new name
  already exists, the message now says which pak has it.
- **Craftables keep their asset names for life** (Qraox: "it also completely changed all the built items on my server"):
  a craftable's asset name (`CR_SS_…`, `BP_SS_…`, the paths the game saves placed objects by) was made from its name each
  time, so renaming a craftable gave it new packages and what players had built from it was gone. The asset name is now
  kept in `craftables.json` from the first time the project is opened, a rename keeps it, and the page shows it under the
  name box ("Asset name: SS_Chair"). New asset names never end in '_' and a number, which the game reads as a different
  name (Shelf 1 becomes `SS_Shelf_v1`, the second 'Couch 01' `SS_Couch_01_v2`, a second import of a model `SS_Table_v2`),
  and names in Arabic, Chinese, with punctuation or differing only in case get asset names of their own: 'كرسي 01' and
  'طاولة 01' no longer share one, which left one of them out of the export. A row that would still be left out (a
  hand-edited file) says so inline and names the craftable that has its asset name.
- **An export never drops the craftables players already have** (Qraox, the same report): when a craftable's new package
  already exists in what the studio reads as the game (an earlier Craftables pak copied into the Paks folder, or another
  mod with the same path), the craftable was left out as "already exists in the game files", so the new Craftables pak
  no longer had it. The export now stops with an error that names that pak. The same goes for a piece of furniture, a
  station or a generator whose mesh is not in the game files, or only in another mod's pak, and for one whose every
  ingredient is: the error says what to change instead of quietly removing what players built. An item recipe, which
  removes nothing players own, is still left out with a warning.
- **Craftables only use what every player has** (found while looking into jenni_1129's crash, "when i add new item in
  crafting meau open game it will crush"; the crash itself is not confirmed fixed yet): a mesh or ingredient tag that
  only another mod's pak has is no longer shipped as an object or recipe that players without that pak cannot load: an
  ingredient like that is left out of the recipe with a warning naming that pak, and a mesh stops the export as above.
  One from a mod imported into the project is kept and ships with it in the main pak. A DLC mesh is now pointed at by
  the path the game knows it by (`/<Pack>/…`), and a station, generator or piece of furniture whose template shows no
  mesh says that it keeps the stock look.
- **A mod only adds** (Qraox: "it also completely changed all the built items on my server"; jenni_1129: "after pak is
  not work in game crash"): the registry a pak ships (`AssetRegistry.bin`, the game reads only the highest pak's) is now
  always the game's own one with every record as it is, plus the records of what the pak adds. It was the registry the
  studio happened to read: another installed mod's, an imported mod's copied as it was, or the studio's own from an
  earlier export, so stock items and other mods' items could vanish from the game. An imported mod's new records are kept
  for the packages that ship with it, two imported mods' registries no longer replace each other, and a craftable or
  clone exported again gets today's record instead of the old one. The game's own records are never rewritten, and game
  files without the game's registry (only another mod's) register nothing, with a warning, instead of shipping that one.
- **The Craftables pak holds only new packages**: a Power craftable (a copy of the generator) no longer ships the game's
  trader table, and the export stops, naming each path, before it would ship anything the game already has. An imported
  model whose folder in craftables.json points outside its own `imports/<name>` folder is left out with a warning.
- **One clone that cannot be built no longer stops the export**: it is left out with a warning and the other vehicles,
  items and craftables are still built; a part of a family that cannot be read is left out without the others pointing
  at it; a recipe no longer asks for a station that could not be built (that ingredient is left out, and a recipe that
  asked for nothing else is left out with its craftable); numbered names (`Weapon_Deagle_357`, `CR_Improvised_Bow_20`)
  are registered like the game stores them.
- **Imported mods**: a mod import that stopped half-way is no longer read over the game or carried into the pak.

## [3.1.0] - 2026-10-09

### New in this release
- **Import 3D model on the Craftables page** (owner: "a list where I can add external files, for example 3D tables, or a
  new weapon added as an external mod"): pick an FBX or OBJ file and the installed Unreal Engine 4.27 imports it as one
  mesh (pivot at the bottom centre, walk-in collision, textures up to 2048), cooks it for SCUM and adds it as a
  craftable; the Craftables pak carries the mesh, its materials and textures. The first import compiles shaders and
  takes several minutes; Cancel closes the engine. glTF is not taken yet (convert it to FBX).
- **Time of day in the Map**: a Time slider in the Map toolbar moves the sun across the sky on SCUM's clock (sunrise
  06:00, sunset 21:00): golden light and an orange sky at dawn and dusk, shadows, haze and the water's reflections
  following along. The night is the game's own (owner: "the night exactly like the game's"): its moon (0.5 lux, the
  game's blue-white tint), its night light (0.05 lux), its auto exposure limits and its lift of dark tones, so a moonlit
  night shows the ground at about a tenth of the day, as in game. It is kept for the next start; 13:30 is the island's
  usual look.
- **Ground closer to the game**: up close the ground now uses the relief of its paint layers (pebbles, needles, ruts catch
  the light), sharper layer textures, less visible tile repeats, and the detail reaches 300 m instead of 160 m.
- **Plant on a timer** (owner: "a timer: every so many seconds it adds a tree where the brush is"): Brush › Plant has a
  Timer switch and an interval (0.05–3 s). While you hold the button, one object is planted every interval at a free
  spot inside the circle, moving or not; the spacing still applies and the stroke is one Ctrl+Z.
- **Lighter 3D view switches** (Discord feature request, separate from the quality presets): Settings has three new
  switches. *Spawn places as 3D models*: off shows the simple marker shapes again, which stand still, so the view stops
  redrawing while nothing moves (the models glint and kept it drawing 25 times a second). *Animated sea*: off keeps the
  water still. *Lighting*: off draws every surface in its own colour without sun, shade or the shadow pass.
- **NPCs instead of zombies** (igor8802: "assign NPCs instead of zombies"): in Spawns › Zombies the groups of a threat
  zone now say what they spawn (Zombies, Armed NPCs, Animals, Drones) instead of "Group 1", "Group 2". Most zones pick
  between a zombie group and an armed-NPC group (drifters, guards; a village is 75 / 25): set Zombies to 0 and the zone
  spawns armed NPCs instead. The hover text names the game's encounter class.
- **Any item of the game as a craftable** (owner: "every item inside the game we can add … so you can craft something in
  exchange for some things"): the Craftables search (and Make craftable on the Assets page) now offers the game's items
  too, weapons, ammunition, tools, food, clothes and DLC items, with their 3D picture. An item gets a new item recipe
  with your ingredients that makes the game's own item, in the crafting menu section of its kind (firearms, melee,
  ammunition, clothes, tools …). An item of another mod works once that mod is imported into the project.
- **Craftables: Add from the game** (owner: "I want every item in the game shown in 3D like the Assets page, so you can
  craft it for a recipe... and the list is empty, why?"): the list only holds what you made craftable, and nothing showed
  what can be added. The Craftables page now shows a gallery whenever nothing is selected (and on its "Add from the game"
  tab): every world object (In the world) and every inventory item (Can pick up: weapons, tools, food, clothes, ammo,
  vehicle parts) in the Assets page's categories and 3D pictures, a search, and the imported mods, DLC packs and newer
  game files the catalogue does not know in a group of their own. Click a picture to make it craftable (its recipe
  opens); ones already added are marked and open their recipe.

### Fixes
- **Craftables**: removing every craftable (or unticking the Craftables mod) now also deletes the old Craftables pak from
  the export folder and the server mods folder, whose old registry hid the project pak's new vehicles and items; a
  mistyped ingredient is left out of the recipe with a warning instead of naming a tag that does not exist; two stations
  with the same name no longer stop the export; names in Arabic or other scripts no longer all become the same asset
  name.
- **Craftables review**: buildings, rivers and the player with an `_ES` twin no longer become broken item craftables;
  spears, the chainsaw, the tomahawk and charms get their own crafting menu section; an emptied craftables-only project
  exports again (it removes the old pak); Unreal Engine stops when the app closes or the import is cancelled.
- **Doors of a copied building open and close every time** (owner: "sometimes they work, sometimes not"): copying a
  building together with its doors (a multi-selection, a brush sweep, select all of a kind) also copied each door, so
  two doors stood on the same spot; the game tells doors apart by where they stand. The building's copy brings its own
  doors, and doors whose building is copied in the same set are no longer copied a second time.
- **Replace shows the new model at once** (Discord: "the model does not change visually when replaced"): a building's
  part or a mesh actor replaced in place kept its old model in the 3D view, although the edit was recorded and exported.
  The view now draws the new mesh right away, and Ctrl+Z / Ctrl+Y switch it back and forth.
- **Copied sentry robots spawn** (igor8802: "When duplicating robots, the robots do not appear"): the game spawns a sentry
  only from a spawner its level's guarded zone manager lists, and a copy was listed nowhere. A duplicated or pasted
  sentry spawner now joins that list; pasted into a level without sentries it brings a manager of its own.
- **Nothing spawns inside what you build** (owner: "the rocks I build: no NPC or zombie may spawn inside them"): the
  "Clear grass under it" option of a placed object (on by default for added objects) is now "Clear grass and spawns
  under it": the exported mod also removes the game's vehicle and zombie/NPC spawn points that stand inside the object
  (inside it, not under it: the points on a road under a bridge stay). The game builds its walking map for zombies and
  NPCs while it runs, so a placed object with collision is already walked around. Removing the object brings both back.
- **No more "Function does not accept floating point Not-a-Number values" errors** (Discord, seviscache: 175 of them
  after moving and duplicating objects to the far side of the map): dragging an arrow while looking almost along it
  flung the object tens of kilometres away, and from there every mouse move failed. The drag now holds still until the
  mouse turns back, the view ignores a point it cannot place on screen, and no edit can put an object at an impossible
  place (not a number, or more than 100 km out), so such a value never reaches the project or the mod.
- **The sun and moon no longer jump when you turn the view** (Discord, seviscache: "the sun attempts to reposition itself,
  creating the effect of jumping"): the sky worked out each pixel's direction through the camera's whole view, including
  its position kilometres out on the island and the 30 km view distance, and the rounding moved the sun's disc by up to
  0.15° as the view turned (the disc is about 1° wide). It now uses the camera's turn alone: 0.00002°.
- **Hangars and buildings in their real colours** (owner: "the textures are very bad, white; give me the real colour"):
  the Map and the 3D previews drew SCUM's building textures as they are, while the game darkens or tints each one
  (its material's brightness and tint). The airfield hangars' near-white roof panels are drawn at a fifth of their
  brightness in game: they are now grey metal like the game's own far view of them, and roofs, trims and floors elsewhere
  (the C_3 church's red roof, for one) no longer come out too light.

## [3.0.0] - 2026-10-09

### New in this release
- **Water that looks like water**: the sea, lakes and rivers are clear turquoise in the shallows, deep blue where they are
  deep and have a light foam line along the shore; long swells, the sky's reflection and a sun glitter stay visible from
  kilometres up.
- **Drop to the surface from the toolbar** (owner: "a cone floating in the air: one click and it sits on what is under
  it"): the toolbar shows "Fit to ground" while something is selected, and the End key does the same, as in Unreal. The
  selection is set down on whatever is right under it (ground, road, roof, wall, a vehicle) without sinking in.
- **Sky, haze, sea and sun shadows in the Map view**: a blue sky with the sun behind the island instead of a flat colour,
  distant hills fading blue-grey into the horizon (thinner haze higher up), a sea with small moving waves, the sky's
  reflection and a sun glint, warmer daylight with tone mapping, and soft sun shadows of trees, buildings and hills within
  150 m of the camera (off on the Performance quality). `scumstudio render level` draws the same (`--no-shadows` to skip).
- **Materials closer to the game**: buildings, rocks, vehicles and props now use their normal maps (bricks, planks,
  corrugated roofs and rock faces catch the light like in game) and a sun highlight set by the material's roughness.
  Textures stay block-compressed on the graphics card as the game ships them, so they take a quarter of the memory:
  Ultra now loads 2048-pixel textures (was 1024) and the Assets page's 3D preview shows every texture at its full size.
  `scumstudio render mesh` renders an asset as the Assets page shows it (every material, normal maps).
- **Clear grass under it** (a Discord user: "I placed a bridge and the meadow grass grows through it"): a checkbox in the
  Map's properties for a placed or moved object, also for a multi-selection. On, the exported mod removes the landscape
  grass and the bushes and small plants standing under the object's outline (a bent road or bridge piece by its bent
  shape), so nothing pokes through. It is on for every new object (added, pasted, copied, extended, replaced, painted,
  prefabs) and off for moved game objects until you tick it; Settings has the default for new objects. The 3D view hides
  the bushes that will go. The game spaces its grass samples 1.5 m apart, so the grass thins out up to about 1.5 m
  around the edge, and a bush goes whole when its foot is under the object.
- **Vehicle parts**: the Vehicles page has a "Default parts" entry for each spawn preset of a vehicle (admin spawn, world
  spawns, traders; a clone gets its own). Every slot shows the part it spawns with: take a part away (No part), or put
  another part that fits in the slot, e.g. heavy or light armour on a door. The cube button shows the part highlighted
  on the car in the 3D view. Changes go into the project journal (undo/redo) and into the exported mod.
- **Weapon attachments**: the Weapons page has an "Attachments" entry: each socket of the weapon (rail, magazine,
  muzzle, light, ghillie, charm …) with a tick for every mount type of its kind. Tick one to let the weapon take those
  attachments or magazines (e.g. AK-15 magazines in the AK-47), untick to take one away (e.g. the bayonet).
- **The brush's options sit right under the toolbar** while the brush is on, in a bar at the top of the 3D view: Select or
  Plant, the circle's size, and for planting the palette and the spacing. Choosing Plant with an empty palette opens the
  palette at once.
- **Copy, Paste and Delete buttons on a multi-selection**: the selection chip says "27 objects selected" and has Copy,
  Paste, Delete and ✕ next to it instead of a long line of key hints.
- **Gizmo orientation like Blender**: Global (the world's axes), Local (the object's own, best for objects already turned)
  or View (the screen's right, up and towards you). A picker sits in the Map toolbar while something is selected, the
  comma key cycles it, and Settings keeps the default. It replaces the Local axes switch.
- **Planting spacing is a random range** (1–3 m by default): each tree, bush or rock keeps its own distance between the two
  values from its neighbours, so a planted patch looks grown rather than gridded. Older palettes keep their one spacing.
- **Loot editor** (Map, properties panel): every searchable object (lockers, safes, crates, cabinets, wardrobes, a
  building's searchable parts) shows a **Lootable** box (off = decoration only: nothing to search, no loot) and a
  collapsible list of its loot presets (`Examine_*`) with Remove buttons and a box to add another of the game's presets;
  the groups and items of the presets are listed under "What can spawn here". Every change is one journal step (undo/redo)
  and the export writes it into the component's `ExamineAssetData`, the way the game reads it.
- **Craftables page and the Craftables mod**: any static mesh or Blueprint that does not move on its own (furniture,
  walls, a house, a tree, a pole, a pump …) becomes a craftable that players build and place like the game's own
  furniture. Add it from the page's search or with **Make craftable** on the Assets page; each entry has a 3D picture,
  its name in game, a recipe suggested from its material and size (scaled from the game's own base-building recipes,
  editable: add, remove, change tags and amounts, tool or material), a station it needs nearby, and for power objects
  the power radius, output and fuel use. Kept in the project as `craftables.json` with Undo.
- **Export mod** builds them as their own pak, `pakchunk<N+1>-<Name>Craftables_P.pak`, with a copied stock `.sig`
  (Projects page: "Also build the Craftables mod"; CLI `project export`, `--no-craftables` to skip). Each craftable is a
  renamed copy of the game's own crafting family (improvised chair recipe and base element; the improvised workbench for
  a station, whose item carries the station's own ingredient tag; the electricity generator for power) with the chosen
  mesh, registered in `AssetRegistry.bin` like the game's recipes.
- Not in the game's data, so kept but reported at export: "station needs electricity" and solar "only by day".

### Fixes
- **The Map camera no longer flies on while you are in another app** (owner: "I pressed W, came back and it was flying
  by itself, as if someone controlled my PC"): the view read the whole keyboard, so W held in a game or a chat in front
  kept it flying when the switch never reached its window. With another app in front it now lets go of every key,
  mouse-look and drone mode.
- **Fit to ground no longer sinks cars into the ground or drops things through a bent bridge** (owner: "some cars go
  into the ground, some things go through the bridge I built"): a bridge or road piece you bent holds things up where
  it is drawn bent, and something whose corners stand on different things (one on a deck, one on the ground far below)
  stands level on the highest of them instead of tipping over (a van came out rolled 68 degrees, half underground).
- **The sea no longer turns into blue ground** when the levels around the camera finish loading in whole-island mode: the
  detailed levels painted their seabed over the island's sea. The detailed scene now brings the one sea of the view.
- **No more "Cannot access a disposed object" and freezes on Ultra** (owner: "the app froze, it got too heavy"): a sharper
  copy of a texture now takes over the old one in place, so nothing is left drawing a deleted texture; and Ultra loads
  1024-pixel textures again (2048 took 24-32 GB to read and prepare around the B_4 outpost and filled the memory).
  Textures and normal maps stay compressed on the graphics card, so Ultra still uses less memory than before.
- **A selected object is always lit orange** (owner: "sometimes it shows no orange, only its numbers"): a copy re-added,
  a piece re-bent or an instance given its own node after the selection was lit came back unlit; the Map lights the
  selection again whenever the 3D view builds new objects.
- `scumstudio render level --island` no longer shows flat brown and green patches of the coarse island terrain through
  the detailed ground: each landscape tile is drawn once, as the Map view and the game do.
- **Sharper textures after raising the 3D quality**: the Map kept drawing the smaller copies of textures it had already
  loaded until the level was opened again; now the sharper copy replaces them at once.
- **Added or copied roads are no longer pink in game** (owner: "the road I copied is purple"): the game draws its road
  pieces into the ground (the landscape's virtual texture), and its gravel pieces only there, never by themselves; on
  their own their material shows a placeholder pink. Added, copied and bent gravel and asphalt road pieces are now
  written the way the game places them (gravel: only into the ground, no shadow; asphalt: into the ground), so the ground
  shows them. Rocks, bridges and buildings draw as before. Export the project again to fix roads already placed.
- **Traders are easy to find**: the Assets page opens with a Traders category (the game's armorer, general goods,
  mechanic, doctor, barber, bartender, harbourmaster, banker; searching "trader" or "shop" shows it), and "Place in map"
  on one places a whole working trader of that type (trade post, NPC, outpost, economy section) where the Map's camera
  aims. The Economy page has a "Place a trader" menu that does the same, and every exported EconomyOverride.json comes
  with "EconomyOverride - where it goes.txt" (server and single player folders, in English and Arabic).
- **A copied building pastes anywhere on the island**: a multi-selection (a building with everything in it) is copied as
  it is, so flying to the other end of the island and pasting places all of it, laid out as before, after a short load
  of its own models. Copies go into the loaded level nearest to where the camera aims (the game streams them with that
  area; a far level showed them only when it loaded), and a paste that cannot make anything says so.
- **Every road and bridge piece bends**: a bridge deck or fill piece of any shape (not only long, narrow ones) now takes
  the Shape tools: curve it left or right, raise or lower its middle, or lay it in an S like a road.
- **Snap joins pieces moved with the gizmo too**: dragging a bridge, road or wall piece by its arrows or squares now joins
  the same piece end to end when its end reaches the other's (green box), as a free drag did; elsewhere the arrows still
  move smoothly. A stretched piece runs along its stretched side.
- **Duplicate makes the copy exactly in place**, selected, and for two seconds a click or drag on that spot takes the copy,
  not the original under it, so it can be pulled off straight away (it used to appear 2 m off along the world's X).
- **The camera no longer flies on by itself**: a W/A/S/D press whose release went to another control (a toast, a menu, the
  entity list) kept the camera moving, and a right button released over another window left mouse-look on so the cursor
  jumped about. The view now checks every frame which keys and buttons are really held and lets go of the rest.
- **Ctrl+C on a multi-selection works every time**: after a brush sweep no single object was "the" selected one, so Copy
  stayed off; and Delete, Ctrl+C and Ctrl+V now work while the keyboard focus is anywhere in the window, not only inside
  the map. Every copy in the studio empties the system clipboard, so Ctrl+V pastes it and not an older Assets path.
- Prefab file names are made safe by Windows' rules on every system (the public build on Linux let "a:b" through).
- Island spawn places read their turn as a unit quaternion: the game stores some as not-quite-unit, and copies and moves of
  tilted zombie places are now proven to keep their turn exactly (16 of 28 places around the A_3 farm stand tilted in the
  game's own data; the Global gizmo orientation keeps the gizmo level on them).
- **Copied lockers, safes and crates were only shapes** (Discord: "the duplicated police locker appears open", "nobody can
  loot the copies"): a click on a locker in part mode took its mesh, so Duplicate and Copy/Paste made a plain mesh
  without its lock or its loot. A container (`ItemContainer` class) is now picked and copied whole, and a searchable part
  copied out of a building (a closet, a file cabinet) carries its loot presets onto the new mesh. Copies keep loot edits.
- **One of two crates moved together floated in game** (Discord, police station): the upper crate pile is attached to
  the lower one, so the game moves it with its parent; the editor also moved it by the same amount relative to the old
  place of the parent, and in game it moved twice. A group move now leaves attached members to their moved parent, a
  move of an attached object is relative to where its parent stands now, and the Map draws objects attached to a moved
  object where the game will put them.
- A copy made inside one level now also gets a fresh `_gameUniqueId` (as copies from other levels already did).

### Changed
- The title strip shows the workspace tabs as icons below 1640 px (nine tabs now).

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
