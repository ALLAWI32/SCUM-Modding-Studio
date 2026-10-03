using System.Diagnostics.CodeAnalysis;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.World;

namespace ScumStudio.Tests.Level;

public sealed class WorldIndexTests
{
    private const string Root = "SCUM/Content/ConZ_Files/Maps/The_Island/";

    /// <summary>Realistic names from the pak index survey (report_mapStruct): every category and naming quirk.</summary>
    internal static readonly string[] SampleFiles =
    [
        // persistent level
        Root + "The_Island.umap", Root + "The_Island.uexp", Root + "The_Island_LevelStaticData.uasset",
        // POI / grid sublevels
        Root + "A_0_Outpost.umap", Root + "A_0_Outpost.uexp",
        Root + "A_0_Outpost_Exterior.umap", Root + "A_0_Outpost_Exterior_02.umap", Root + "A_0_Outpost_Ext_Armory.umap",
        Root + "A_0_Outpost_Armory.umap", Root + "A_0_Outpost_Trader.umap", Root + "A_0_Outpost_Airfield.umap",
        Root + "A_0_Small_Port.umap", Root + "A_0_WW2_Bunker_01.umap", Root + "A_0_Dr_Tudman_Bridge.umap",
        Root + "A_1_Apatija.umap", Root + "B_1_Trpanj.umap", Root + "C_1_Veliki_Tabor.umap", Root + "D_1_Kirin.umap",
        Root + "D_1_Castle_04_Interior.umap", Root + "D_4_Samobor_01_Block_02_Interior_01.umap",
        Root + "D_4_Airport_Zeljava_Entrance_05.umap", Root + "D_4_Samobor_00_Atmosphere.umap",
        Root + "B_3_Mirkovci_ProceduralFoliageBlockers.umap", Root + "Z_3_Outpost_Exterior.umap", Root + "Z_0_Stone_Quarry.umap",
        // landscape tiles (+ bulk and one BuiltData)
        Root + "Landscape_A_0_1.umap", Root + "Landscape_A_0_1.uexp", Root + "Landscape_A_0_1.ubulk",
        Root + "Landscape_A_0_1b.umap", Root + "Landscape_A_0_1c.umap", Root + "Landscape_A_0_1c_BuiltData.uasset",
        Root + "Landscape_D_3_4d.umap", Root + "Landscape_Z_4_2.umap",
        // TV bases
        Root + "TV_Base_B_2.umap", Root + "TV_Base_D_3.umap", Root + "TV_Base_B_2_Research_Facility_Area_A.umap",
        Root + "TV_Base_Z_1_Research_Facility_Audio.umap", Root + "TV_Base_A_4_Abandoned.umap",
        Root + "B2_TV_Base_Research_Facility_Entrance/B2_TV_Base_Research_Facility_Entrance.umap",
        // misc / special
        Root + "AquaticVolumes.umap", Root + "Biomes.umap", Root + "Threat_Zones.umap", Root + "WaterSplines.umap",
        Root + "Safe_Zone_D_2.umap", Root + "B3_Castle_Ruins.umap", Root + "B3_Castle_Ruins_02.umap", Root + "D4_Airport_Zeljava.umap",
        Root + "D_1_Halloween_Hut/D_1_Halloween_Hut.umap",
        // BuiltData, HLOD
        Root + "A_0_Outpost_Airfield_BuiltData.uasset", Root + "A_0_Outpost_Airfield_BuiltData.uexp",
        Root + "A_0_Outpost_Exterior_02_BuiltData.uasset",
        Root + "HLOD/A_0_Outpost_Exterior_0_HLOD.uasset", Root + "HLOD/A_0_Outpost_Exterior_0_HLOD.ubulk",
        Root + "HLOD/Z_3_Outpost_Exterior_0_HLOD.uasset",
        // Pripyat
        Root + "AbandonedCity_PripyatLike/C_0_AbandonedCity_00.umap",
        Root + "AbandonedCity_PripyatLike/C_0_AbandonedCity_00_Lighting.umap",
        Root + "AbandonedCity_PripyatLike/C_0_AbandonedCity_01_PalaceOfEnergetik_Ext.umap",
        Root + "AbandonedCity_PripyatLike/C_0_AbandonedCity_01a_PalaceOfEnergetik_Int.umap",
        Root + "AbandonedCity_PripyatLike/C_0_AbandonedCity_00_BuiltData.uasset",
        // shared assets (not levels)
        Root + "The_Island_sharedassets/Asphalt_LayerInfo.uasset",
        Root + "Landscape_B_1_sharedassets/Forest_Continental_01_LayerInfo.uasset",
        // outside the world folder: ignored
        "SCUM/Content/ConZ_Files/Maps/Menus/MainMenu.umap",
        "SCUM/Content/ConZ_Files/Models/Buildings/Outpost/SM_Wall.uasset",
    ];

