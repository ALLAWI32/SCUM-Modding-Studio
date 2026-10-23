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

    /// <summary>
    /// r40 (Discord unknownskill: "I change the speed of the plane but it does not apply"): a plane's pull is the game's
    /// <c>_propellerForce</c>, which no plane file stores; it is listed with the default the game's code sets (180000) and a
    /// change is written into the plane's own defaults.
    /// </summary>
    [Fact]
    public void APlanesPropellerForceIsListedWithTheGamesDefaultAndWritten()
    {
        var plane = Blueprint("/Game/Test/BPC_Plane", "BPC_Plane_C", ("/Script/SCUM", "Airplane"), ("_maxVelocity", 8332.5f));

        var force = Assert.Single(InheritedValues.Read(plane, _ => null));
        Assert.Equal(("Default__BPC_Plane_C", "_propellerForce", TunableKind.Float, "180000", "/Script/SCUM.Airplane"),
            (force.Export, force.Path, force.Kind, force.Value, force.InheritedFrom));

        var bytes = InheritedValues.Add(plane, [(force, "360000")]);
        var values = TunableReader.Read(CookedPackage.Parse(bytes.UAsset, bytes.UExp)).ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("360000", values["Default__BPC_Plane_C|_propellerForce"]);
        Assert.Equal("8332.5", values["Default__BPC_Plane_C|_maxVelocity"]);
    }

    /// <summary>
    /// r40 (unknownskill: "changing a backpack, how much size it has inside, load capacity"): a bag's grid is a default
    /// subobject of its entity setup. What the grid does not store is listed (the game's code: 1 x 1 cells, 10 kg, a weight
    /// limit) and written into the grid as the tags the game's own bags store (ByteProperty rows and columns).
    /// </summary>
    [Fact]
    public void ABagsGridValuesItDoesNotStoreAreListedAndWritten()
    {
        var bag = Blueprint("/Game/Test/Bag_ES", "Bag_ES_C", ("/Script/SCUM", "ItemEntitySetup"), [("Weight", 500f)], gridRows: 6);

        var grid = InheritedValues.Read(bag, _ => null).Where(t => t.Export == "EntityGridInventoryComponentSetup_0").ToDictionary(t => t.Path);
        Assert.Equal(["HasContainedWeightLimit", "MaxContainedWeight", "NumColumns"], grid.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("1", TunableKind.UInt, "10", "true"), (grid["NumColumns"].Value, grid["NumColumns"].Kind, grid["MaxContainedWeight"].Value, grid["HasContainedWeightLimit"].Value));
        Assert.All(grid.Values, t => Assert.Equal(InheritedValues.GridInventory, t.InheritedFrom));

        var bytes = InheritedValues.Add(bag, [(grid["NumColumns"], "8"), (grid["MaxContainedWeight"], "50"), (grid["HasContainedWeightLimit"], "false")]);
        var written = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, bag.BasePath);
        var values = TunableReader.Read(written).Where(t => t.Export == "EntityGridInventoryComponentSetup_0").ToDictionary(t => t.Path, t => t.Value);
        Assert.Equal(("6", "8", "50", "false"), (values["NumRows"], values["NumColumns"], values["MaxContainedWeight"], values["HasContainedWeightLimit"]));
        var columns = written.ReadProperties(2).Find("NumColumns")!;
        Assert.Equal(("ByteProperty", 1), (columns.Type, columns.Size));
        Assert.Equal("500", TunableReader.Read(written).Single(t => t.Name == "Weight").Value); // the defaults stay as they were
        Assert.Empty(InheritedValues.Read(written, _ => null).Where(t => t.Export == "EntityGridInventoryComponentSetup_0")); // all stored now
    }

    /// <summary>
    /// r40 review: a bag's rows and columns are one byte in the game file (ByteProperty). A bigger number was accepted by the
    /// page, the MCP tool and the CLI, and at export it stopped every edit of the item. Each value now knows its tag type, a
    /// number outside it is refused before it is recorded, and 255 is written, stored or inherited.
    /// </summary>
    [Fact]
    public void ABagsRowsAndColumnsAreOneByteSoAtMost255()
    {
        var bag = Blueprint("/Game/Test/Bag_ES", "Bag_ES_C", ("/Script/SCUM", "ItemEntitySetup"), [("Weight", 500f)], gridRows: 6);
        var rows = TunableReader.Read(bag).Single(t => t.Name == "NumRows");
        var inherited = InheritedValues.Read(bag, _ => null).ToDictionary(t => t.Name);
        var columns = inherited["NumColumns"];
        Assert.Equal(("ByteProperty", "ByteProperty"), (rows.TagType, columns.TagType));
        Assert.Equal(("FloatProperty", "BoolProperty", "FloatProperty"), (inherited["MaxContainedWeight"].TagType, inherited["HasContainedWeightLimit"].TagType, inherited["MaxHealth"].TagType));
        foreach (var value in new[] { rows, columns })
        {
            Assert.Null(TunableValue.CheckRange(value, "0"));
            Assert.Null(TunableValue.CheckRange(value, " 255 "));
            Assert.Contains("from 0 to 255", TunableValue.CheckRange(value, "256"), StringComparison.Ordinal);
            Assert.NotNull(TunableValue.CheckRange(value, "-1"));
            Assert.NotNull(TunableValue.CheckRange(value, "99999999999999999999999"));
            Assert.NotNull(TunableValue.CheckRange(value, "2.5"));
        }

        // Floats and bools have no whole-number range; other whole numbers have theirs.
        Assert.Null(TunableValue.CheckRange(inherited["MaxContainedWeight"], "100000"));
        Assert.Null(TunableValue.CheckRange(rows with { TagType = null }, "300"));
        Assert.NotNull(TunableValue.CheckRange(rows with { Kind = TunableKind.Int, TagType = "IntProperty" }, "2147483648"));
        Assert.Null(TunableValue.CheckRange(rows with { Kind = TunableKind.Int, TagType = "IntProperty" }, "-2147483648"));
        Assert.NotNull(TunableValue.CheckRange(rows with { TagType = "UInt16Property" }, "65536"));
        Assert.Null(TunableValue.CheckRange(rows with { TagType = "UInt64Property" }, "18446744073709551615"));

        // 255 is written: the stored row count by the patcher, the inherited column count as a new byte tag.
        var patched = TunablePatcher.Apply(bag, [new TunableEdit(rows.Export, rows.Path, "255")]).Bytes;
        var grown = InheritedValues.Add(CookedPackage.Parse(patched.UAsset, patched.UExp, null, bag.BasePath), [(columns, "255")]);
        var written = TunableReader.Read(CookedPackage.Parse(grown.UAsset, grown.UExp, null, bag.BasePath)).Where(t => t.Export == "EntityGridInventoryComponentSetup_0").ToDictionary(t => t.Path);
        Assert.Equal(("255", "255"), (written["NumRows"].Value, written["NumColumns"].Value));
        Assert.Equal("ByteProperty", written["NumColumns"].TagType);
    }

    /// <summary>A Blueprint package: its class (parent <paramref name="super"/>) and its CDO storing <paramref name="floats"/>.</summary>
    private static CookedPackage Blueprint(string path, string className, (string Package, string Class) super, params (string Name, float Value)[] floats) =>
        Blueprint(path, className, super, floats, gridRows: null);

    /// <summary>
    /// A Blueprint package: its class (parent <paramref name="super"/>), its CDO storing <paramref name="floats"/> and, with
    /// <paramref name="gridRows"/>, a grid inventory (<c>EntityGridInventoryComponentSetup_0</c>, outer the CDO) storing only
    /// <c>NumRows</c> (a ByteProperty, as the game's bags).
    /// </summary>
    private static CookedPackage Blueprint(string path, string className, (string Package, string Class) super, (string Name, float Value)[] floats, int? gridRows)
    {
        List<string> names = ["/Script/CoreUObject", "/Script/Engine", "Package", "Class", "BlueprintGeneratedClass", "None", "FloatProperty", super.Package, super.Class, className, "Default__" + className];
        names.AddRange(floats.Select(f => f.Name));
        if (gridRows is not null)
        {
            names.AddRange(["EntityGridInventoryComponentSetup", "NumRows", "ByteProperty"]);
        }
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
        List<ExportEntry> exports =
        [
            new ExportEntry { ClassIndex = -2, SuperIndex = -4, ObjectName = N(className), ObjectFlags = 0x9, FirstExportDependency = -1 },
            new ExportEntry { ClassIndex = 1, ObjectName = N("Default__" + className), ObjectFlags = 0x31, FirstExportDependency = -1 },
        ];
        List<byte[]> data = [cls.ToArray(), cdo.ToArray()];
        if (gridRows is { } rows)
        {
            imports.Add(new(N("/Script/CoreUObject"), N("Class"), -3, N("EntityGridInventoryComponentSetup")));
            var grid = new ByteWriter();
            grid.FName(N("NumRows"));
            grid.FName(N("ByteProperty"));
            grid.I32(1);
            grid.I32(0);
            grid.FName(N("None")); // a plain byte: no enum
            grid.U8(0);
            grid.U8((byte)rows);
            grid.FName(N("None"));
            grid.I32(0);
            exports.Add(new ExportEntry { ClassIndex = -imports.Count, OuterIndex = 2, ObjectName = new FNameRef(names.IndexOf("EntityGridInventoryComponentSetup"), 1), ObjectFlags = 0x1, FirstExportDependency = -1 });
            data.Add(grid.ToArray());
        }

        var bytes = PackageWriter.Build(new PackageBuildInput
        {
            Summary = SyntheticPackage.DefaultSummary(),
            Names = names,
            Imports = imports,
            Exports = exports,
            ExportData = [.. data.Select(d => (ReadOnlyMemory<byte>)d)],
        });
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, path);
    }
}
