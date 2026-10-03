using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Robustness sweep of the cross-package copier over the whole slice: every actor of every POI sublevel is copied into
/// the saloon exterior level, the result must parse, and CUE4Parse must read the copies with the same class and component
/// counts as the sources. Guards the FName / import / dependency remapping against property shapes the hand-written tests
/// do not cover.
/// </summary>
public sealed class ForeignCopySweepTests
{
    private const string Target = MapSlice.MapsPath + "A_0_Outpost_Ext_Saloon";

    [MapSliceFact]
    public void CopiesEveryActorOfTheSliceIntoOneLevel()
    {
        using var catalog = MapSlice.Open();
        var options = new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false, ReadInstances = false };
        var reader = new Cue4ParseLevelReader(catalog, options);
        var world = ScumStudio.Level.World.WorldIndex.FromCatalog(catalog);
        var sources = world.Sublevels.Where(p => p.Kind == ScumStudio.Level.World.WorldPackageKind.Poi && p.PackagePath != Target).ToList();
        Assert.NotEmpty(sources);

        var target = Load(catalog, Target);
        var copies = new List<ForeignActorCopy>();
        var expected = new List<(string Name, string Class, int Components)>();
        var failures = new List<string>();
        foreach (var source in sources)
        {
            LevelDocument document;
            CookedPackage package;
            try
            {
                document = LevelDocument.Load(reader, source.PackagePath);
                package = Load(catalog, source.PackagePath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add($"{source.Name}: {ex.Message}");
                continue;
            }

            // Top-level actors only (child actors travel with their parents); a handful per level keeps the run short.
            foreach (var actor in document.Actors.Where(a => a.ParentComponent is null).Take(6))
            {
                var name = $"{source.Name}__{actor.Name}";
                copies.Add(new ForeignActorCopy(package, actor.Name, name, new TransformValue(actor.Root?.Relative.Location ?? default, default, new ScumStudio.Core.Mathematics.FVector(1, 1, 1)), actor.Root is { IsSynthesized: false } r ? r.Name : null));
                expected.Add((name, actor.ClassName, actor.Components.Count(c => !c.IsSynthesized)));
            }
        }

        Assert.Empty(failures);
        var (bytes, report) = LevelPackageEditor.Apply(target, new LevelEditRequest { ForeignCopies = copies });
        Assert.Equal(copies.Count, report.AddedActors.Count);
        var unexpected = report.Warnings.Where(w => !w.Contains("were cleared", StringComparison.Ordinal) && !w.Contains("keeps the source transform", StringComparison.Ordinal)).ToList();
        Assert.Empty(unexpected);

        // The rewritten package parses, every new export's properties parse, and CUE4Parse reads the copies back.
        var rewritten = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, Target);
        for (var i = target.Exports.Count; i < rewritten.Exports.Count; i++)
        {
            _ = rewritten.ReadProperties(i);
        }

        using var temp = new LevelTempDirectory();
        var staged = temp.Combine("SCUM", "Content", "ConZ_Files", "Maps", "The_Island", "A_0_Outpost_Ext_Saloon");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        bytes.WriteAsync(staged, ".umap").GetAwaiter().GetResult();
        using var overlay = MapSlice.Open(temp.Path);
        var after = LevelDocument.Load(new Cue4ParseLevelReader(overlay, options), Target);
        var mismatches = new List<string>();
        foreach (var (name, cls, components) in expected)
        {
            var copy = after.FindActor(name);
            if (copy is null)
            {
                mismatches.Add($"{name}: missing");
            }
            else if (copy.ClassName != cls || copy.Components.Count(c => !c.IsSynthesized) != components)
            {
                mismatches.Add($"{name}: {copy.ClassName} with {copy.Components.Count} components, expected {cls} with {components}");
            }
        }

        Assert.Empty(mismatches);
        Assert.True(after.Actors.Count >= expected.Count, $"{after.Actors.Count} actors after copying {expected.Count}");
    }

    private static CookedPackage Load(ScumStudio.Assets.Catalog.AssetCatalog catalog, string level)
    {
        Assert.True(catalog.TryGetPackageFile(level, out var file));
        var stem = file.Path[..file.Path.LastIndexOf('.')];
        var uexp = catalog.Provider.Files[stem + ".uexp"].Read();
        var ubulk = catalog.Provider.Files.TryGetValue(stem + ".ubulk", out var b) ? b.Read() : null;
        return CookedPackage.Parse(file.Read(), uexp, ubulk, level);
    }
}
