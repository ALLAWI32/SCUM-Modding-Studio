using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// D01 on the reporter's own export (Discord jenni_1129: seven Rager colour clones exported with 3.2, 1072 packages, the
/// game crashed after a restart). Her export report lists <c>&lt;Colour&gt;Rager_Item_Container</c> but no
/// <c>&lt;Colour&gt;Rager_Item_Container_ES</c> and no <c>LWObjectSetup</c> record for it. One colour clone planned the way
/// 3.2 recorded it (without the container's setup) and built with this version must ship every package and registry record
/// her export had for that colour, plus the container's setup and its two <c>LWObjectSetup</c> records. Runs only when
/// <c>SCUM_JENNI_REPORT</c> names her report and <c>SCUM_PAKS</c> the game; her report is never committed.
/// </summary>
public sealed partial class JenniRagerExportRealTests(ITestOutputHelper output)
{
    private const string Rager = "/Game/ConZ_Files/Vehicles/Car/Rager/";

    [Theory]
    [InlineData("RedRager")]
    public void AColourCloneShipsEverythingHerExportHadPlusTheContainersSetup(string colour)
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks)
            || Environment.GetEnvironmentVariable("SCUM_JENNI_REPORT") is not { Length: > 0 } report || !File.Exists(report))
        {
            return; // needs the game and her report
        }

        var lines = File.ReadAllLines(report);
        var herPackages = lines.Where(l => l.StartsWith("- New: SCUM/Content/", StringComparison.Ordinal) && l.Contains(colour, StringComparison.Ordinal))
            .Select(l => "/Game/" + Path.ChangeExtension(l["- New: SCUM/Content/".Length..], null).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var herRecords = lines.Select(l => RegisteredLine().Match(l)).Where(m => m.Success && m.Groups[2].Value.Contains(colour, StringComparison.Ordinal))
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(herPackages);

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var plan = CloneFamilyPlanner.PlanVehicle(catalog, Rager + "BPC_Rager", colour);
        var recorded = plan with { Packages = plan.Packages.Where(p => !p.Key.EndsWith("_Item_Container_ES", StringComparison.Ordinal)).ToList() }; // what 3.2 put in her journal
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([recorded], new Dictionary<string, IReadOnlyList<TunableEdit>>()));
        var ours = result.Packages.Where(p => p.IsClone).Select(p => p.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ourRecords = result.Registered.Select(r => $"{r.PrimaryAssetType} {r.ObjectPath}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = herPackages.Except(ours, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var extra = ours.Except(herPackages, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var missingRecords = herRecords.Except(ourRecords, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var extraRecords = ourRecords.Except(herRecords, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        output.WriteLine($"{colour}: her export {herPackages.Count} packages, {herRecords.Count} registry records; this build {ours.Count} packages, {ourRecords.Count} records; warnings {result.Warnings.Count}");
        output.WriteLine($"kinds: {string.Join(", ", ours.GroupBy(Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"))}");
        foreach (var line in missing.Select(p => "missing package " + p).Concat(extra.Select(p => "extra package " + p))
                     .Concat(missingRecords.Select(r => "missing record " + r)).Concat(extraRecords.Select(r => "extra record " + r)))
        {
            output.WriteLine("  " + line);
        }

        Assert.Empty(missing);
        Assert.Empty(missingRecords);
        Assert.Equal([$"{Rager}{colour}_Item_Container_ES"], extra);
        Assert.Equal(
            [$"LWObjectSetup {Rager}{colour}_Item_Container_ES.{colour}_Item_Container_ES", $"LWObjectSetup {Rager}{colour}_Item_Container_ES.{colour}_Item_Container_ES_C"],
            extraRecords);
    }

    private static string Kind(string package) => package[..package.LastIndexOf('/')];

    [GeneratedRegex(@"^- Registered \((\w+)\): (\S+)$")]
    private static partial Regex RegisteredLine();
}
