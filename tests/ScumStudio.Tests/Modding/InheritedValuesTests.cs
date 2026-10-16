using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Tuning;
using ScumStudio.Tests.Formats;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// CwHedgehog: "I want to change this weapon's MaxHealth. It seems to be inherited from a parent?" A cooked Blueprint stores
/// only what differs from its parent; a value it inherits is listed with its parent and, once changed, written as a tag of
/// its own into the package's CDO.
/// </summary>
public sealed class InheritedValuesTests
{
    [Fact]
    public void AValueOfTheGamesCodeIsListedAndWrittenIntoTheOwnDefaults()
    {
        var es = Blueprint("/Game/Test/Katana_ES", "Katana_ES_C", ("/Script/SCUM", "ItemEntitySetup"), ("Weight", 1500f));

        var inherited = Assert.Single(InheritedValues.Read(es, _ => null));
        Assert.Equal(("Default__Katana_ES_C", "MaxHealth", TunableKind.Float, string.Empty), (inherited.Export, inherited.Path, inherited.Kind, inherited.Value));
        Assert.Equal("/Script/SCUM.ItemEntitySetup", inherited.InheritedFrom);
        Assert.DoesNotContain(TunableReader.Read(es), t => t.Name == "MaxHealth"); // not stored: the page could not edit it before

        var bytes = InheritedValues.Add(es, [(inherited, "500")]);
        var written = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, es.BasePath);
        var values = TunableReader.Read(written).ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("500", values["Default__Katana_ES_C|MaxHealth"]);
        Assert.Equal("1500", values["Default__Katana_ES_C|Weight"]);
        var tag = written.ReadProperties(1).Find("MaxHealth")!;
        Assert.Equal(("FloatProperty", 4, false), (tag.Type, tag.Size, tag.HasPropertyGuid));
        Assert.Empty(InheritedValues.Read(written, _ => null)); // stored now: an ordinary value from here on
        Assert.Equal(4, written.ReadProperties(1).NativeDataLength); // the CDO's trailing object guid flag stays after the list
    }

    [Fact]
    public void ABlueprintParentsValuesAreListedWithTheirValue()
    {
        var parent = Blueprint("/Game/Test/Katana", "Katana_C", ("/Script/SCUM", "WeaponItem"), ("Weight", 9f), ("_damageOverTime", 0.2f));
        var child = Blueprint("/Game/Test/Katana1", "Katana1_C", ("/Game/Test/Katana", "Katana_C"), ("Weight", 7f));

        var inherited = InheritedValues.Read(child, path => path == "/Game/Test/Katana" ? parent : null);
        var damage = Assert.Single(inherited); // Weight is stored on the child; WeaponItem has no known value
        Assert.Equal(("_damageOverTime", "0.2", "/Game/Test/Katana.Katana_C"), (damage.Path, damage.Value, damage.InheritedFrom));

        var bytes = InheritedValues.Add(child, [(damage, "0.05")]);
        var values = TunableReader.Read(CookedPackage.Parse(bytes.UAsset, bytes.UExp)).ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("0.05", values["Default__Katana1_C|_damageOverTime"]);
        Assert.Equal("7", values["Default__Katana1_C|Weight"]);
        Assert.Equal("0.2", TunableReader.Read(parent).Single(t => t.Name == "_damageOverTime").Value); // the parent keeps its own
    }

    [Fact]
    public void AStoredOrUnknownValueIsRefused()
    {
        var es = Blueprint("/Game/Test/Katana_ES", "Katana_ES_C", ("/Script/SCUM", "ItemEntitySetup"), ("Weight", 1500f));
        var weight = TunableReader.Read(es).Single(t => t.Name == "Weight");
        Assert.Throws<InvalidOperationException>(() => InheritedValues.Add(es, [(weight, "5")]));
        var maxHealth = InheritedValues.Read(es, _ => null).Single();
        Assert.Throws<InvalidOperationException>(() => InheritedValues.Add(es, [(maxHealth, "lots")]));
    }

    /// <summary>A Blueprint package: its class (parent <paramref name="super"/>) and its CDO storing <paramref name="floats"/>.</summary>
    private static CookedPackage Blueprint(string path, string className, (string Package, string Class) super, params (string Name, float Value)[] floats)
    {
        List<string> names = ["/Script/CoreUObject", "/Script/Engine", "Package", "Class", "BlueprintGeneratedClass", "None", "FloatProperty", super.Package, super.Class, className, "Default__" + className];
        names.AddRange(floats.Select(f => f.Name));
        var isBlueprint = super.Package.StartsWith("/Game/", StringComparison.Ordinal);
        if (isBlueprint)
        {
            names.Add("Default__" + super.Class);
        }

        FNameRef N(string name) => new(names.IndexOf(name));
        List<ImportEntry> imports =
        [
            new(N("/Script/CoreUObject"), N("Package"), 0, N("/Script/Engine")),
            new(N("/Script/CoreUObject"), N("Class"), -1, N("BlueprintGeneratedClass")),
            new(N("/Script/CoreUObject"), N("Package"), 0, N(super.Package)),
            new(isBlueprint ? N("/Script/Engine") : N("/Script/CoreUObject"), isBlueprint ? N("BlueprintGeneratedClass") : N("Class"), -3, N(super.Class)),
        ];

        var cdo = new ByteWriter();
        foreach (var (name, value) in floats)
        {
            cdo.FName(N(name));
            cdo.FName(N("FloatProperty"));
            cdo.I32(4);
            cdo.I32(0);
            cdo.U8(0);
            cdo.F32(value);
        }

        cdo.FName(N("None"));
        cdo.I32(0); // no object guid
        var cls = new ByteWriter();
        cls.FName(N("None"));
        var bytes = PackageWriter.Build(new PackageBuildInput
        {
            Summary = SyntheticPackage.DefaultSummary(),
            Names = names,
            Imports = imports,
            Exports =
            [
                new ExportEntry { ClassIndex = -2, SuperIndex = -4, ObjectName = N(className), ObjectFlags = 0x9, FirstExportDependency = -1 },
                new ExportEntry { ClassIndex = 1, ObjectName = N("Default__" + className), ObjectFlags = 0x31, FirstExportDependency = -1 },
            ],
            ExportData = [cls.ToArray(), cdo.ToArray()],
        });
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, path);
    }
}
