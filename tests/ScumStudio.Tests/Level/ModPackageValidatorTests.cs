using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The package check every export runs before it packs (owner 2026-10-16: the game crashed on leaving a server and on
/// Alt+F4 with a studio mod, "everything safe, 100%"). A small level is staged once as the game has it and once with one
/// defect each; the check passes the game's copy and names each defect.
/// </summary>
public sealed class ModPackageValidatorTests : IDisposable
{
    private const string Engine = "/Script/Engine";
    private const string LevelPath = "/Game/ConZ_Files/Maps/The_Island/A_0_CheckLevel";
    private const string LevelFile = SyntheticLevels.MapsFolder + "/A_0_CheckLevel";
    private const string RockPackage = "/Game/ConZ_Files/Models/Rocks/SM_Rock";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-validator-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void TheGamesOwnLevelPasses()
    {
        var result = Check(new Defects());

        Assert.True(result.Passed, string.Join('\n', result.Problems));
        Assert.Equal(1, result.Packages);
        Assert.True(result.Exports >= 9);
        Assert.StartsWith("Validator: 1 packages checked, 0 problems", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletedActorAnotherActorStillPointsAtIsNamed()
    {
        var result = Check(new Defects { DeleteRockB = true });

        Assert.Equal(2, result.Problems.Count);
        Assert.Contains(result.Problems, p => p.Contains("Switch.Target still points at PersistentLevel.Rock_B, which the export deleted (the game reads it as null)", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("Switch.Members[1] still points at PersistentLevel.Rock_B, which the export deleted (the game reads it as null, a null entry in that list)", StringComparison.Ordinal));
    }

    [Fact]
    public void DeletingAnActorOthersPointAtLeavesNothingPointingAtIt()
    {
        // The export's own deletion: before 2026-10-16 the switch kept its target and the list its member (both read as
        // null by the game, the list with a null entry), and the check above stopped the export.
        var stock = Build(new Defects { WithLift = true });
        var (bytes, report) = LevelPackageEditor.Apply(CookedPackage.Parse(stock.UAsset, stock.UExp, null, LevelPath), new LevelEditRequest { DeleteActors = ["Rock_B"] });

        var result = Check(bytes, stock);

        Assert.True(result.Passed, string.Join('\n', result.Problems));

        // The lift assigned to it goes with it (the game has no lift without its trade post), the switch and list let go.
        Assert.Equal(["Rock_B", "BP_CarLift_2"], report.RemovedActors);
        Assert.Contains(report.Warnings, w => w.Contains("'BP_CarLift_2' was removed with 'Rock_B'", StringComparison.Ordinal));
        Assert.Contains("Switch.Target", report.ForgottenReferences);
        Assert.Contains("Switch.Members[]", report.ForgottenReferences);
        var written = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var sw = Enumerable.Range(0, written.Exports.Count).Single(i => written.ResolveName(written.Exports[i].ObjectName) == "Switch");
        var rockA = Enumerable.Range(0, written.Exports.Count).Single(i => written.ResolveName(written.Exports[i].ObjectName) == "Rock_A") + 1;
        var block = written.ReadProperties(sw);
        Assert.Equal(0, Assert.IsType<ObjectValue>(block.Find("Target")!.Value).Index);
        Assert.Equal([rockA], Assert.IsType<ArrayValue>(block.Find("Members")!.Value).Items.Cast<ObjectValue>().Select(o => o.Index));
    }

    [Fact]
    public void DeletingOneActorAndCopyingAnotherInTheSameLevelPasses()
    {
        // One object deleted and one copied in the same level: the actor list keeps its length and every actor after the
        // deleted one sits one place earlier. The list is a set to the game, but the check read the moved places as
        // references to other actors and refused the pak (final check after r37: a Map pak and the whole project's pak).
        var stock = Build(new Defects());
        var (bytes, _) = LevelPackageEditor.Apply(CookedPackage.Parse(stock.UAsset, stock.UExp, null, LevelPath),
            new LevelEditRequest { DeleteActors = ["Rock_B"], Copies = [new ActorCopy("Rock_A", "Rock_A_Copy", null)] });

        var result = Check(bytes, stock);

        Assert.True(result.Passed, string.Join('\n', result.Problems));
    }

    [Fact]
    public void ADeletedActorStillInTheActorListIsNamed()
    {
        var result = Check(new Defects { DeleteRockB = true, ClearTarget = true, KeepDeletedListed = true });

        Assert.Contains(result.Problems, p => p.Contains("actor list still holds PersistentLevel.Rock_B, which the export deleted", StringComparison.Ordinal));
    }

    [Fact]
    public void AComponentInTheActorListIsNamed()
    {
        var result = Check(new Defects { ListComponent = true });

        Assert.Contains(result.Problems, p => p.Contains("a component, not an actor", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoActorsWithOneNameAreNamed()
    {
        var result = Check(new Defects { DuplicateName = true });

        Assert.Contains(result.Problems, p => p.Contains("2 objects named PersistentLevel.Rock_A share one outer", StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceOutsideTheTablesIsNamed()
    {
        var result = Check(new Defects { TargetOutOfRange = true });

        Assert.Contains(result.Problems, p => p.Contains("Switch.Target points outside the import/export tables (999)", StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceMovedToAnotherObjectIsNamed()
    {
        var result = Check(new Defects { TargetShifted = true });

        Assert.Contains(result.Problems, p => p.Contains("Switch.Target points at PersistentLevel.Rock_A; the game's points at PersistentLevel.Rock_B, which is still there", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAttachLoopAndAnAttachmentIntoAnotherActorAreNamed()
    {
        var loop = Check(new Defects { AttachLoop = true });
        Assert.Contains(loop.Problems, p => p.Contains("AttachParent chain loops", StringComparison.Ordinal));

        var across = Check(new Defects { AttachAcross = true });
        Assert.Contains(across.Problems, p => p.Contains("is attached into another actor", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImportNobodyHasIsNamed()
    {
        var result = Check(new Defects { MissingMesh = true });

        Assert.Contains(result.Problems, p => p.Contains("neither the game nor the mod has the package /Game/Nowhere/SM_Missing", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImportOnlyTheClientHasIsNoHoleInTheServersPakButOneNobodyHasStillIs()
    {
        // Report "ServerMods" (3.4.0): the server's pak is checked against the dedicated server's cook, which leaves out what
        // only players' games use; the client's game files tell such an asset from a hole.
        var client = Path.Combine(_dir, "client-game");
        Write(client, "SCUM/Content/Nowhere/SM_Missing", BuildMesh(), ".uasset");
        using var clientCatalog = AssetCatalog.OpenLoose(client);

        var serverOnly = Check(Build(new Defects { MissingMesh = true }), options: new ValidationOptions { Client = clientCatalog });
        Assert.True(serverOnly.Passed, string.Join('\n', serverOnly.Problems));

        // Neither side has it: still stopped, as without the client's files.
        var other = Path.Combine(_dir, "client-game-other");
        Write(other, "SCUM/Content/Elsewhere/SM_Other", BuildMesh(), ".uasset");
        using var emptyClient = AssetCatalog.OpenLoose(other);
        var nobody = Check(Build(new Defects { MissingMesh = true }), options: new ValidationOptions { Client = emptyClient });
        Assert.Contains(nobody.Problems, p => p.Contains("imports /Game/Nowhere/SM_Missing, but neither the game nor the mod has the package /Game/Nowhere/SM_Missing (the game would get null there)", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImportedModsOwnReferenceTheGameNoLongerHasIsANoteUnlessAnObjectIsBuiltFromIt()
    {
        // Report "ServerMods": an imported mod's BP_Prisoner from before a game update imports a post process the game removed.
        var carried = new ValidationOptions { Carried = new Dictionary<string, string> { [LevelPath] = "OldMod" } };
        var reference = Check(Build(new Defects { MissingMesh = true }), options: carried);
        Assert.True(reference.Passed, string.Join('\n', reference.Problems));
        var note = Assert.Single(reference.Notes);
        Assert.Contains($"{LevelPath} (from the imported mod OldMod) imports /Game/Nowhere/SM_Missing", note, StringComparison.Ordinal);
        Assert.Contains("made for another version of SCUM", note, StringComparison.Ordinal);

        // The same file written by the export itself is a broken import that stops it.
        Assert.Contains(Check(Build(new Defects { MissingMesh = true })).Problems, p => p.Contains("neither the game nor the mod has the package /Game/Nowhere/SM_Missing", StringComparison.Ordinal));

        // An object of the mod's package built from what is gone (its class): the game cannot make it, so it still stops.
        var built = Check(Build(new Defects { MissingClass = true }), options: carried);
        Assert.Contains(built.Problems, p => p.Contains("imports /Game/Nowhere/BP_Gone", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExportThatDoesNotTakeItsSizeIsNamed()
    {
        var result = Check(new Defects { ExtraBytes = true });

        Assert.Contains(result.Problems, p => p.Contains("Rock_A.StaticMeshComponent0 (StaticMeshComponent) does not take exactly its SerialSize (4 byte(s) left", StringComparison.Ordinal));
    }

    [Fact]
    public void ARepeatedLightGuidIsNamed()
    {
        var result = Check(new Defects { RepeatGuid = true });

        Assert.Contains(result.Problems, p => p.Contains("LightGuid", StringComparison.Ordinal) && p.Contains("is held by 2 objects", StringComparison.Ordinal));
    }

    [Fact]
    public void ACopiedLightKeysNoBuiltDataByItsSourcesId()
    {
        // As the editor's duplicate does: before 2026-10-16 a copy kept its source's LightGuid (defolt-style copies of the
        // abandoned city hall: 9 neon lamps and a reflection capture shared their ids) and the check stopped the export.
        var stock = Build(new Defects());
        var (bytes, _) = LevelPackageEditor.Apply(CookedPackage.Parse(stock.UAsset, stock.UExp, null, LevelPath),
            new LevelEditRequest { Copies = [new ActorCopy("Rock_A", "Rock_A_Copy", null)] });

        var result = Check(bytes);

        Assert.True(result.Passed, string.Join('\n', result.Problems));
        var written = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var lights = Enumerable.Range(0, written.Exports.Count).Where(i => written.ResolveName(written.Exports[i].ObjectName) == "PointLight")
            .Select(i => Assert.IsType<GuidValue>(written.ReadProperties(i).Find("LightGuid")!.Value).Value).ToList();
        Assert.Equal(3, lights.Count); // Rock_A's, Rock_B's and the copy's
        Assert.Equal(3, lights.Distinct().Count());
    }

    [Fact]
    public void AMeshsSharedCollisionGuidIsNoProblem()
    {
        // Every piece of one mesh names that mesh's collision by the same CachedMeshBodySetupGuid (bent bridge pieces do).
        Assert.True(Check(new Defects { RepeatBodySetupGuid = true }).Passed);
    }

    [Fact]
    public void AGameSoundEventInAServerCookIsNamed()
    {
        // The dedicated server's cook holds a 12-byte stub per Wwise event; a pak that brings the game's whole event to it is the client's.
        var game = Path.Combine(_dir, "server-game");
        var staging = Path.Combine(_dir, "server-staging");
        Write(game, "SCUM/Content/WwiseAudio/Events/Play_Door", SoundEvent(stub: true), ".uasset");
        Write(staging, "SCUM/Content/WwiseAudio/Events/Play_Door", SoundEvent(stub: false), ".uasset");
        using var catalog = AssetCatalog.OpenLoose(game);

        var result = ModPackageValidator.Validate(staging, catalog);

        Assert.Contains(result.Problems, p => p.Contains("Play_Door is a game's sound event, but these game files (a dedicated server's) hold a stub there", StringComparison.Ordinal));
    }

    [Fact]
    public void TheServersPakMustChangeWhatTheClientsDoes()
    {
        static ExportResult Cook(ProjectSourceRole role, string[] removed, params (string Level, string[] Added)[] levels) => new()
        {
            Role = role,
            ModName = "M",
            StagingDirectory = "staging",
            Warnings = [],
            Levels = levels.Select(l => new ExportedLevel(l.Level, l.Level + ".umap", new LevelEditReport
            {
                LevelExport = "PersistentLevel", ActorsBefore = 3, ActorsAfter = 3, RemovedActors = removed, PatchedTransforms = [], AddedNames = [], Warnings = [],
                AddedActors = l.Added,
            })).ToList(),
            Assets = role == ProjectSourceRole.Client ? [new ExportedAsset("/Game/V/BPC_Red", "SCUM/Content/V/BPC_Red.uasset", IsClone: true)] : [],
        };

        var same = ModPackageValidator.CompareCooks(Cook(ProjectSourceRole.Client, ["Rock_B"], ("/Game/A", ["Door_Copy"])), Cook(ProjectSourceRole.Client, ["Rock_B"], ("/Game/A", ["Door_Copy"])));
        Assert.Empty(same);

        var lines = ModPackageValidator.CompareCooks(
            Cook(ProjectSourceRole.Client, ["Rock_B"], ("/Game/A", ["Door_Copy"]), ("/Game/B", [])),
            Cook(ProjectSourceRole.Server, [], ("/Game/A", [])));

        Assert.Contains(lines, l => l.StartsWith("/Game/B is changed in the client pak only", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("/Game/A: 1 actor(s) removed in the client pak only (Rock_B)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("/Game/A: 1 actor(s) added in the client pak only (Door_Copy)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("/Game/V/BPC_Red (a clone) is in the client pak only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExportWithAProblemWritesNoPak()
    {
        var result = Check(new Defects { TargetOutOfRange = true });
        var exception = new ModValidationException(result);

        Assert.Contains("was not packed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Switch.Target", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<InvalidOperationException>(exception); // the app and the command line show it as a failed export
        await Task.CompletedTask;
    }

    private ValidationResult Check(Defects defects) => Check(Build(defects));

    private ValidationResult Check(PackageBytes mod, PackageBytes? stock = null, ValidationOptions? options = null)
    {
        var game = Path.Combine(_dir, "game-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(_dir, "staging-" + Guid.NewGuid().ToString("N"));
        Write(game, LevelFile, stock ?? Build(new Defects()), ".umap");
        Write(game, "SCUM/Content/ConZ_Files/Models/Rocks/SM_Rock", BuildMesh(), ".uasset");
        Write(staging, LevelFile, mod, ".umap");
        using var catalog = AssetCatalog.OpenLoose(game);
        return ModPackageValidator.Validate(staging, catalog, options, CancellationToken.None);
    }

    private static void Write(string root, string virtualBase, PackageBytes bytes, string extension) =>
        bytes.WriteAsync(Path.Combine([root, .. virtualBase.Split('/')]), extension).GetAwaiter().GetResult();

    /// <summary>What one staged copy of the level gets wrong.</summary>
    private sealed record Defects
    {
        public bool DeleteRockB { get; init; }

        public bool ClearTarget { get; init; }

        public bool KeepDeletedListed { get; init; }

        public bool ListComponent { get; init; }

        public bool DuplicateName { get; init; }

        public bool TargetOutOfRange { get; init; }

        public bool TargetShifted { get; init; }

        public bool AttachLoop { get; init; }

        public bool AttachAcross { get; init; }

        public bool MissingMesh { get; init; }

        public bool MissingClass { get; init; }

        public bool ExtraBytes { get; init; }

        public bool RepeatGuid { get; init; }

        public bool RepeatBodySetupGuid { get; init; }

        public bool WithLift { get; init; }
    }

    /// <summary>
    /// World, PersistentLevel, Rock_A and Rock_B (static mesh actors, a mesh and a light each), and Switch (an actor whose
    /// <c>Target</c> is Rock_B, as a door, a switch or a spawner group points at another actor).
    /// </summary>
    private static PackageBytes Build(Defects d)
    {
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_CheckLevel", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var rock = p.ObjectImport(RockPackage, Engine, "StaticMesh", "SM_Rock");
        var actorClass = p.ScriptClass(Engine, "StaticMeshActor");
        var inert = d.DeleteRockB ? p.ScriptClass("/Script/CoreUObject", "Object") : 0;
        var a = p.Export("Rock_A", actorClass, level);
        var aMesh = p.Export("StaticMeshComponent0", p.ScriptClass(Engine, "StaticMeshComponent"), a);
        var aLight = p.Export("PointLight", p.ScriptClass(Engine, "PointLightComponent"), a);
        var b = p.Export(d.DuplicateName ? "Rock_A" : "Rock_B", d.DeleteRockB ? inert : actorClass, level);
        var bMesh = p.Export("StaticMeshComponent0", d.DeleteRockB ? inert : p.ScriptClass(Engine, "StaticMeshComponent"), b);
        var bLight = p.Export("PointLight", d.DeleteRockB ? inert : p.ScriptClass(Engine, "PointLightComponent"), b);
        var sw = p.Export("Switch", d.MissingClass ? p.ObjectImport("/Game/Nowhere/BP_Gone", Engine, "BlueprintGeneratedClass", "BP_Gone_C") : p.ScriptClass(Engine, "Actor"), level);
        var swRoot = p.Export("DefaultSceneRoot", p.ScriptClass(Engine, "SceneComponent"), sw);
        var missing = d.MissingMesh ? p.ObjectImport("/Game/Nowhere/SM_Missing", Engine, "StaticMesh", "SM_Missing") : 0;
        var lift = d.WithLift ? p.Export("BP_CarLift_2", p.ScriptClass(Engine, "Actor"), level) : 0;

        var listed = new List<int> { a, sw };
        if (!d.DeleteRockB || d.KeepDeletedListed)
        {
            listed.Insert(1, b);
        }

        if (d.ListComponent)
        {
            listed.Add(aMesh);
        }

        if (lift > 0)
        {
            listed.Add(lift);
            p.SetPayload(lift, p.Properties(t => t.Object("_assignedTradePost", b)));
        }

        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => SyntheticLevels.LevelNative(w, listed)));
        p.SetPayload(a, p.Properties(t => t.Object("RootComponent", aMesh)));
        p.SetPayload(aMesh, p.Properties(t =>
        {
            t.Object("StaticMesh", d.MissingMesh ? missing : rock);
            t.Vector("RelativeLocation", 100, 0, 0);
            t.Struct("CachedMeshBodySetupGuid", "Guid", w => { w.I32(9); w.I32(9); w.I32(9); w.I32(9); });
            if (d.AttachLoop)
            {
                t.Object("AttachParent", aLight);
            }
        }, w =>
        {
            w.I32(0); // LODData
            if (d.ExtraBytes)
            {
                w.I32(0x1234);
            }
        }));
        p.SetPayload(aLight, p.Properties(t =>
        {
            t.Object("AttachParent", d.AttachAcross ? swRoot : aMesh);
            t.Struct("LightGuid", "Guid", w => { w.I32(1); w.I32(2); w.I32(3); w.I32(4); });
        }));
        if (d.DeleteRockB)
        {
            var blank = p.Properties();
            p.SetPayload(b, blank);
            p.SetPayload(bMesh, blank);
            p.SetPayload(bLight, blank);
        }
        else
        {
            p.SetPayload(b, p.Properties(t => t.Object("RootComponent", bMesh)));
            p.SetPayload(bMesh, p.Properties(t =>
            {
                t.Object("StaticMesh", rock);
                t.Vector("RelativeLocation", 500, 0, 0);
                t.Struct("CachedMeshBodySetupGuid", "Guid", w => { w.I32(9); w.I32(9); w.I32(9); w.I32(d.RepeatBodySetupGuid ? 9 : 8); });
            }, w => w.I32(0)));
            p.SetPayload(bLight, p.Properties(t =>
            {
                t.Object("AttachParent", bMesh);
                t.Struct("LightGuid", "Guid", w => { w.I32(d.RepeatGuid ? 1 : 5); w.I32(d.RepeatGuid ? 2 : 6); w.I32(d.RepeatGuid ? 3 : 7); w.I32(d.RepeatGuid ? 4 : 8); });
            }));
        }

        p.SetPayload(sw, p.Properties(t =>
        {
            t.Object("RootComponent", swRoot);
            t.Object("Target", d.TargetOutOfRange ? 999 : d.TargetShifted ? a : d.ClearTarget ? 0 : b);
            t.ObjectArray("Members", d.ClearTarget ? [a] : [a, b]);
        }));
        p.SetPayload(swRoot, p.Properties(t => t.Vector("RelativeLocation", 0, 0, 50)));
        return p.Build();
    }

    /// <summary>A Wwise event: the server's 12-byte stub (no properties, no guid) or the game's (properties and data).</summary>
    private static PackageBytes SoundEvent(bool stub)
    {
        var p = new SyntheticLevelPackage();
        var ev = p.Export("Play_Door", p.ScriptClass("/Script/AkAudio", "AkAudioEvent"), 0, flags: 0x1);
        p.SetPayload(ev, stub ? p.Properties() : p.Properties(t => t.Float("MaxAttenuationRadius", 3000), w => w.I32(0)));
        return p.Build();
    }

    /// <summary>A stand-in for the rock mesh the level imports (only its export table is read).</summary>
    private static PackageBytes BuildMesh()
    {
        var p = new SyntheticLevelPackage();
        var mesh = p.Export("SM_Rock", p.ScriptClass(Engine, "Object"), 0, flags: 0x1);
        p.SetPayload(mesh, p.Properties());
        return p.Build();
    }
}
