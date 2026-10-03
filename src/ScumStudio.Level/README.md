# ScumStudio.Level

The editable world model for The_Island: which packages make up the map, what is in a sublevel, and the journal of
edits a project makes to it. Nothing here rewrites packages yet (that is the exporter, stage 1); edits are pure data
replayed onto pristine client and server packages at export time.

| Type | Purpose |
| --- | --- |
| `World.WorldIndex` | Every package under `SCUM/Content/ConZ_Files/Maps/The_Island/**` classified as Persistent, Poi, Landscape, TvBase, Pripyat, BuiltData, Hlod or Misc, with the map cell (`A_0` .. `D_4`, `Z_0` .. `Z_4`) parsed from the name. Works from a file list alone; `TryCrossCheck` compares with `The_Island.umap`'s StreamingLevels when it is available. |
| `World.WorldNameParser`, `World.MapCell` | The naming rules (grid `A_0_*`, `Landscape_A_0_1b`, `TV_Base_B_2_*`, compact `B3_*`, trailing `*_D_2`, `*_BuiltData`, `HLOD/*_0_HLOD`, `AbandonedCity_PripyatLike/`). |
| `Reading.ILevelReader` | Parser-independent access to a level: `LevelData` = export table + actor/component properties. |
| `Reading.Cue4ParseLevelReader` | CUE4Parse 1.2.2 implementation over an `AssetCatalog`: `UWorld.PersistentLevel`, `ULevel.Actors`, tagged `RelativeLocation`/`RelativeRotation`/`RelativeScale3D`/`bAbsolute*`/`AttachParent`/`StaticMesh`/`RootComponent`, `UInstancedStaticMeshComponent.PerInstanceSMData`, and `ULevelStreaming.WorldAsset`/`LevelTransform`. Values a cooked Blueprint instance does not store are looked up on its template (SCS `*_GEN_VARIABLE` / CDO). Blueprint actors are completed from their classes (see below). |
| `Model.LevelDocument` | Actor graph: `ActorRecord` (kind StaticMeshActor / Blueprint / Light / Volume / Other, root, components, mesh, world transform, ISM instances in world space) built by composing `AttachParent` chains (`World = Relative * ParentWorld`, honouring `bAbsolute*`). `ToJson()` dumps it. |
| `Editing.EditOp` (+ `DeleteActorOp`, `DeleteAllOfKindOp`, `DuplicateActorOp`, `SetTransformOp`, `SetInstanceTransformOp`, `DeleteInstanceOp`, `AddStaticMeshActorOp`, `AddBlueprintActorOp`, their restore/remove counterparts) | Edits keyed by (level package, actor name); each has an exact `Inverse()`. `EditOpFactory` builds them from a `LevelDocument` (old values, bulk targets, unique names). |
| `Editing.EditState` | Net effect of the applied edits (deleted actors/instances, transform overrides, added actors) with validation. |
| `Editing.Journal` | Append-only `journal.jsonl` with undo/redo records; recomputes the applied and redo stacks on open. |
| `Projects.Project` | A `.ssproj` folder: `project.json` (name, game build, created/modified, sources), `journal.jsonl`, `notes.md`; Apply / Undo / Redo, History, pending export set. |

## Blueprint actors

A cooked UE 4.27 level stores every component a placed Blueprint instance owns (only its delta properties; the template
is the Blueprint's `<Variable>_GEN_VARIABLE`) and every child actor a `ChildActorComponent` spawned, as a separate level
actor `<Component>_GEN_VARIABLE_<ChildClass>_CAT_<N>` (`ChildActor` on the component, `ParentComponent` on the actor;
both exposed as `ComponentRecord.ChildActor` / `ActorRecord.ParentComponent`). `Cue4ParseLevelReader` additionally
walks the Blueprint class chain (class export `SuperIndex`), each class's `SimpleConstructionScript` (`RootNodes` →
`SCS_Node.ChildNodes`, `InternalVariableName`, `ComponentTemplate`, `ParentComponentOrVariableName`) and
`InheritableComponentHandler` overrides, and **synthesizes** whatever the level does not store
(`ExpandBlueprintComponents`), plus the components of child actors the level does not store (`ExpandChildActors`,
flattened as `ChildActorComponent/Component`, depth ≤ `MaxChildActorDepth`). Synthesized components have negative
synthetic export indices, `IsSynthesized = true`, `TemplatePath`, `"synthesized": true` in `ToJson()`, and cannot be
edited. Component classes are recognised by their class chain (`IsStaticMeshComponent` covers SCUM's
`InteriorStaticMeshComponent`); `IsVisible` reflects `bVisible`/`bHiddenInGame`. Blueprint classes missing from the source
are listed in one warning per level.

## Journal format

One JSON object per line, UTF-8 without BOM, flushed on every change; never rewritten (a torn last line from a crash is
truncated on open):

```jsonl
{"seq":0,"at":"2026-09-30T12:00:00+00:00","type":"header","format":"scumstudio.journal/1"}
{"seq":1,"at":"...","type":"edit","edit":{"op":"deleteActor","target":{"level":"/Game/ConZ_Files/Maps/The_Island/A_0_Outpost","actor":"StaticMeshActor_12"}}}
{"seq":2,"at":"...","type":"edit","edit":{"op":"setTransform","target":{...},"old":{"location":[0,0,0],"rotation":[0,0,0],"scale":[1,1,1]},"new":{...}}}
{"seq":3,"at":"...","type":"undo","ref":2}
{"seq":4,"at":"...","type":"redo","ref":2}
```

Vectors are `[x, y, z]` (cm), rotators `[pitch, yaw, roll]` (degrees). A new edit after undos discards the redo stack;
the undone edits stay in the file and show as `discarded` in the history. ISM instance indices always refer to the
pristine package (deleting an instance never renumbers the others).

## Command line

```
scumstudio level list <source> [--cell A_0] [--kind Poi] [--all] [--json] [--no-crosscheck] [--aes <key>]
scumstudio level dump <source> <sublevel> [--json] [--instances] [--no-templates] [--aes <key>]
scumstudio project new <dir.ssproj> [--name] [--game-build] [--source <paks|loose>]... [--server-source <...>]...
scumstudio project status|history|undo|redo <dir.ssproj>
scumstudio project apply <dir.ssproj> <ops.jsonl>
```

`<source>` is a Paks folder, a single `.pak` or a loose folder with `SCUM/Content`. Encrypted stock paks need the key in
`SCUMSTUDIO_AES_KEY` (preferred) or `--aes`; it is never printed.

## Tests

`tests/ScumStudio.Tests/Level/`: world index classification on ~60 realistic names, graph building on a fake reader
(nested and cross-actor attachments, absolute rotation, ISM instances, inferred roots, cycles), edit/inverse/JSON round
trips, journal and project persistence, and the CUE4Parse reader on synthetic cooked packages written with
`ScumStudio.Formats.PackageWriter` (level, Blueprint templates, persistent level with StreamingLevels). No `.umap` from the
game is needed; fixture tests (`SCUM_FIXTURES`) only check that the stock archive contains no levels. With a map slice
(`SCUM_MAP_SLICE` = a loose client cook containing `SCUM/Content`, e.g. the A_0 Outpost area), `BlueprintExpansionTests`
check the saloon Blueprint, its child actor door, the outpost exterior counts, and a synthetic level (layered over the
slice) that stores only the saloon's root, which the reader rebuilds to the same component transforms as the real level.
