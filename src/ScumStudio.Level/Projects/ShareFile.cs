using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Serialization;
using ScumStudio.Level.World;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Level.Projects;

/// <summary>A shared mod in memory: its manifest, its edits as one batch, the project files it carries and its pictures.</summary>
/// <param name="Manifest">What it is (see <see cref="ShareManifest"/>).</param>
/// <param name="Edits">The edits (see <see cref="JournalSlice.Take"/>).</param>
/// <param name="Files">Project files by name (<see cref="ShareFile.CarriedFiles"/>).</param>
/// <param name="Preview">The picture, JPEG.</param>
/// <param name="Thumbnail">The gallery's small picture, JPEG.</param>
public sealed record ShareContent(ShareManifest Manifest, BatchOp Edits, IReadOnlyDictionary<string, byte[]> Files, byte[] Preview, byte[] Thumbnail);

/// <summary>
/// The package of a shared mod (owner: "share your mod"): a small zip of the project's own edits, never a game file. It holds
/// exactly <c>manifest.json</c>, <c>edits.json</c>, <c>files/craftables.json</c> and <c>files/EconomyOverride.json</c> when
/// carried, <c>preview.jpg</c> and <c>thumb.jpg</c> (the pictures stored uncompressed, so the server can hand them out
/// without unpacking). Reading never touches the disk and trusts nothing the zip says about itself: names, counts and
/// sizes are checked against the limits below on what is actually read.
/// </summary>
public static class ShareFile
{
    /// <summary>Largest package, bytes.</summary>
    public const int MaxZipBytes = 3 * 1024 * 1024;

    /// <summary>Longest mod name, characters.</summary>
    public const int MaxName = 60;

    /// <summary>Longest maker name, characters.</summary>
    public const int MaxAuthor = 32;

    /// <summary>Longest description, characters.</summary>
    public const int MaxDescription = 600;

    /// <summary>Most files in a package.</summary>
    public const int MaxEntries = 8;

    /// <summary>Largest JSON file once unpacked, bytes.</summary>
    public const int MaxJsonBytes = 16 * 1024 * 1024;

    /// <summary>Largest picture, bytes.</summary>
    public const int MaxPreviewBytes = 800 * 1024;

    /// <summary>Largest gallery picture, bytes.</summary>
    public const int MaxThumbnailBytes = 120 * 1024;

    /// <summary>Most bytes unpacked from one package.</summary>
    public const int MaxTotalBytes = 24 * 1024 * 1024;

    /// <summary>Name of the manifest in the zip.</summary>
    public const string ManifestEntry = "manifest.json";

    /// <summary>Name of the edits in the zip.</summary>
    public const string EditsEntry = "edits.json";

    /// <summary>Name of the picture in the zip.</summary>
    public const string PreviewEntry = "preview.jpg";

    /// <summary>Name of the gallery picture in the zip.</summary>
    public const string ThumbnailEntry = "thumb.jpg";