    private static WorldIndex Sample() => WorldIndex.Build(SampleFiles);

    [Theory]
    [InlineData("The_Island", WorldPackageKind.Persistent, null)]
    [InlineData("A_0_Outpost", WorldPackageKind.Poi, "A_0")]
    [InlineData("A_0_Outpost_Ext_Armory", WorldPackageKind.Poi, "A_0")]
    [InlineData("D_4_Samobor_01_Block_02_Interior_01", WorldPackageKind.Poi, "D_4")]
    [InlineData("B_3_Mirkovci_ProceduralFoliageBlockers", WorldPackageKind.Poi, "B_3")]
    [InlineData("Z_0_Stone_Quarry", WorldPackageKind.Poi, "Z_0")]
    [InlineData("D_1_Halloween_Hut", WorldPackageKind.Poi, "D_1")]
    [InlineData("Landscape_A_0_1", WorldPackageKind.Landscape, "A_0")]
    [InlineData("Landscape_D_3_4d", WorldPackageKind.Landscape, "D_3")]
    [InlineData("Landscape_Z_4_2", WorldPackageKind.Landscape, "Z_4")]
    [InlineData("TV_Base_B_2", WorldPackageKind.TvBase, "B_2")]
    [InlineData("TV_Base_B_2_Research_Facility_Area_A", WorldPackageKind.TvBase, "B_2")]
    [InlineData("TV_Base_Z_1_Research_Facility_Audio", WorldPackageKind.TvBase, "Z_1")]
    [InlineData("B2_TV_Base_Research_Facility_Entrance", WorldPackageKind.TvBase, "B_2")]
    [InlineData("C_0_AbandonedCity_00", WorldPackageKind.Pripyat, "C_0")]
    [InlineData("C_0_AbandonedCity_01a_PalaceOfEnergetik_Int", WorldPackageKind.Pripyat, "C_0")]
    [InlineData("A_0_Outpost_Airfield_BuiltData", WorldPackageKind.BuiltData, "A_0")]
    [InlineData("Landscape_A_0_1c_BuiltData", WorldPackageKind.BuiltData, "A_0")]
    [InlineData("C_0_AbandonedCity_00_BuiltData", WorldPackageKind.BuiltData, "C_0")]
    [InlineData("A_0_Outpost_Exterior_0_HLOD", WorldPackageKind.Hlod, "A_0")]
    [InlineData("AquaticVolumes", WorldPackageKind.Misc, null)]
    [InlineData("Threat_Zones", WorldPackageKind.Misc, null)]
    [InlineData("Safe_Zone_D_2", WorldPackageKind.Misc, "D_2")]
    [InlineData("B3_Castle_Ruins", WorldPackageKind.Misc, "B_3")]
    [InlineData("D4_Airport_Zeljava", WorldPackageKind.Misc, "D_4")]
    [InlineData("The_Island_LevelStaticData", WorldPackageKind.Misc, null)]
    [InlineData("Asphalt_LayerInfo", WorldPackageKind.Misc, null)]
    public void ClassifiesRealisticNames(string name, WorldPackageKind kind, string? cell)
    {
        var entry = Sample().Find(name);
        Assert.NotNull(entry);
        Assert.Equal(kind, entry.Kind);
        Assert.Equal(cell, entry.Cell?.ToString());
    }

    [Fact]
    public void BuildsPathsFoldersAndCounts()
    {
        var index = Sample();

        // .uexp/.ubulk companions and files outside the world folder are ignored.
        Assert.Equal(55, index.Packages.Count);
        Assert.Null(index.Find("MainMenu"));
        Assert.Null(index.Find("SM_Wall"));

        var counts = index.CountByKind();
        Assert.Equal(1, counts[WorldPackageKind.Persistent]);
        Assert.Equal(22, counts[WorldPackageKind.Poi]);
        Assert.Equal(5, counts[WorldPackageKind.Landscape]);
        Assert.Equal(6, counts[WorldPackageKind.TvBase]);
        Assert.Equal(4, counts[WorldPackageKind.Pripyat]);
        Assert.Equal(4, counts[WorldPackageKind.BuiltData]);
        Assert.Equal(2, counts[WorldPackageKind.Hlod]);
        Assert.Equal(11, counts[WorldPackageKind.Misc]);
        Assert.Equal(45, index.Sublevels.Count());

        var persistent = index.PersistentLevel;
        Assert.NotNull(persistent);
        Assert.Equal(WorldIndex.PersistentLevelPackagePath, persistent.PackagePath);
        Assert.Equal(Root + "The_Island.umap", persistent.FilePath);
        Assert.False(persistent.IsSublevel);

        var pripyat = index.Find("C_0_AbandonedCity_00")!;
        Assert.Equal("AbandonedCity_PripyatLike", pripyat.Folder);
        Assert.Equal("/Game/ConZ_Files/Maps/The_Island/AbandonedCity_PripyatLike/C_0_AbandonedCity_00", pripyat.PackagePath);
        Assert.True(pripyat.IsMap);

        var layerInfo = index.Find("Asphalt_LayerInfo")!;
        Assert.False(layerInfo.IsMap);
        Assert.Equal("The_Island_sharedassets", layerInfo.Folder);
    }

