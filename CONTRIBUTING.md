# Contributing to ScumStudio

Thanks for helping. A few rules keep the project safe to share and easy to review.

## Ground rules

- **No game data in git.** Never commit `*.pak`, `*.sig`, `*.utoc`, `*.ucas`, `*.uasset`, `*.uexp`, `*.ubulk`, `*.umap`,
  extracted textures or meshes. `.gitignore` blocks the usual ones; check `git status` before you commit.
- **Never share the AES key.** Not in code, tests, logs, issues or screenshots. The app keeps it DPAPI-protected per
  user; the CLI reads it from there or from `SCUMSTUDIO_AES_KEY`.
- **Files only.** ScumStudio reads game files and writes mod paks. No reading or writing of the running game's memory,
  no patching of `SCUM.exe` / `SCUMServer.exe`, no forged pak signatures, no speed or movement hacks. Pull requests that
  cross this line are closed.
- **Pinned packages.** Versions live in `Directory.Packages.props`; do not bump them in a feature pull request.
- **Credit third-party code** in `THIRD-PARTY-NOTICES.md`.

## Building and testing

```
scripts\build.ps1 -Test
```

Tests that need game data skip by themselves. If you have the data, point `SCUM_MAP_SLICE` at an extracted A_0 map slice
and `SCUM_FIXTURES` at a mod build folder to run them too. Headless UI tests run everywhere (Avalonia headless); GL tests
need an OpenGL 4.3 context and skip without one.

## Code style

- Match the code around you: file-scoped namespaces, XML doc comments on public members, `.editorconfig` settings.
- The build has 0 warnings; keep it that way.
- Every user-facing string goes through `Loc` with a key in all four `src/ScumStudio.App/Localization/Strings/*.json`
  files (English is the reference), and every interactive control has a `ToolTip`. Tests check both.
- Level edits are `EditOp` records with an exact `Inverse()`; add the JSON discriminator, `EditState` validation and
  apply, the exporter path, and a round-trip test.

## Pull requests

1. One topic per pull request, with a short description of what changed and how you tested it.
2. Add or update tests for logic changes.
3. For UI changes, attach a before/after screenshot (headless screenshots: set `SCUMSTUDIO_SCREENSHOTS=<folder>` and run
   the app tests).
4. Update `CHANGELOG.md` under *Unreleased*.

## Reporting bugs

Use the issue templates. Include the app version (status bar), what you did, what you expected, and the log
(Console button in the status bar). Remove anything personal, and never the key.