    private const string FilesFolder = "files/";

    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] PakExtensions = [".pak", ".sig", ".utoc", ".ucas"];

    private static readonly byte[] PakMagic = BitConverter.GetBytes(PakFormat.Magic);

    private static readonly HashSet<string> KnownOps = typeof(EditOp).GetCustomAttributes<JsonDerivedTypeAttribute>()
        .Select(a => a.TypeDiscriminator as string).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>The project files a package may carry, by name.</summary>
    public static IReadOnlySet<string> CarriedFiles { get; } = new HashSet<string>(StringComparer.Ordinal) { CraftablesFile.FileName, EconomyOverride.FileName };

    /// <summary>
    /// The share of <paramref name="project"/>'s edits in <paramref name="choice"/> (see <see cref="JournalSlice.Take"/>),
    /// with its craftables when <paramref name="craftables"/> is set (those made from an imported 3D model stay home: they
    /// are cooked game files) and the economy sections of the traders in the chosen cells and of the traders it places.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="choice">The cells, the island-wide levels and the assets.</param>
    /// <param name="info">Name, maker, description, app version and time; the rest is filled in.</param>
    /// <param name="preview">The picture, JPEG.</param>
    /// <param name="thumbnail">The gallery picture, JPEG.</param>
    /// <param name="economy">The project's economy as the app holds it, or null.</param>
    /// <param name="craftables">True to carry the project's craftables.</param>
    /// <param name="pulledIn">Groups added because the chosen edits copy something they made.</param>
    /// <param name="craftablesLeftOut">Craftables left out because they are imported models.</param>
    /// <exception cref="ShareException">
    /// <see cref="ShareProblem.BadManifest"/>: the choice holds no edits; <see cref="ShareProblem.Conflicts"/>: its edits do
    /// not apply on their own (the detail says which).
    /// </exception>
    public static ShareContent Collect(
        Project project, SectorChoice choice, ShareManifest info, byte[] preview, byte[] thumbnail, EconomyOverride? economy, bool craftables,
        out IReadOnlyList<string> pulledIn, out int craftablesLeftOut)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(info);
        BatchOp edits;
        try
        {
            edits = JournalSlice.Take(project.Journal.Applied, choice, string.IsNullOrWhiteSpace(info.Name) ? "Shared mod" : info.Name, out pulledIn);
        }
        catch (InvalidDataException ex)
        {
            var empty = !JournalSlice.Groups(project.Journal.Applied).Any(g => choice.Sectors.Any(s => MapCell.TryParse(s, out var cell) && cell.ToString() == g.Key)
                || (choice.IslandWide && g.Key == JournalSlice.IslandKey) || (choice.Assets && g.Key == JournalSlice.AssetsKey));
            throw new ShareException(empty ? ShareProblem.BadManifest : ShareProblem.Conflicts, ex.Message);
        }

        var sectors = choice.Sectors.Select(s => MapCell.TryParse(s, out var cell) ? cell.ToString() : s).Concat(pulledIn)
            .Where(k => k is not (JournalSlice.IslandKey or JournalSlice.AssetsKey)).Distinct(StringComparer.Ordinal)
            .OrderBy(JournalSlice.Order).ToList();
        var islandWide = choice.IslandWide || pulledIn.Contains(JournalSlice.IslandKey);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        craftablesLeftOut = 0;
        if (craftables)
        {
            var (json, leftOut) = CraftablesFor(CraftablesFile.Load(project.DirectoryPath));
            craftablesLeftOut = leftOut;
            if (json is not null)
            {
                files[CraftablesFile.FileName] = json;
            }
        }

        if (economy is not null && EconomyFor(economy, project.State, edits, sectors, islandWide) is { } economyJson)
        {
            files[EconomyOverride.FileName] = economyJson;
        }

        var manifest = info with
        {
            Format = ShareManifest.FormatId,
            Sectors = sectors,
            IslandWide = islandWide,
            Assets = choice.Assets || pulledIn.Contains(JournalSlice.AssetsKey),
            Files = files.Keys.Order(StringComparer.Ordinal).ToList(),
            GameBuild = project.Manifest.GameBuild,
            Counts = JournalSlice.Count(edits.Ops),
            Edits = edits.Ops.Count,
        };
        return new ShareContent(manifest, edits, files, preview, thumbnail);
    }

    /// <summary><c>craftables.json</c> without the imported models (null when none is left) and how many were left out.</summary>
    public static (byte[]? Json, int LeftOut) CraftablesFor(CraftablesFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var kept = file.Items.Where(c => c.Imported is null).ToList();
        var json = kept.Count == 0 ? null : Utf8NoBom.GetBytes(new CraftablesFile { Items = kept }.ToJson());
        return (json, file.Items.Count - kept.Count);
    }

    /// <summary>
    /// <c>EconomyOverride.json</c> with the sections of the traders <paramref name="edits"/> places and of the game's traders
    /// in <paramref name="sectors"/> (the project's other placed traders stay home); the settings only for the whole
    /// island. Null when nothing is left.
    /// </summary>
    public static byte[]? EconomyFor(EconomyOverride economy, EditState state, BatchOp edits, IReadOnlyCollection<string> sectors, bool islandWide)
    {
        ArgumentNullException.ThrowIfNull(economy);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(sectors);
        var placedHere = PlacedTraders(edits.Ops).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var placedElsewhere = PlacedTraders(state.AddedActors.Values).Where(n => !placedHere.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = economy.Traders.Where(t => placedHere.Contains(t)
            || (!placedElsewhere.Contains(t) && (TraderPosts.CellOf(t) is { } cell ? sectors.Contains(cell, StringComparer.OrdinalIgnoreCase) : islandWide))).ToList();
        if (names.Count == 0 && !islandWide)
        {
            return null;
        }

        var shared = islandWide ? economy.Without(economy.Traders.Except(names, StringComparer.OrdinalIgnoreCase).ToList()) : EconomyOverride.Parse("{\"economy-override\": {}}");
        if (!islandWide)
        {
            foreach (var name in names)
            {
                shared.SetSection(name, economy.Entries(name));
            }
        }

        return Utf8NoBom.GetBytes(shared.ToJson());
    }

    /// <summary>The package's bytes.</summary>
    /// <exception cref="ShareException">A field is empty or too long, a picture is not a JPEG, the content holds game data, or the package is over <see cref="MaxZipBytes"/>.</exception>
    public static byte[] Write(ShareContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var manifest = content.Manifest with
        {
            Files = content.Files.Keys.Order(StringComparer.Ordinal).ToList(),
            Counts = JournalSlice.Count(content.Edits.Ops),
            Edits = content.Edits.Ops.Count,
        };
        if (content.Files.Keys.FirstOrDefault(name => !CarriedFiles.Contains(name)) is { } other)
        {
            throw new ShareException(ShareProblem.GameData, $"{other} is not a project file a shared mod carries.");
        }

        if (manifest.Validate() is { } problem)
        {
            throw new ShareException(problem, "The name, maker, description or a sector of the mod is empty, too long or not valid.");
        }

        if (content.Edits.Ops.Count == 0)
        {
            throw new ShareException(ShareProblem.BadManifest, "The mod has no edits.");
        }

        if (!IsJpeg(content.Preview) || !IsJpeg(content.Thumbnail))
        {
            throw new ShareException(ShareProblem.BadPicture, "The picture is not a JPEG.");
        }

        if (content.Preview.Length > MaxPreviewBytes || content.Thumbnail.Length > MaxThumbnailBytes)
        {
            throw new ShareException(ShareProblem.TooLarge, $"The picture is {content.Preview.Length / 1024} KB (at most {MaxPreviewBytes / 1024} KB; the small one {MaxThumbnailBytes / 1024} KB).");
        }

        var entries = new List<(string Name, byte[] Data, bool Stored)>
        {
            (ManifestEntry, Utf8NoBom.GetBytes(JsonSerializer.Serialize(manifest, LevelJson.Indented)), false),
            (EditsEntry, Utf8NoBom.GetBytes(JsonSerializer.Serialize<EditOp>(content.Edits, LevelJson.Compact)), false),
        };
        foreach (var (name, data) in content.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            CheckFile(name, data);
            entries.Add((FilesFolder + name, data, false));
        }

        entries.Add((PreviewEntry, content.Preview, true));
        entries.Add((ThumbnailEntry, content.Thumbnail, true));
        foreach (var (name, data, _) in entries)
        {
            CheckNoGameData(name, data);
        }

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data, stored) in entries)
            {
                var entry = zip.CreateEntry(name, stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                entry.LastWriteTime = Stamp;
                using var stream = entry.Open();
                stream.Write(data);
            }
        }

        if (memory.Length > MaxZipBytes)
        {
            throw new ShareException(ShareProblem.TooLarge, $"The package is {memory.Length / 1024} KB (at most {MaxZipBytes / 1024} KB).");
        }

        return memory.ToArray();
    }

    /// <summary>Reads a package in memory (nothing is unpacked to disk).</summary>
    /// <exception cref="ShareException">The package is refused; <see cref="ShareException.Problem"/> says why.</exception>
    public static ShareContent Read(byte[] zip)
    {
        ArgumentNullException.ThrowIfNull(zip);
        if (zip.Length > MaxZipBytes)
        {
            throw new ShareException(ShareProblem.TooLarge, $"The package is {zip.Length / 1024} KB (at most {MaxZipBytes / 1024} KB).");
        }

        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxEntries)
            {
                throw new ShareException(ShareProblem.TooLarge, $"The package holds {archive.Entries.Count} files (at most {MaxEntries}).");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (!IsSafeName(name) || !seen.Add(name))
                {
                    throw new ShareException(ShareProblem.UnsafeEntry, $"'{name}' is not a safe file name, or it appears twice.");
                }

                CheckNoGameData(name, []);
                var cap = CapOf(name) ?? throw new ShareException(ShareProblem.UnsafeEntry, $"'{name}' is not a file of a shared mod.");
                var bytes = entry.Length > cap ? null : ReadAtMost(entry, cap);
                total += bytes?.Length ?? 0;
                if (bytes is null || total > MaxTotalBytes)
                {
                    throw new ShareException(ShareProblem.TooLarge, $"'{name}' unpacks to more than {cap / 1024} KB, or the package to more than {MaxTotalBytes / 1024} KB.");
                }

                CheckNoGameData(name, bytes);
                data[name] = bytes;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new ShareException(ShareProblem.BadZip, ex.Message);
        }

        var manifest = ReadManifest(data);
        var edits = ReadEdits(data, manifest);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, bytes) in data.Where(d => d.Key.StartsWith(FilesFolder, StringComparison.Ordinal)))
        {
            files[name[FilesFolder.Length..]] = bytes;
        }

        if (!files.Keys.Order(StringComparer.Ordinal).SequenceEqual(manifest.Files.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new ShareException(ShareProblem.BadManifest, "The files listed in manifest.json are not the files in the package.");
        }

        foreach (var (name, bytes) in files)
        {
            CheckFile(name, bytes);
        }

        var preview = data.GetValueOrDefault(PreviewEntry);
        var thumbnail = data.GetValueOrDefault(ThumbnailEntry);
        if (!IsJpeg(preview) || !IsJpeg(thumbnail))
        {
            throw new ShareException(ShareProblem.BadPicture, "preview.jpg or thumb.jpg is missing or not a JPEG.");
        }

        return new ShareContent(manifest, edits, files, preview!, thumbnail!);
    }

    private static ShareManifest ReadManifest(Dictionary<string, byte[]> data)
    {
        ShareManifest? manifest;
        try
        {
            manifest = data.TryGetValue(ManifestEntry, out var bytes) ? JsonSerializer.Deserialize<ShareManifest>(bytes, LevelJson.Indented) : null;
        }
        catch (JsonException ex)
        {
            throw new ShareException(ShareProblem.BadManifest, "manifest.json is not valid: " + ex.Message);
        }

        if (manifest is null)
        {
            throw new ShareException(ShareProblem.BadManifest, "manifest.json is missing.");
        }

        return manifest.Validate() is { } problem
            ? throw new ShareException(problem, problem == ShareProblem.NewerFormat ? $"Format '{manifest.Format}' needs a newer ScumStudio." : "manifest.json has an empty, too long or invalid field.")
            : manifest;
    }

    private static BatchOp ReadEdits(Dictionary<string, byte[]> data, ShareManifest manifest)
    {
        if (!data.TryGetValue(EditsEntry, out var bytes))
        {
            throw new ShareException(ShareProblem.BadManifest, "edits.json is missing.");
        }

        EditOp? edits;
        try
        {
            using (var document = JsonDocument.Parse(bytes))
            {
                if (UnknownOp(document.RootElement) is { } unknown)
                {
                    throw new ShareException(ShareProblem.UnknownEdit, $"Edit type '{unknown}' needs a newer ScumStudio.");
                }
            }

            edits = JsonSerializer.Deserialize<EditOp>(bytes, LevelJson.Compact);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ShareException(ShareProblem.BadManifest, "edits.json is not valid: " + ex.Message);
        }

        if (edits is not BatchOp { Ops.Count: > 0 } batch || batch.Ops.Count != manifest.Edits)
        {
            throw new ShareException(ShareProblem.BadManifest, "edits.json does not hold the edits manifest.json lists.");
        }

        if (Leaves(batch).FirstOrDefault(o => !EditOpRemap.KnownTypes.Contains(o.GetType())) is { } other)
        {
            throw new ShareException(ShareProblem.UnknownEdit, $"Edit type {other.GetType().Name} cannot be shared by this version.");
        }

        // The export writes the levels and assets the edits name, and a trader's name becomes a package name: none may
        // point outside the game's content.
        var outside = Leaves(batch).SelectMany(o => o.GetTouchedLevels().Concat(o.GetTouchedAssets())).FirstOrDefault(p => !IsGamePath(p))
            ?? Leaves(batch).OfType<AddBlueprintActorOp>().Select(b => b.Trader?.Name).OfType<string>()
                .FirstOrDefault(n => n.Any(c => c is '/' or '\\' or ':' or '.' || char.IsControl(c)));
        if (outside is not null)
        {
            throw new ShareException(ShareProblem.UnsafeEntry, $"An edit names '{outside}', which is not a package of the game's content.");
        }

        return batch;
    }

    private static bool IsGamePath(string path) =>
        path.StartsWith("/Game/", StringComparison.Ordinal) && !path.Contains('\\', StringComparison.Ordinal) && !path.Contains(':', StringComparison.Ordinal)
        && !path.Split('/').Skip(1).Any(part => part is "" or "." or "..") && !path.Any(char.IsControl);

    private static IEnumerable<EditOp> Leaves(EditOp op) => op switch
    {
        BatchOp batch => batch.Ops.SelectMany(Leaves),
        RemoveAddedActorOp remove => [op, .. Leaves(remove.Original)],
        RemoveAddedInstanceOp remove => [op, .. Leaves(remove.Original)],
        RemoveAssetCloneOp remove => [op, .. Leaves(remove.Original)],
        _ => [op],
    };

    /// <summary>The first <c>"op"</c> value this version does not know, anywhere in the document.</summary>
    private static string? UnknownOp(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("op") && property.Value.ValueKind == JsonValueKind.String && !KnownOps.Contains(property.Value.GetString()!))
                    {
                        return property.Value.GetString();
                    }

                    if (UnknownOp(property.Value) is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(UnknownOp).FirstOrDefault(u => u is not null);
            default:
                return null;
        }
    }

    /// <summary>Checks a carried project file: it reads as its kind, and craftables made from imported models are not in it.</summary>
    private static void CheckFile(string name, byte[] bytes)
    {
        try
        {
            if (name == CraftablesFile.FileName && CraftablesFile.Parse(Utf8NoBom.GetString(bytes)).Items.Any(c => c is null || c.Imported is not null))
            {
                throw new ShareException(ShareProblem.GameData, "craftables.json lists an imported 3D model (cooked game files).");
            }

            if (name == EconomyOverride.FileName)
            {
                EconomyOverride.Parse(Utf8NoBom.GetString(bytes));
            }
        }
        catch (JsonException ex)
        {
            throw new ShareException(ShareProblem.BadManifest, $"{name} is not valid: {ex.Message}");
        }
    }

    /// <summary>Refuses game files by name (packages, paks, signatures) and by content (a package's or a pak's magic number).</summary>
    private static void CheckNoGameData(string name, byte[] data)
    {
        if (ProjectMods.IsGameFile(name) || PakExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
        {
            throw new ShareException(ShareProblem.GameData, $"'{name}' is a game file.");
        }

        if ((data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == PackageSummary.PackageMagic) || data.AsSpan().IndexOf(PakMagic) >= 0)
        {
            throw new ShareException(ShareProblem.GameData, $"'{name}' holds an Unreal package or a pak.");
        }
    }

    /// <summary>The most bytes an entry may unpack to, or null when the name is not one of the package's files.</summary>
    private static int? CapOf(string name) => name switch
    {
        ManifestEntry or EditsEntry => MaxJsonBytes,
        PreviewEntry => MaxPreviewBytes,
        ThumbnailEntry => MaxThumbnailBytes,
        _ when name.StartsWith(FilesFolder, StringComparison.Ordinal) && CarriedFiles.Contains(name[FilesFolder.Length..]) => MaxJsonBytes,
        _ => null,
    };

    private static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 64 && name[0] != '/' && !name.Contains('\\', StringComparison.Ordinal) && !name.Contains(':', StringComparison.Ordinal)
        && !name.Split('/').Any(part => part is "" or "." or "..") && !name.Any(char.IsControl);

    /// <summary>The entry's bytes, or null once it unpacks to more than <paramref name="cap"/> (its stated length is not trusted).</summary>
    private static byte[]? ReadAtMost(ZipArchiveEntry entry, int cap)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsJpeg(byte[]? data) => data is [0xFF, 0xD8, 0xFF, ..];

    private static IEnumerable<string> PlacedTraders(IEnumerable<EditOp> ops) =>
        ops.OfType<AddBlueprintActorOp>().Select(b => b.Trader?.Name).OfType<string>().Where(n => n.Length > 0);
}
