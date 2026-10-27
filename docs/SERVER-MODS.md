# Lua mods for the dedicated server

A server mod is a Lua script that runs **inside** the SCUM dedicated server and reacts to the game: a player joins, leaves or
dies, a timer fires.

It runs through **UE4SS** (github.com/UE4SS-RE/RE-UE4SS), the community's Lua framework for Unreal games. SCUM Modding Studio
installs it as files next to `SCUMServer.exe`; it never starts or stops the server. UE4SS puts code into the server process, so use it
on **your own server**, and start that server with BattlEye off (`-nobattleye`), as a UE4SS server needs.

## In the app

Open the **Server** page (it uses the server's Paks folder from Settings).

1. **Install UE4SS**: downloads the official release from GitHub, checks its SHA-256 against the one GitHub published, and installs
   it next to `SCUMServer.exe` with the settings a server needs. Existing settings and mods are kept.
2. Pick a ready mod and press **Add mod**: *Announcements every few minutes*, *Message when a player joins*, *Event log (joins,
   leaves, deaths)*, *Game time on request (!time)*, *Class and function list of the game*.
3. Change the lines under `SETTINGS` in the editor and **Save**.
4. Start the server. A **new** mod loads when the server starts; saving a mod that is **already running** reloads it at once.
   **UE4SS log** shows what the mods print and why one did not load.

**Cloud server** opens a guide window (in every UI language) for a rented server where the app cannot reach the files: what to
download, where to upload it, the settings to copy, BattlEye off, adding mods and checking the log.

**Switch UE4SS off** parks the proxy DLL so the server starts with no Lua mods at all (stop the server first).

## Command line

```
scumstudio server status --server "D:\SteamLibrary\steamapps\common\SCUM Server\SCUM\Content\Paks"
scumstudio server install-ue4ss          # or --zip UE4SS.zip
scumstudio server templates
scumstudio server add Announcer          # --name, --file my.lua
scumstudio server mods | enable | disable | remove <name>
scumstudio server ue4ss on|off
scumstudio server log --lines 40
```

`--server` takes the server's Paks folder, `SCUM\Binaries\Win64` or the install folder (or set `SCUMSTUDIO_SERVER_SOURCE`).

## The Lua editor

**Server > Lua editor** opens a window of its own for writing a mod yourself. Left: the **catalog** (functions, events, ready pieces and
rules, each marked tried on a real server / exists but not tried / does not work or crashes, with a description and an example) and the
**game functions** from `ue4ss\scum_dump.txt`. Middle: the script. **Check** finds syntax errors, game functions that are not in the class
dump and mistakes that crashed or froze a server before; **To my server** checks the script and writes `Mods\<Name>\Scripts\main.lua`.
The same is available to an AI client as the MCP tools `lua_catalog`, `lua_game_functions`, `check_lua` and `write_lua_mod`.

## What a mod can call

The ready mods use functions found in a running SCUM Server (class and function list of 2026-10-25) and called there without error:

| Function | Use |
|---|---|
| `/Script/SCUM.ConZGameMode:SendHUDMessageToAll(Message, beep)` | a message on every player's screen |
| `/Script/SCUM.ConZGameMode:KickPlayer(Player, kickReason)` | kick a player |
| `/Script/SCUM.PlayerRpcChannel:SurvivalStats_Server_HandlePlayerLogin / ...Logout (PlayerController)` | hook: a player joins or leaves |
| `/Script/SCUM.PlayerRpcChannel:Prisoner_Server_Die(Prisoner)` | hook: a player dies |
| `/Script/SCUM.PlayerRpcChannel:Chat_Server_BroadcastChatMessage(Message, Channel)` | hook: chat |
| `/Script/SCUM.ConZPlayerController:Teleport(request)` | teleport |
| `/Script/SCUM.ConZPlayerController:SendHUDMessageToClient(Message, AudioEvent)` | a message on one player's screen (pass `nil` as the sound) |
| `/Script/SCUM.WorldStatics:GetTimeOfDay(WorldContext)` | the game time of day in hours (20.85 = 20:51); call it on `StaticFindObject("/Script/SCUM.Default__WorldStatics")` with the game mode as the context |

The game object to call them on is `FindFirstOf("BP_ConZGameMode_C")`. The **Class and function list** mod writes every
class and function the server has loaded (about 3,400 native classes, 5,100 functions) to `ue4ss\scum_dump.txt`; search it for more.
UE4SS's own Lua API: docs.ue4ss.com/lua-api.

## Good to know

- The server needs `bForceGUObjectArrayForIteration = true` in `ue4ss\UE4SS-settings.ini` (the installer sets it). Without it the
  game thread hung in the tests.
- With UE4SS's stock `[Hooks]` the server crashes as soon as a player joins (`EXCEPTION_ACCESS_VIOLATION`, `SCUMServer.exe+0x4295f3f`
  on SCUM 1.3.4.2). The installer turns every hook off except `HookEngineTick` and `HookUObjectProcessEvent`, the two that
  `ExecuteInGameThread` and `RegisterHook` need. The same finding is in the Takaro SCUM connector (github.com/gettakaro/connectors,
  `games/scum`). `scumstudio server apply-settings` writes the settings again.
- A chat line that starts with `#` is the game's own admin command (it arrives in `Chat_Server_ProcessAdminCommand`, not in the chat hook), so a chat command of a mod should start with `!`. In a chat hook the sender is `channel:GetOuter()`, the player's controller (read it a moment later, not inside the hook).
- A hook callback must not read the player: a native call on a player that is not ready crashes the server. The ready mods only
  look at the player list a few seconds after a join.
- An error in a mod is printed to `ue4ss\UE4SS.log`; a mod that hangs the game thread hangs the server. Test on a spare world first.
- A game update can rename or change a function a mod hooks; the **Class and function list** shows what is there now.
- The server refuses to start when a pak in `Paks\~mods` has a signature that does not match it (`Pak file or matching sig
  file integrity compromised`). This is independent of UE4SS.