    [Fact]
    public void ParsesLandscapeQuadrantsAndOwners()
    {
        var index = Sample();
        var tile = index.Find("Landscape_A_0_1c")!;
        Assert.Equal(1, tile.LandscapeQuadrant);
        Assert.Equal('c', tile.LandscapeVariant);
        Assert.Equal("/Game/ConZ_Files/Maps/The_Island/Landscape_A_0_1c_BuiltData", tile.BuiltDataPackage);

        var baseTile = index.Find("Landscape_Z_4_2")!;
        Assert.Equal(2, baseTile.LandscapeQuadrant);
        Assert.Null(baseTile.LandscapeVariant);
        Assert.Null(baseTile.BuiltDataPackage);

        Assert.Equal("A_0_Outpost_Exterior", index.Find("A_0_Outpost_Exterior_0_HLOD")!.Owner);
        Assert.Equal("A_0_Outpost_Airfield", index.Find("A_0_Outpost_Airfield_BuiltData")!.Owner);
        Assert.Equal("/Game/ConZ_Files/Maps/The_Island/A_0_Outpost_Airfield_BuiltData", index.Find("A_0_Outpost_Airfield")!.BuiltDataPackage);
        Assert.Equal(
            "/Game/ConZ_Files/Maps/The_Island/AbandonedCity_PripyatLike/C_0_AbandonedCity_00_BuiltData",
            index.Find("C_0_AbandonedCity_00")!.BuiltDataPackage);
    }

    [Fact]
    public void FiltersByCellAndFindsByAnyPathSpelling()
    {
        var index = Sample();
        var a0 = index.InCell(new MapCell('A', 0));
        Assert.Contains(a0, p => p.Name == "A_0_Outpost");
        Assert.Contains(a0, p => p.Name == "Landscape_A_0_1b");
        Assert.Contains(a0, p => p.Kind == WorldPackageKind.Hlod);
        Assert.DoesNotContain(a0, p => p.Name == "A_1_Apatija");
        Assert.All(a0, p => Assert.Equal("A_0", p.Cell.ToString()));

        var byCell = index.SublevelCountByCell();
        Assert.Equal(13, byCell[new MapCell('A', 0)]);

        Assert.Same(index.Find("A_0_Outpost"), index.Find("a_0_outpost"));
        Assert.Same(index.Find("A_0_Outpost"), index.Find("/Game/ConZ_Files/Maps/The_Island/A_0_Outpost"));
        Assert.Same(index.Find("A_0_Outpost"), index.Find("/Game/ConZ_Files/Maps/The_Island/A_0_Outpost.A_0_Outpost"));
        Assert.Same(index.Find("A_0_Outpost"), index.Find(Root + "A_0_Outpost.umap"));
        Assert.Same(index.Find("A_0_Outpost"), index.Find("A_0_Outpost.umap"));
        Assert.Null(index.Find("A_0_Nowhere"));
        Assert.Equal(index.Packages.Count(p => p.Kind == WorldPackageKind.TvBase), index.OfKind(WorldPackageKind.TvBase).Count);
    }

    [Fact]
    public void NamesOnlyIndexHasNoStreamingInformation()
    {
        var index = Sample();
        Assert.Null(index.CrossCheck);
        Assert.All(index.Packages, p => Assert.Null(p.IsStreamed));
    }

    [Fact]
    public void CrossChecksPersistentLevelStreamingLevels()
    {
        var index = Sample();
        var notStreamed = new[] { "D_1_Kirin", "C_0_AbandonedCity_00_Lighting" };
        var streamed = index.Sublevels.Where(p => !notStreamed.Contains(p.Name)).Select(p => p.PackagePath)
            .Append("/Game/ConZ_Files/Maps/The_Island/A_0_Not_In_Source")
            .ToList();
        var reader = new FakeLevelReader().AddStreaming(WorldIndex.PersistentLevelPackagePath, streamed);

        var checkedIndex = index.TryCrossCheck(reader);

        var check = checkedIndex.CrossCheck;
        Assert.NotNull(check);
        Assert.Equal(streamed.Count, check.StreamingLevelCount);
        Assert.Equal(streamed.Count - 1, check.Matched);
        Assert.Equal("A_0_Not_In_Source", Assert.Single(check.MissingPackages).LevelName);
        Assert.Equal(notStreamed.Order(), check.NotStreamed.Select(p => p.Name).Order());
        Assert.False(check.IsConsistent);
        Assert.True(checkedIndex.Find("A_0_Outpost")!.IsStreamed);
        Assert.False(checkedIndex.Find("D_1_Kirin")!.IsStreamed);
        Assert.Null(checkedIndex.Find("The_Island")!.IsStreamed);
        Assert.Null(checkedIndex.Find("A_0_Outpost_Airfield_BuiltData")!.IsStreamed);
    }

