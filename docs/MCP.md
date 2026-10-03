# AI control — ScumStudio as an MCP server

ScumStudio speaks the **Model Context Protocol (MCP)**, so an AI assistant (Claude Code, Claude Desktop or any other
MCP client) can drive it: open the game files, show map levels in the 3D viewport, delete / move / duplicate / copy
actors, tune and clone vehicles and weapons, export the mod pak, and look at the result through screenshots.

Every change goes through the **project journal**, exactly like a change made with the mouse: it shows up in History,
the pages refresh at once, and you can undo it (Ctrl+Z, the History panel, or the AI's `undo` tool).

## Three ways to connect

| Setup | When | How the AI connects |
|---|---|---|
| **A. Desktop app + Claude Code** | you work in the app and want the AI to act in it | Streamable HTTP to `http://127.0.0.1:47130/mcp` with a bearer token |
| **B. Desktop app + Claude Desktop** | same, from Claude Desktop (stdio-only config) | Claude Desktop starts `scumstudio mcp --connect …`, a stdio ↔ HTTP bridge to the running app |
| **C. Headless (no app window)** | automation, a server, quick scripted edits | the client starts `scumstudio mcp --source <Paks folder>` on stdio |

### A. Desktop app + Claude Code

1. ScumStudio → **Settings → AI control (MCP server)** → switch it **On**. The card shows
   `Listening on http://127.0.0.1:47130/mcp`, and a random access token is created on first use.
2. Click **Copy Claude Code command** and run the copied line in a terminal:
   ```
   claude mcp add --transport http scumstudio http://127.0.0.1:47130/mcp --header "Authorization: Bearer <token>"
   ```
3. Start Claude Code and ask, for example: *"Use scumstudio: show A_0_Outpost_Exterior, delete every concrete road
   barricade there, and show me a screenshot."* The **AI** pill in the top bar counts the calls. Each change also shows
   as an "AI: …" notification.

### B. Desktop app + Claude Desktop

1. Turn AI control on (as in A), then click **Copy Claude Desktop config**.
2. Claude Desktop → Settings → Developer → **Edit config**. Merge the copied block into `claude_desktop_config.json`:
   ```json
   {
     "mcpServers": {
       "scumstudio": {
         "command": "C:\\Tools\\ScumStudio\\scumstudio.exe",
         "args": ["mcp", "--connect", "http://127.0.0.1:47130/mcp"],
         "env": { "SCUMSTUDIO_MCP_TOKEN": "<token>" }
       }
     }
   }
   ```
3. Restart Claude Desktop. The bridge only forwards messages, and the token travels in its environment (not on its
   command line). When the app is closed or AI control is off, the bridge answers each request with
   *"ScumStudio is not reachable … turn on Settings → AI control"* instead of failing silently.

### C. Headless: `scumstudio mcp`

```
scumstudio mcp --source "D:\SteamLibrary\steamapps\common\SCUM\SCUM\Content\Paks" [--server-source <server Paks>] [--project My.ssproj]
```

- `--source` can also be an extracted folder (one that contains `SCUM/Content`) or a single `.pak`.
  `SCUMSTUDIO_SOURCE` / `SCUMSTUDIO_SERVER_SOURCE` work too.
- Encrypted game paks need the AES key in the **environment** of the process that starts the server
  (`SCUMSTUDIO_AES_KEY`). The key is never a tool argument or a tool result. Prefer setup A or B: the app keeps the key in
  its protected store.
- The headless server has every tool except the UI ones. It adds `render_levels`, an off-screen 3D render of
  levels/cells (with the project's deletions and moves applied) returned as an image.
- `scumstudio mcp --list-tools` prints the table below.

Example for Claude Code with an extracted folder:
`claude mcp add scumstudio -- scumstudio mcp --source "D:\map_slice\client"`

## Teach the AI: the ScumStudio skill

The server already sends its short `instructions` on connect. For Claude Code there is also a full **skill**
(`src/ScumStudio.Mcp/Skill/SKILL.md`: workflow, units, how to verify every edit with a screenshot, the owner's rules).
Install it once per user with **Settings → AI control → Install Claude skill**, or `scumstudio mcp --install-skill`.
It lands in `~/.claude/skills/scumstudio/SKILL.md`; every new Claude Code session then knows how to drive ScumStudio.

## What the AI should do (the server's `instructions`)

1. `get_status`. 2. `open_source` if no game files are open. 3. `create_project` / `open_project`: edits need a project.
4. Map: `list_levels` → `list_actors` / `get_actor` → `delete_actors`, `delete_all_of_kind`, `move_actor`,
   `duplicate_actor`, `add_static_mesh`, `copy_actor_to_level`, `restore_actors`. In the app also call `show_levels` and
   `screenshot` to look at the result.
5. Vehicles/weapons: `list_items` → `get_item_values` → `set_item_values`; `clone_item` (new name + in-game caption)
   / `remove_item_clone`.
6. `export_mod` builds `pakchunk900-<Name>_P.pak` (+ `.sig`, report, optional server pak) for you to copy into
   `SCUM\Content\Paks`.

Units are centimetres. The axes are X forward, Y right, Z up. Rotations are `[pitch, yaw, roll]` in degrees.

## Tools

| Tool | Where | Kind | What it does |
|---|---|---|---|
| `get_status` | app + CLI | read | What is open right now: game files (source, package count, The_Island sublevels per kind), the project (name, folder, applied/undoable edits, levels and vehicle/item packages waiting for export) and, in the desktop app, the page, the levels shown in the 3D viewport, the selected actor and the camera. Call this first. |
| `open_source` | app + CLI | files | Opens the game files: the game's SCUM\Content\Paks folder (uses the AES key the user configured; the key is never exchanged here), a single .pak, or an extracted folder containing SCUM/Content. Without 'path' the configured game Paks folder is used. |
| `create_project` | app + CLI | files | Creates a project folder '<folder>/<name>.ssproj' (project.json + journal.jsonl) and opens it. Every edit is recorded in its journal; the project is what export_mod builds the mod from. |
| `open_project` | app + CLI | files | Opens an existing project (.ssproj folder or its project.json). Its journal is replayed: undo/redo continue where the user left off. |
| `get_history` | app + CLI | read | The project's edit history, newest first: sequence number, time, applied/undone, summary and target. |
| `undo` | app + CLI | edit (undoable) | Undoes the last edit(s) of the project (persisted in the journal; redo brings them back). |
| `redo` | app + CLI | edit (undoable) | Redoes the last undone edit(s). |
| `export_mod` | app + CLI | files | Builds the mod from the project's applied edits: '<outputFolder>/Client/pakchunk<N>-<Name>_P.pak' (+ .sig copied from a stock pak when available, + export-report.md) with the rewritten levels, changed/cloned vehicle and item packages and the merged AssetRegistry.bin; with includeServer also the server pak from the server's Paks folder. The user copies the pak and .sig into SCUM\Content\Paks. |
| `list_levels` | app + CLI | read | Lists The_Island's sublevels (streaming levels): name, kind (Poi = places/buildings, Landscape = terrain tiles, TvBase, Pripyat, …), map cell (A_0 … D_4, Z_0 … Z_4) and package path. Filter by cell, kind and name text. |
| `list_actors` | app + CLI | read | Lists the actors of one level with their state in the project: name, class, kind, mesh, world location/rotation, instance count (foliage/rocks), and whether the project deleted, moved or added it. Filter by words (name, class or mesh) and kind. |
| `get_actor` | app + CLI | read | Details of one actor: class path, kind, mesh, world and root-relative transform (with the project's override), components (class, mesh, instance count) and its state in the project. |
| `delete_actors` | app + CLI | edit (destructive, undoable) | Deletes actors from a level (journaled, undoable). Deleting a building also removes the child actors it spawned when exported. |
| `restore_actors` | app + CLI | edit (undoable) | Restores actors the project deleted (journaled). |
| `delete_all_of_kind` | app + CLI | edit (destructive, undoable) | Deletes every actor of the same kind in one or more levels or a whole cell, as one undoable edit: by static mesh (StaticMeshActors, mesh roots and — unless includeInstances is false — every foliage/rock instance drawing it) or by class (e.g. a Blueprint). Give the mesh/class path in 'value' or name an example actor in 'likeActor'. |
| `move_actor` | app + CLI | edit (undoable) | Sets the transform of an actor's root (journaled): absolute location / rotation / scale, and/or an offset added to the current location. Units cm, rotation [pitch, yaw, roll] degrees. |
| `duplicate_actor` | app + CLI | edit (undoable) | Copies an actor inside its level (journaled): a new actor with the same class, mesh and stored child actors at 'location' or shifted by 'offset' (default 200 cm along +X). |
| `add_static_mesh` | app + CLI | edit (undoable) | Places a new StaticMeshActor with the given mesh in a level (journaled). Find meshes with search_assets (className StaticMesh). |
| `copy_actor_to_level` | app + CLI | edit (undoable) | Copies an actor (a building Blueprint with its child actors, or a mesh actor) from one level into another level at a new place (journaled). Within the same level this is a duplicate. |
| `level_references` | app + CLI | read | Follows every package reference of the given levels or cell (meshes, Blueprints, materials, textures, built data) and reports how many are present in the open game files and which folders are missing (useful for extracted folders). |
| `list_items` | app + CLI | read | Lists vehicles (BPC_*), weapons (Weapon_*), magazines, ammunition (Cal_*) and projectiles (BP_WeaponBullet_*) of the game files plus the project's clones, with their entity setup (_ES: in-game name, weight, inventory size). |
| `get_item_values` | app + CLI | read | The stored values of a vehicle/weapon/magazine/ammo/projectile package or its entity setup (_ES) or a vehicle attachment: key (export\|path, what set_item_values takes), name, type, stock value, current value in the project, enum choices. Cooked Blueprints store only values that differ from their parent class. |
| `set_item_values` | app + CLI | edit (undoable) | Changes stored values of a vehicle/weapon package (journaled, undoable): each entry is a key from get_item_values and the new value as text (numbers with '.', true/false, an enum choice like EWeaponCategory::Rifles, "x, y, z" for vectors, plain text for captions). |
| `clone_item` | app + CLI | edit (undoable) | Clones a stock vehicle or item under a new name (journaled): items copy the item and its _ES; vehicles copy the whole family (entity setup, item container, anim/physics assets, curves, mount slots, attachments, manual spawn presets) while meshes and textures stay shared. The clone is registered in AssetRegistry.bin at export, so #SpawnItem <newName> or #SpawnVehicle BPC_<newName> finds it. 'caption' sets its in-game name. |
| `remove_item_clone` | app + CLI | edit (destructive, undoable) | Removes a clone made by clone_item (its edited values are reset first; journaled). |
| `search_assets` | app + CLI | read | Searches the game files by package path words (e.g. 'barrel', 'SM_Rock', 'Outpost wall'); optionally only one class (StaticMesh, SkeletalMesh, Texture2D, Material, MaterialInstanceConstant, BlueprintGeneratedClass, World …). |
| `get_asset` | app + CLI | read | Exports (objects) of one package with their classes. |
| `navigate` | app | view (UI only) | Shows a page of the ScumStudio window. |
| `show_levels` | app | view (UI only) | Loads levels into the Map page's 3D viewport (the user sees them; edits of the project are shown live). Give level names or a cell (a whole cell loads its POI sublevels and terrain). |
| `select_actor` | app | view (UI only) | Selects an actor in the 3D viewport and entity list (its level must be shown, see show_levels) and frames the camera on it. |
| `set_camera` | app | view (UI only) | Moves the 3D viewport camera: a location and/or a point to look at (UE cm), or yaw/pitch in degrees; frameAll frames the whole shown scene. |
| `screenshot` | app | read | Returns a PNG of what the user sees: 'viewport' = the 3D map view (levels shown with show_levels), 'window' = the whole ScumStudio window (pages, lists, values). Use it to check your work. |
| `show_item` | app | view (UI only) | Opens a vehicle or weapon (stock or clone) on the Vehicles/Weapons page so the user sees its values. |
| `render_levels` | CLI | read | Renders levels (or a whole cell) off-screen to a PNG with the project's deletions and moves applied (added actors are not drawn here; the desktop app's screenshot shows everything). Camera: yaw/pitch around the scene centre in degrees. |

"view (UI only)" tools change only what the window shows: the page, the levels in the viewport, the selection and the
camera. Edits are journaled and can be undone. "files" tools read or write folders the AI names (game files, project
folders, export output).

## Security

- The app's server listens on **127.0.0.1 only** and requires the **bearer token** (Settings → AI control → Copy /
  New token; a new token disconnects clients that use the old one). The `Host` header must name localhost, and a request
  that carries an `Origin` header is refused unless the origin is a localhost page. This protects against DNS rebinding and
  against requests made by websites.
- The token is stored in `settings.json` in your user data folder. Treat it like a password for the editor.
- The **AES key never passes through MCP**: no tool takes or returns it. The app mounts paks with its protected key; the
  headless server reads `SCUMSTUDIO_AES_KEY` from its own environment.
- Tools that write files (`create_project`, `export_mod`) write where the AI asks, like any tool you run. Review the
  folder names in the notifications and in History.
- The rules of the project still hold: no speed/movement hacks, `SCUM.exe` / `SCUMServer.exe` are never patched, and
  pak signatures are only copied from stock `.sig` files.

## Protocol details

- MCP revisions `2025-11-25`, `2025-06-18`, `2025-03-26` and `2024-11-05` (negotiated in `initialize`). Supports `tools/list`,
  `tools/call` (text, image and `structuredContent` results, `isError` for tool failures), `ping`,
  `notifications/cancelled`, JSON-RPC batches, and empty `resources/*` and `prompts/*` lists. Every tool carries
  annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`).
- Streamable HTTP: `POST /mcp` answers with JSON (or `202` for notifications). `Mcp-Session-Id` is assigned on
  `initialize`; `DELETE /mcp` ends the session; `GET` is `405` (no server-initiated stream).
- Tool calls run one at a time. A UI action waits for the window (e.g. `screenshot` waits until the viewport has drawn the
  current scene).
- App start-up options for automation: `ScumStudio.App --mcp-port <port> [--mcp-token <token>]` runs the server for that
  session only (the setting stays as it is), together with `--source`, `--load`, `--screenshot` and `--exit-after`.

## Verified

- Unit and UI tests: `tests/ScumStudio.Tests/Mcp` (protocol, stdio framing, HTTP sessions/token/Origin/CORS, bridge)
  and `tests/ScumStudio.Tests/App/AppMcpTests.cs`. The second covers the Settings card, AI edits reaching the Weapons page
  with undo, and the map page driven through MCP with a window screenshot.
- Interoperability with the official **MCP Python SDK 2.2.0**: over stdio (headless CLI, `render_levels`), and over
  Streamable HTTP against the real app under Xvfb with OpenGL. In that run the client called `show_levels`, `set_camera`
  and viewport and window `screenshot`s. It selected, lifted and deleted the outpost saloon, then undid both edits.
