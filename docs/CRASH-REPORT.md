# The game or the server crashed with a ScumStudio mod: send us this

A crash is fixed fastest from the game's own crash files. Please send **the newest crash folder** and **the log**, and
say what you did just before (left the server, pressed Alt+F4, closed from the menu, joined, walked somewhere).

## The game (your PC)

1. Press **Win + R**, paste `%LOCALAPPDATA%\SCUM\Saved` and press Enter.
2. Open **Crashes**, sort by **Date modified** and take the **newest folder** (named like `UE4CC-Windows-...`). Zip the whole folder:
   it holds `CrashContext.runtime-xml`, `SCUM.log` and `UE4Minidump.dmp`.
3. Also take **Logs\SCUM.log** (the last session) and, if the game was started again since, **Logs\SCUM-backup-*.log**
   with the time of the crash.

## The dedicated server

1. Open the server's folder (where `SCUMServer.exe` is), then `SCUM\Saved`.
2. Zip the **newest folder in Crashes** and take **Logs\SCUM.log** (or the `SCUM-backup-*.log` from the time of the crash).

## Also send

- The export report of your mod: `export-report.md` in the export folder's `Client` (and `Server`) folder.
- Which paks are installed: the names of every `pakchunk...` file you added to the game's `Paks` folder (and the server's).

The files only contain the game's own log and crash data (no passwords). Post them in the ScumStudio Discord bug channel.
