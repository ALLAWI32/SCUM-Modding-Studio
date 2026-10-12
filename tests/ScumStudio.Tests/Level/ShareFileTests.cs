using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Crafting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static ScumStudio.Tests.Level.EditOpTests;

namespace ScumStudio.Tests.Level;

public sealed class ShareFileTests
{
    internal const string Crate = "/Game/ConZ_Files/Models/Props/SM_Crate.SM_Crate";

    /// <summary>Set to 1 to write <c>Fixtures/Share/sample-share.zip</c> again from <see cref="Sample"/>.</summary>
    private const string WriteFixtureVariable = "SCUMSTUDIO_WRITE_SHARE_FIXTURE";

    private static readonly string[] Whitelist = ["manifest.json", "edits.json", "files/craftables.json", "files/EconomyOverride.json", "preview.jpg", "thumb.jpg"];

    [Fact]
    public void WriteThenReadRoundTrips()
    {
        var economy = EconomyOverride.CreateDefault();
        economy.Set("A_0_Armory", new TradeableOverride("Weapon_AK47") { PurchasePrice = 500 });
        var files = new Dictionary<string, byte[]>
        {
            [CraftablesFile.FileName] = Encoding.UTF8.GetBytes(new CraftablesFile { Items = [Table()] }.ToJson()),
            [EconomyOverride.FileName] = Encoding.UTF8.GetBytes(economy.ToJson()),
        };
        var content = Sample(files);

        var zip = ShareFile.Write(content);
        var back = ShareFile.Read(zip);

        Assert.Equal(Whitelist.Order(StringComparer.Ordinal), Entries(zip).Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(content.Edits, back.Edits);
        Assert.Equal("Crate camp", back.Manifest.Name);
        Assert.Equal("Tester", back.Manifest.Author);
        Assert.Equal(content.Manifest.Description, back.Manifest.Description);
        Assert.Equal(["A_0"], back.Manifest.Sectors);
        Assert.Equal("1.0.0.0", back.Manifest.GameBuild);
        Assert.Equal(content.Manifest.Created, back.Manifest.Created);
        Assert.Equal(3, back.Manifest.Edits);
        Assert.Equal(3, back.Manifest.Counts["A_0"]);
        Assert.Equal([EconomyOverride.FileName, CraftablesFile.FileName], back.Manifest.Files);
        Assert.Equal(files[CraftablesFile.FileName], back.Files[CraftablesFile.FileName]);
        Assert.Equal(500, EconomyOverride.Parse(Encoding.UTF8.GetString(back.Files[EconomyOverride.FileName])).Find("A_0_Armory", "Weapon_AK47")!.PurchasePrice);
        Assert.Equal(content.Preview, back.Preview);
        Assert.Equal(content.Thumbnail, back.Thumbnail);

        // The pictures are stored as they are (the server hands them out without unpacking), the JSON compressed.
        Assert.True(zip.AsSpan().IndexOf(content.Preview) > 0);
        Assert.True(zip.AsSpan().IndexOf(content.Thumbnail) > 0);
        Assert.True(zip.AsSpan().IndexOf("\"op\":\"addStaticMeshActor\""u8) < 0);
        Assert.Equal(zip, ShareFile.Write(content));
    }

    [Fact]
    public void NoGameDataOrPrivateDataLeavesThePc()
    {
        using var temp = new LevelTempDirectory();
        var paks = temp.Combine("Steam", "SCUM", "Content", "Paks");
        using var project = Project.Create(temp.Combine("Mod.ssproj"), "Mod", "1.0.0.0", [new ProjectSource(paks)], ManualClock.At2026());
        var mod = Path.Combine(project.DirectoryPath, ProjectMods.FolderName, "HektorMap", "SCUM", "Content", "Maps");
        Directory.CreateDirectory(mod);
        var package = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(package, 0x9E2A83C1);
        File.WriteAllBytes(Path.Combine(mod, "A_0_Outpost.umap"), package);
        new CraftablesFile { Items = [Table(), Table() with { Name = "Imported model", Imported = "imports/SS_Imported_model" }] }.Save(project.DirectoryPath);
        foreach (var op in SampleEdits().Ops.Append(new DeleteActorOp(new ActorRef(JournalSliceTests.Farm, "Barn_2"))))
        {
            project.Apply(op);
        }

        var economy = EconomyOverride.CreateDefault();
        economy.Set("A_0_Armory", new TradeableOverride("Weapon_AK47") { PurchasePrice = 500 });
        economy.Set("B_1_Trader", new TradeableOverride("Apple") { SellPrice = 1 });

        var content = ShareFile.Collect(project, JournalSliceTests.Choose("A_0"), SampleManifest(), Jpeg(), Jpeg(8, 5), economy, craftables: true, out var pulledIn, out var leftOut);
        var zip = ShareFile.Write(content);

        Assert.Empty(pulledIn);
        Assert.Equal(1, leftOut);
        Assert.Equal(3, content.Edits.Ops.Count);
        var entries = Entries(zip);
        Assert.All(entries, e => Assert.Contains(e.Name, Whitelist));
        Assert.Contains(entries, e => e.Name == "files/EconomyOverride.json");
        var secrets = new[] { project.DirectoryPath, temp.Path, paks, "Imported model", "HektorMap", "B_1_Trader" }
            .Concat(Environment.UserName is { Length: >= 3 } user ? [user] : []).ToList();
        foreach (var (name, data) in entries)
        {
            Assert.False(data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 0x9E2A83C1, name);
            Assert.True(data.AsSpan().IndexOf(BitConverter.GetBytes(0x5A6F12E1u)) < 0, name);
            var text = Encoding.UTF8.GetString(data);
            Assert.All(secrets, s => Assert.DoesNotContain(s, text, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(temp.Path.Replace('\\', '/'), text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(["A_0_Armory"], EconomyOverride.Parse(Encoding.UTF8.GetString(ShareFile.Read(zip).Files[EconomyOverride.FileName])).Traders);
        Assert.Equal(ShareProblem.BadManifest, Refused(() => ShareFile.Collect(project, JournalSliceTests.Choose("D_4"), SampleManifest(), Jpeg(), Jpeg(), null, false, out _, out _)));

        // What Write is handed is checked too: an imported model, a game file, a pak inside a picture.
        var imported = new CraftablesFile { Items = [Table() with { Imported = "imports/SS_Table" }] }.ToJson();
        Assert.Equal(ShareProblem.GameData, Refused(() => ShareFile.Write(Sample(new Dictionary<string, byte[]> { [CraftablesFile.FileName] = Encoding.UTF8.GetBytes(imported) }))));
        Assert.Equal(ShareProblem.GameData, Refused(() => ShareFile.Write(Sample(new Dictionary<string, byte[]> { ["A_0_Outpost.umap"] = package }))));
        var pakInPicture = Jpeg().Concat(BitConverter.GetBytes(0x5A6F12E1u)).ToArray();
        Assert.Equal(ShareProblem.GameData, Refused(() => ShareFile.Write(Sample() with { Preview = pakInPicture })));
        Assert.Equal(ShareProblem.BadPicture, Refused(() => ShareFile.Write(Sample() with { Preview = package })));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/x")]
    [InlineData("C:x")]
    [InlineData("a\\b")]
    [InlineData("files/../manifest.json")]
    public void AZipWithAPathOutsideIsRefused(string name)
    {
        var zip = Zip([.. Entries(ShareFile.Write(Sample())), (name, "{}"u8.ToArray())]);

        Assert.Equal(ShareProblem.UnsafeEntry, Refused(() => ShareFile.Read(zip)));
    }

    [Theory]
    [InlineData("/Game/../../Users/Public/evil")]
    [InlineData("C:/Windows/evil")]
    [InlineData("/Game/Maps//A_0_Outpost")]
    public void AnEditOutsideTheGameContentIsRefused(string level)
    {
        var edits = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize<EditOp>(
            new BatchOp("x", [new DeleteActorOp(new ActorRef(Outpost, "A")), new DeleteActorOp(new ActorRef(level, "B")), new DeleteActorOp(new ActorRef(Outpost, "C"))]),
            ScumStudio.Level.Serialization.LevelJson.Compact));
        var zip = Zip(Entries(ShareFile.Write(Sample())).Select(e => e.Name == "edits.json" ? (e.Name, edits) : e));

        Assert.Equal(ShareProblem.UnsafeEntry, Refused(() => ShareFile.Read(zip)));
    }

    [Fact]
    public void ADuplicateAnUnknownOrAGameFileEntryIsRefused()
    {
        var entries = Entries(ShareFile.Write(Sample()));

        Assert.Equal(ShareProblem.UnsafeEntry, Refused(() => ShareFile.Read(Zip([.. entries, ("MANIFEST.json", entries[0].Data)]))));
        Assert.Equal(ShareProblem.UnsafeEntry, Refused(() => ShareFile.Read(Zip([.. entries, ("notes.md", "x"u8.ToArray())]))));
        Assert.Equal(ShareProblem.GameData, Refused(() => ShareFile.Read(Zip([.. entries, ("files/A_0_Outpost.uexp", [1, 2, 3])]))));
        Assert.Equal(ShareProblem.BadZip, Refused(() => ShareFile.Read("PK not a zip"u8.ToArray())));
    }

    [Fact]
    public void AZipBombIsRefused()
    {
        var entries = Entries(ShareFile.Write(Sample()));
        var spaces = new byte[17 * 1024 * 1024];
        Array.Fill(spaces, (byte)' ');

        // Honest about its size: 17 MiB of edits, past the 16 MiB a JSON file may unpack to.
        var big = Zip([.. entries.Where(e => e.Name != "edits.json"), ("edits.json", spaces)]);
        Assert.True(big.Length < ShareFile.MaxZipBytes);
        Assert.Equal(ShareProblem.TooLarge, Refused(() => ShareFile.Read(big)));

        // Lying about its size: the picture says 100 bytes and holds 1 MiB. .NET 8 reads a stored entry to its real end,
        // where the reader's own cap stops it; newer runtimes refuse the length mismatch themselves.
        var picture = new byte[1024 * 1024];
        picture[0] = 0xFF;
        picture[1] = 0xD8;
        picture[2] = 0xFF;
        var lying = Zip([.. entries.Where(e => e.Name != "preview.jpg"), ("preview.jpg", picture)]);
        StateSize(lying, "preview.jpg", 100);
        Assert.Contains(Refused(() => ShareFile.Read(lying)), new[] { ShareProblem.TooLarge, ShareProblem.BadZip });

        // Two JSON files of 13 MiB each: each under its cap, together past the 24 MiB a package may unpack to.
        var thirteen = spaces[..(13 * 1024 * 1024)];
        var total = Zip([.. entries.Where(e => e.Name is not ("edits.json" or "manifest.json")), ("manifest.json", thirteen), ("edits.json", thirteen)]);
        Assert.Equal(ShareProblem.TooLarge, Refused(() => ShareFile.Read(total)));

        var many = Zip([.. entries, .. Enumerable.Range(1, ShareFile.MaxEntries + 1 - entries.Count).Select(i => ($"extra{i}.json", "{}"u8.ToArray()))]);
        Assert.Equal(ShareProblem.TooLarge, Refused(() => ShareFile.Read(many)));
        Assert.Equal(ShareProblem.TooLarge, Refused(() => ShareFile.Read(new byte[ShareFile.MaxZipBytes + 1])));
    }

    [Fact]
    public void AnUnknownEditNeedsANewerApp()
    {
        var entries = Entries(ShareFile.Write(Sample()));
        var edits = """{"op":"batch","title":"Forest","ops":[{"op":"deleteActor","target":{"level":"/Game/A_0_X","actor":"Y"}},{"op":"plantForest","trees":3}]}"""u8.ToArray();
        var zip = Zip([.. entries.Select(e => e.Name == "edits.json" ? (e.Name, edits) : e)]);

        Assert.Equal(ShareProblem.UnknownEdit, Refused(() => ShareFile.Read(zip)));
    }

    [Fact]
    public void ANewerFormatIsRefused()
    {
        Assert.Equal(ShareProblem.NewerFormat, Refused(() => ShareFile.Read(WithManifest(SampleManifest() with { Format = "scumstudio.share/2" }))));
        Assert.Equal(ShareProblem.BadManifest, Refused(() => ShareFile.Read(WithManifest(SampleManifest() with { Format = "scumstudio.prefab/1" }))));
        Assert.Equal(ShareProblem.NewerFormat, Refused(() => ShareFile.Write(Sample() with { Manifest = SampleManifest() with { Format = "scumstudio.share/3" } })));
    }

    [Fact]
    public void ADescriptionOver600IsRefused()
    {
        var longest = new string('x', ShareFile.MaxDescription);

        Assert.Equal(longest, ShareFile.Read(ShareFile.Write(Sample() with { Manifest = SampleManifest() with { Description = longest } })).Manifest.Description);
        Assert.Equal(ShareProblem.BadManifest, Refused(() => ShareFile.Write(Sample() with { Manifest = SampleManifest() with { Description = longest + "x" } })));
        Assert.Equal(ShareProblem.BadManifest, Refused(() => ShareFile.Read(WithManifest(SampleManifest() with { Description = longest + "x" }))));
        Assert.Equal(ShareProblem.BadManifest, Refused(() => ShareFile.Write(Sample() with { Manifest = SampleManifest() with { Name = new string('n', ShareFile.MaxName + 1) } })));
        Assert.Equal(ShareProblem.BadManifest, Refused(() => ShareFile.Write(Sample() with { Manifest = SampleManifest() with { Author = new string('a', ShareFile.MaxAuthor + 1) } })));
        Assert.Equal(ShareProblem.BadManifest, Refused(() => ShareFile.Write(Sample() with { Manifest = SampleManifest() with { Name = " " } })));
    }

    [Fact]
    public void TheSampleFixtureReads()
    {
        var path = FixturePath();
        if (Environment.GetEnvironmentVariable(WriteFixtureVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, ShareFile.Write(Sample()));
        }

        var content = ShareFile.Read(File.ReadAllBytes(path));

        Assert.Equal(SampleManifest().Name, content.Manifest.Name);
        Assert.Equal(["A_0"], content.Manifest.Sectors);
        Assert.Equal(SampleEdits().Ops, content.Edits.Ops);
        Assert.Equal(16, Image.Identify(content.Preview).Width);
        Assert.Equal(9, Image.Identify(content.Preview).Height);
        Assert.Empty(content.Files);
    }

    internal static byte[] Jpeg(int width = 16, int height = 9)
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24(60, 110, 70));
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        return stream.ToArray();
    }

    internal static Craftable Table() => new() { Name = "Table", Source = "/Game/ConZ_Files/Models/Furniture/SM_Table", Mesh = "/Game/ConZ_Files/Models/Furniture/SM_Table" };

    internal static BatchOp SampleEdits() => new("Crate camp",
    [
        new AddStaticMeshActorOp(Outpost, "SM_Crate_Added", Crate, Somewhere),
        new SetTransformOp(new ActorRef(Outpost, "StaticMeshActor_7"), TransformValue.Identity, Elsewhere),
        new DeleteActorOp(new ActorRef(Outpost, "StaticMeshActor_12")),
    ]);

    internal static ShareManifest SampleManifest() => new()
    {
        Name = "Crate camp",
        Author = "Tester",
        Description = "Three crates by the outpost gate.",
        Sectors = ["A_0"],
        AppVersion = "3.1.0",
        GameBuild = "1.0.0.0",
        Created = new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.Zero),
    };

    internal static ShareContent Sample(IReadOnlyDictionary<string, byte[]>? files = null) =>
        new(SampleManifest(), SampleEdits(), files ?? new Dictionary<string, byte[]>(), Jpeg(), Jpeg(8, 5));

    internal static ShareProblem Refused(Func<object> action) => Assert.Throws<ShareException>(action).Problem;

    private static string FixturePath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "Fixtures", "Share", "sample-share.zip");

    private static List<(string Name, byte[] Data)> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.Select(e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return (e.FullName, copy.ToArray());
        }).ToList();
    }

    private static byte[] Zip(IEnumerable<(string Name, byte[] Data)> entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = archive.CreateEntry(name, name.EndsWith(".jpg", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                stream.Write(data);
            }
        }

        return memory.ToArray();
    }

    private static byte[] WithManifest(ShareManifest manifest)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest with { Edits = 3 }, ScumStudio.Level.Serialization.LevelJson.Indented);
        return Zip(Entries(ShareFile.Write(Sample())).Select(e => e.Name == "manifest.json" ? (e.Name, json) : e));
    }

    /// <summary>Overwrites the unpacked size an entry states, in its local header and in the central directory.</summary>
    private static void StateSize(byte[] zip, string name, uint size)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        for (var i = 0; i + 46 < zip.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i));
            var (nameAt, sizeAt) = signature switch
            {
                0x04034b50 => (30, 22),
                0x02014b50 => (46, 24),
                _ => (0, 0),
            };
            var nameLength = nameAt == 0 ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(i + nameAt - (nameAt == 30 ? 4 : 18)));
            if (nameAt > 0 && nameLength == nameBytes.Length && i + nameAt + nameLength <= zip.Length && zip.AsSpan(i + nameAt, nameLength).SequenceEqual(nameBytes))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + sizeAt), size);
            }
        }
    }
}