    [Fact]
    public void CrossCheckIsSkippedWithoutPersistentLevelOrOnFailure()
    {
        var withoutPersistent = WorldIndex.Build(SampleFiles.Where(f => !f.EndsWith("The_Island.umap", StringComparison.Ordinal)));
        Assert.Null(withoutPersistent.PersistentLevel);
        var reader = new FakeLevelReader();
        Assert.Same(withoutPersistent, withoutPersistent.TryCrossCheck(reader));

        var index = Sample();
        reader.StreamingFailure = new InvalidDataException("broken");
        Assert.Same(index, index.TryCrossCheck(reader));
        Assert.Same(index, index.TryCrossCheck(null));
    }

    [Fact]
    public void BuildsFromFileSource()
    {
        var source = new ListFileSource(SampleFiles);
        var index = WorldIndex.FromFileSource(source);
        Assert.Equal(Sample().Packages.Select(p => p.PackagePath), index.Packages.Select(p => p.PackagePath));
    }

    [Fact]
    public void ParseCellHandlesAllForms()
    {
        Assert.Equal(new MapCell('B', 3), WorldNameParser.ParseCell("B3_Castle_Ruins"));
        Assert.Equal(new MapCell('D', 2), WorldNameParser.ParseCell("Safe_Zone_D_2"));
        Assert.Equal(new MapCell('Z', 1), WorldNameParser.ParseCell("TV_Base_Z_1"));
        Assert.Equal(new MapCell('C', 4), WorldNameParser.ParseCell("Landscape_C_4_3b"));
        Assert.Null(WorldNameParser.ParseCell("Biomes"));
        Assert.Null(WorldNameParser.ParseCell("E_0_NotACell"));
        Assert.Null(WorldNameParser.ParseCell("A_5_OutOfRange"));
        Assert.Null(WorldNameParser.Classify(Root + "A_0_Outpost.uexp"));
        Assert.Null(WorldNameParser.Classify("SCUM/Content/Other/A_0_Outpost.umap"));
    }

    private sealed class ListFileSource(IEnumerable<string> files) : IFileSource
    {
        private readonly string[] _files = files.Select(VirtualPath.Normalize).ToArray();

        public string DisplayName => "list";

        public bool Exists(string virtualPath) => _files.Contains(VirtualPath.Normalize(virtualPath), VirtualPath.Comparer);

        public bool TryGetBytes(string virtualPath, [NotNullWhen(true)] out byte[]? data)
        {
            data = Exists(virtualPath) ? [] : null;
            return data is not null;
        }

        public Task<byte[]?> ReadBytesAsync(string virtualPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Exists(virtualPath) ? Array.Empty<byte>() : null);

        public IEnumerable<string> EnumerateFiles(string virtualDirectory = "", bool recursive = true) =>
            _files.Where(f => VirtualPath.IsUnder(f, virtualDirectory)
                              && (recursive || VirtualPath.GetDirectory(f).Equals(VirtualPath.Normalize(virtualDirectory), StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed class MapCellTests
{
    [Theory]
    [InlineData("A_0", 'A', 0)]
    [InlineData("a0", 'A', 0)]
    [InlineData("Z-4", 'Z', 4)]
    [InlineData(" d_3 ", 'D', 3)]
    [InlineData("B 2", 'B', 2)]
    public void ParsesSpellings(string text, char column, int row)
    {
        Assert.True(MapCell.TryParse(text, out var cell));
        Assert.Equal(new MapCell(column, row), cell);
        Assert.Equal($"{column}_{row}", cell.ToString());
        Assert.True(cell.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("E_0")]
    [InlineData("A_5")]
    [InlineData("A_10")]
    [InlineData("AA")]
    [InlineData(null)]
    public void RejectsInvalid(string? text)
    {
        Assert.False(MapCell.TryParse(text, out _));
        if (text is not null)
        {
            Assert.Throws<FormatException>(() => MapCell.Parse(text));
        }
    }

    [Fact]
    public void EnumeratesAndSortsTheTwentyFiveCells()
    {
        var all = MapCell.All.ToList();
        Assert.Equal(25, all.Count);
        Assert.Equal("A_0", all[0].ToString());
        Assert.Equal("Z_4", all[^1].ToString());
        Assert.Equal(all, all.Order().ToList());
    }
}
