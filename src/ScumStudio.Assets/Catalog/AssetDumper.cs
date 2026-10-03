using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ScumStudio.Assets.Catalog;

/// <summary>One category of the content catalogue (weapons, vehicles, buildings, trees …).</summary>
public sealed record DumpNode(string Id, string Title, int Count, double SizeMB, IReadOnlyList<DumpNode> Children);

/// <summary>A package and the catalogue category it belongs to.</summary>
public readonly record struct DumpPackage(string PackagePath, string Node, string ClassName);

/// <summary>What a dump wrote.</summary>
public sealed record DumpResult(int Packages, int Files, long Bytes, int Missing, string ManifestPath);

/// <summary>
/// The Dump feature: the content catalogue (1,769 categories over every package of the game, built from the cooked
/// AssetRegistry of game build 1.3.3.4.149664) and the extraction of the raw package files of chosen categories.
/// </summary>
public static class AssetDumper
{
    private static readonly Lazy<IReadOnlyList<DumpNode>> TreeCache = new(LoadTree);
    private static readonly Lazy<IReadOnlyList<DumpPackage>> PackagesCache = new(LoadPackages);

    /// <summary>The category tree (26 roots).</summary>
    public static IReadOnlyList<DumpNode> Tree => TreeCache.Value;

    /// <summary>Every catalogued package with its deepest category id.</summary>
    public static IReadOnlyList<DumpPackage> Packages => PackagesCache.Value;

    /// <summary>The packages of the chosen categories (a category includes all of its sub-categories).</summary>
    public static IReadOnlyList<DumpPackage> Select(IEnumerable<string> nodeIds)
    {
        var ids = nodeIds.ToList();
        return Packages.Where(p => ids.Any(id => p.Node == id || p.Node.StartsWith(id + ".", StringComparison.Ordinal))).ToList();
    }

    /// <summary>
    /// Writes every file of each package (<c>.uasset</c>/<c>.umap</c> + <c>.uexp</c>/<c>.ubulk</c>/<c>.uptnl</c>) under
    /// <paramref name="outputFolder"/> at its pak path, plus <c>dump-manifest.csv</c>. Files already there with the same size
    /// are skipped, so a cancelled dump continues where it stopped.
    /// </summary>
    public static DumpResult Dump(AssetCatalog catalog, IReadOnlyList<DumpPackage> packages, string outputFolder,
        IProgress<(int Done, int Total, string Item)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(packages);
        Directory.CreateDirectory(outputFolder);
        var manifest = new StringBuilder("package,category,class,files,bytes\n");
        int files = 0, missing = 0, done = 0;
        long bytes = 0;
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((done++, packages.Count, package.PackagePath));
            if (!catalog.TryGetPackageFile(package.PackagePath, out var main))
            {
                missing++;
                continue;
            }

            var stem = main.Path[..^Path.GetExtension(main.Path).Length];
            int packageFiles = 0;
            long packageBytes = 0;
            foreach (var ext in new[] { Path.GetExtension(main.Path), ".uexp", ".ubulk", ".uptnl" })
            {
                if (!catalog.Provider.Files.TryGetValue(stem + ext, out var file))
                {
                    continue;
                }

                var target = Path.Combine([outputFolder, .. (stem + ext).Split('/')]);
                if (!File.Exists(target) || new FileInfo(target).Length != file.Size)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, file.Read());
                }

                packageFiles++;
                packageBytes += file.Size;
            }

            files += packageFiles;
            bytes += packageBytes;
            manifest.Append(Csv(package.PackagePath)).Append(',').Append(Csv(package.Node)).Append(',').Append(Csv(package.ClassName))
                .Append(',').Append(packageFiles).Append(',').Append(packageBytes).Append('\n');
        }

        var manifestPath = Path.Combine(outputFolder, "dump-manifest.csv");
        File.WriteAllText(manifestPath, manifest.ToString());
        return new DumpResult(packages.Count - missing, files, bytes, missing, manifestPath);

        static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static Stream Resource(string name) =>
        typeof(AssetDumper).Assembly.GetManifestResourceStream("ScumStudio.Assets.Catalog.Dump." + name)
        ?? throw new InvalidOperationException("Embedded dump catalogue " + name + " is missing.");

    private static IReadOnlyList<DumpNode> LoadTree()
    {
        using var stream = Resource("dump-tree.json");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateArray().Select(Node).ToList();

        static DumpNode Node(JsonElement e) => new(
            e.GetProperty("id").GetString()!,
            e.GetProperty("title").GetString()!,
            e.GetProperty("count").GetInt32(),
            e.GetProperty("sizeMB").GetDouble(),
            e.TryGetProperty("children", out var c) ? c.EnumerateArray().Select(Node).ToList() : []);
    }

    private static IReadOnlyList<DumpPackage> LoadPackages()
    {
        using var reader = new StreamReader(new GZipStream(Resource("dump-assignments.tsv.gz"), CompressionMode.Decompress));
        var list = new List<DumpPackage>(111_000);
        reader.ReadLine(); // header: package node leaf class assets sizeBytes sizeIsExact pak
        while (reader.ReadLine() is { } line)
        {
            var cols = line.Split('\t');
            if (cols.Length >= 4)
            {
                list.Add(new DumpPackage(cols[0], cols[1], cols[3]));
            }
        }

        return list;
    }
}
