using System.Buffers.Binary;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Meshes;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Export;

/// <summary>A box in the world: <see cref="Local"/> in the space of <see cref="World"/> (a mesh's bounds where it is placed).</summary>
/// <param name="World">Placement.</param>
/// <param name="Local">Box in placement space.</param>
public readonly record struct CutBox(FTransform World, BoundingBox Local)
{
    /// <summary>True when <paramref name="p"/> (world) lies in the box grown by <paramref name="margin"/> world units.</summary>
    public bool Contains(FVector p, float margin)
    {
        var l = World.InverseTransformPosition(p);
        var s = World.Scale3D;
        float mx = margin / Math.Max(Math.Abs(s.X), 1e-4f), my = margin / Math.Max(Math.Abs(s.Y), 1e-4f), mz = margin / Math.Max(Math.Abs(s.Z), 1e-4f);
        return l.X >= Local.Min.X - mx && l.X <= Local.Max.X + mx
            && l.Y >= Local.Min.Y - my && l.Y <= Local.Max.Y + my
            && l.Z >= Local.Min.Z - mz && l.Z <= Local.Max.Z + mz;
    }

    /// <summary>World axis-aligned box around the oriented one.</summary>
    public (FVector Min, FVector Max) WorldBounds() => FarModels.Around(World, Local);
}

/// <summary>
/// SCUM draws every place from far away with a ready-made model: the persistent level's <c>DistantLevelManager</c> lists
/// 1,523 of them (<c>_distantLevelDescriptions</c>: one per level, a building, an outpost, a quarter of a town, the Tudman
/// bridge), each with the meshes it draws while that level is not shown; the four outposts' exteriors also have HLOD
/// proxies (<c>LODActor</c>, drawn past 30 m). Deleting or moving an object leaves it in those merged models (a modder:
/// "when you delete a building, its further LODs still remain"). The export cuts each removed or moved object out of
/// them: a far model that is (almost) only that object is collapsed in the manager's list (scale 0.0001 in place; the
/// outposts share those meshes), a merged one gets the object's triangles made empty in every index buffer of every LOD
/// (in place, so the mesh files keep their size).
/// </summary>
public static class FarModels
{
    /// <summary>The persistent level holding the <c>DistantLevelManager</c>.</summary>
    public const string IslandLevel = "/Game/ConZ_Files/Maps/The_Island/The_Island";

    /// <summary>How far past an object's bounds a far-model vertex still counts as the object's (far models are simplified).</summary>
    public const float Margin = 50f;

    /// <summary>Share of a far model's triangles inside the cut from which the whole model is that object (and is hidden).</summary>
    public const double WholeShare = 0.8;

    private const float Collapsed = 0.0001f;

    /// <summary>One entry of the manager's list.</summary>
    /// <param name="Name">Level name the model stands for.</param>
    /// <param name="World">Where the meshes are drawn.</param>
    /// <param name="Bounds">Mesh bounds in <paramref name="World"/> space.</param>
    /// <param name="Meshes">Mesh object paths (the model's LODs).</param>
    /// <param name="Scale3DOffset">Export-relative offset of the 12-byte <c>Transform.Scale3D</c> value.</param>
    public sealed record Description(string Name, FTransform World, BoundingBox Bounds, IReadOnlyList<string> Meshes, int Scale3DOffset);

    /// <summary>A merged mesh drawn somewhere (far model LOD or HLOD proxy) that may show cut objects.</summary>
    /// <param name="Label">For the report.</param>
    /// <param name="World">Where it is drawn.</param>
    /// <param name="Meshes">Its meshes.</param>
    /// <param name="Description">The manager entry it belongs to, or null (HLOD proxy).</param>
    public sealed record Candidate(string Label, FTransform World, IReadOnlyList<string> Meshes, Description? Description);

    /// <summary>The oriented boxes of what <paramref name="actor"/> draws: each mesh component, each instance.</summary>
    public static IEnumerable<CutBox> BoxesOf(ActorRecord actor, Func<string, BendMesh?> meshes)
    {
        ArgumentNullException.ThrowIfNull(actor);
        foreach (var c in actor.Components.Where(c => c.IsSceneComponent && c.IsVisible && !HelperMeshes.IsHelper(actor, c)))
        {
            if (meshes(c.StaticMeshPath!)?.Bounds is not { IsEmpty: false } bounds)
            {
                continue;
            }

            if (c.IsInstanced)
            {
                foreach (var instance in c.Instances)
                {
                    yield return new CutBox(instance * c.WorldTransform, bounds);
                }
            }
            else if (c.SplineMesh is null)
            {
                yield return new CutBox(c.WorldTransform, bounds);
            }
            else if (StreamingArea.Of([actor with { Components = [c] }], meshes) is { } box)
            {
                yield return new CutBox(FTransform.Identity, new BoundingBox(box.Min.ToVector3(), box.Max.ToVector3()));
            }
        }
    }

    /// <summary>The box of one instance of a component.</summary>
    public static CutBox? BoxOf(ComponentRecord component, FTransform instance, Func<string, BendMesh?> meshes) =>
        component.StaticMeshPath is { } path && meshes(path)?.Bounds is { IsEmpty: false } bounds
            ? new CutBox(instance * component.WorldTransform, bounds)
            : null;

    /// <summary>Reads the manager's list from the persistent level (empty when it has none).</summary>
    public static IReadOnlyList<Description> ReadDescriptions(CookedPackage island, out PropertyBlock? block)
    {
        ArgumentNullException.ThrowIfNull(island);
        block = null;
        for (var i = 0; i < island.Exports.Count; i++)
        {
            if (island.GetExportClassName(i) != "DistantLevelManager")
            {
                continue;
            }

            block = island.ReadProperties(i);
            if (block.Properties.FirstOrDefault(t => t.Name == "_distantLevelDescriptions")?.Value is not ArrayValue list)
            {
                return [];
            }

            var result = new List<Description>(list.Items.Count);
            foreach (var item in list.Items.OfType<StructValue>())
            {
                if (Find(item, "Transform")?.Value is not StructValue transform
                    || Find(transform, "Scale3D") is not { Value: VectorValue scale } scaleTag
                    || Find(item, "MeshBounds")?.Value is not { } boundsValue)
                {
                    continue;
                }

                // MeshBounds is stored tagged (Origin, BoxExtent, SphereRadius), not as the native struct.
                var bounds = boundsValue switch
                {
                    BoxSphereBoundsValue native => native,
                    StructValue { } tagged when Find(tagged, "Origin")?.Value is VectorValue origin && Find(tagged, "BoxExtent")?.Value is VectorValue extent
                        => new BoxSphereBoundsValue(origin, extent, 0f),
                    _ => null,
                };
                if (bounds is null)
                {
                    continue;
                }

                var rotation = Find(transform, "Rotation")?.Value is QuatValue q ? new FQuat(q.X, q.Y, q.Z, q.W) : FQuat.Identity;
                var translation = Find(transform, "Translation")?.Value is VectorValue t ? new FVector(t.X, t.Y, t.Z) : FVector.Zero;
                var meshes = Find(item, "MeshLODs")?.Value is ArrayValue lods
                    ? lods.Items.OfType<SoftObjectValue>().Select(s => s.AssetPath).Where(p => p.Length > 0 && p != "None").ToList()
                    : [];
                var o = new System.Numerics.Vector3(bounds.Origin.X, bounds.Origin.Y, bounds.Origin.Z);
                var e = new System.Numerics.Vector3(bounds.BoxExtent.X, bounds.BoxExtent.Y, bounds.BoxExtent.Z);
                result.Add(new Description(
                    Find(item, "Name")?.Value is StrValue name ? name.Value : string.Empty,
                    new FTransform(rotation, translation, new FVector(scale.X, scale.Y, scale.Z)),
                    new BoundingBox(o - e, o + e),
                    meshes,
                    scaleTag.ValueOffset));
            }

            return result;
        }

        return [];
    }

    /// <summary>
    /// Cuts <paramref name="cuts"/> out of the far models and the HLOD proxies (<paramref name="hlod"/>) that show them and
    /// writes the changed files under <paramref name="staging"/> (the persistent level is read from there when the export
    /// already rewrote it). Returns one report line per changed model.
    /// </summary>
    public static IReadOnlyList<string> Apply(
        AssetCatalog catalog, IReadOnlyList<CutBox> cuts, IReadOnlyList<Candidate> hlod, string staging, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(cuts);
        ArgumentNullException.ThrowIfNull(hlod);
        var lines = new List<string>();
        if (cuts.Count == 0)
        {
            return lines;
        }

        var files = new Dictionary<string, PackageFiles?>(StringComparer.OrdinalIgnoreCase);
        PackageFiles? Files(string package)
        {
            if (!files.TryGetValue(package, out var f))
            {
                f = PackageFiles.Read(catalog, package, staging);
                files[package] = f;
            }

            return f;
        }

        var island = Files(IslandLevel);
        IReadOnlyList<Description> descriptions = [];
        PropertyBlock? block = null;
        if (island is not null)
        {
            try
            {
                descriptions = ReadDescriptions(CookedPackage.Parse(island.Header, island.Exports, null, IslandLevel), out block);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException)
            {
                warnings.Add($"Far models: the island's list could not be read ({ex.Message}); deleted objects may still show from far away.");
            }
        }

        var uses = descriptions.SelectMany(d => d.Meshes).GroupBy(m => m, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var cutBounds = cuts.Select(c => c.WorldBounds()).ToList();
        var candidates = descriptions.Select(d => new Candidate(d.Name, d.World, d.Meshes, d)).Concat(hlod);
        foreach (var candidate in candidates)
        {
            var (min, max) = Around(candidate.World, candidate.Description?.Bounds ?? Huge);
            var near = cuts.Where((c, i) => Overlaps(cutBounds[i], (min, max))).ToList();
            if (near.Count == 0 || candidate.Meshes.Count == 0)
            {
                continue;
            }

            var meshes = new List<(string Path, UStaticMesh Mesh)>();
            foreach (var path in candidate.Meshes)
            {
                if (catalog.TryLoadObject<UStaticMesh>(path, out var mesh) && mesh.RenderData?.LODs is { Length: > 0 })
                {
                    meshes.Add((path, mesh));
                }
            }

            if (meshes.Count == 0)
            {
                continue;
            }

            var (inside, total) = Count(meshes[0].Mesh, candidate.World, near);
            if (inside == 0)
            {
                continue;
            }

            if (candidate.Description is { } d && (double)inside / total >= WholeShare)
            {
                if (island is not null && block is not null)
                {
                    var at = block.ToUExpOffset(d.Scale3DOffset);
                    for (var k = 0; k < 3; k++)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(island.Exports.AsSpan(at + (4 * k)), Collapsed);
                    }

                    island.Dirty = true;
                    lines.Add($"{d.Name}: far model hidden (it showed only what was removed)");
                }

                continue;
            }

            if (candidate.Description is not null && candidate.Meshes.Any(m => uses.GetValueOrDefault(m) > 1))
            {
                // A neighbour's shared model the cut only grazes is left alone; one it covers in good part is reported.
                if ((double)inside / total < 0.2)
                {
                    continue;
                }

                warnings.Add($"Far model {candidate.Label}: its meshes are shared with other places, so {inside:N0} triangles of what was removed still show from far away.");
                continue;
            }

            var cutTriangles = 0;
            foreach (var (path, mesh) in meshes)
            {
                var package = path[..path.LastIndexOf('.')];
                if (Files(package) is not { } target)
                {
                    warnings.Add($"Far model {candidate.Label}: {package} was not found; it still shows what was removed.");
                    continue;
                }

                var (cut, missing) = Patch(mesh, candidate.World, near, target);
                cutTriangles += cut;
                if (missing > 0)
                {
                    warnings.Add($"Far model {candidate.Label}: {missing} index buffer(s) of {mesh.Name} were not found in its files; parts of what was removed may still show from far away.");
                }
            }

            if (cutTriangles > 0)
            {
                lines.Add($"{candidate.Label}: {cutTriangles:N0} triangles cut from the far model");
            }
        }

        foreach (var f in files.Values.Where(f => f is { Dirty: true }))
        {
            f!.Write(staging);
        }

        return lines;
    }

    /// <summary>(triangles inside the cut, all triangles) of the first LOD of <paramref name="mesh"/> drawn at <paramref name="world"/>.</summary>
    public static (int Inside, int Total) Count(UStaticMesh mesh, FTransform world, IReadOnlyList<CutBox> cuts)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var lod = mesh.RenderData!.LODs.FirstOrDefault(l => !l.SkipLod && l.PositionVertexBuffer is not null && l.IndexBuffer is not null);
        if (lod is null)
        {
            return (0, 0);
        }

        var inside = Inside(lod.PositionVertexBuffer!, world, cuts);
        var indices = Indices(lod.IndexBuffer!);
        var count = 0;
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            count += inside[indices[t]] && inside[indices[t + 1]] && inside[indices[t + 2]] ? 1 : 0;
        }

        return (count, indices.Length / 3);
    }

    /// <summary>
    /// Makes every triangle of every LOD of <paramref name="mesh"/> that lies in the cut empty (its three indices the same),
    /// in each index buffer found byte for byte in <paramref name="files"/>. Returns (triangles cut, buffers not found).
    /// </summary>
    public static (int Cut, int Missing) Patch(UStaticMesh mesh, FTransform world, IReadOnlyList<CutBox> cuts, PackageFiles files)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(files);
        int cut = 0, missing = 0;
        foreach (var lod in mesh.RenderData!.LODs.Where(l => !l.SkipLod && l.PositionVertexBuffer is not null))
        {
            var inside = Inside(lod.PositionVertexBuffer!, world, cuts);
            var buffers = new[] { lod.IndexBuffer, lod.ReversedIndexBuffer, lod.DepthOnlyIndexBuffer, lod.ReversedDepthOnlyIndexBuffer }
                .Where(b => b is not null && Indices(b).Length > 0)
                .Select(b => (Wide: b!.Indices32 is { Length: > 0 }, Values: Indices(b!)))
                .DistinctBy(b => Convert.ToBase64String(Bytes(b.Values, b.Wide)));
            var first = true;
            foreach (var (wide, values) in buffers)
            {
                var original = Bytes(values, wide);
                var changed = (uint[])values.Clone();
                var n = CutTriangles(changed, inside);
                if (n == 0)
                {
                    continue;
                }

                // FRawStaticIndexBuffer: int32 b32Bit, then the bulk array of bytes (int32 element size 1, int32 count, data).
                var header = new byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(header, wide ? 1 : 0);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 1);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), original.Length);
                if (!files.Replace([.. header, .. original], [.. header, .. Bytes(changed, wide)]))
                {
                    missing++;
                    continue;
                }

                cut += first ? n : 0;
                first = false;
            }
        }

        return (cut, missing);
    }

    /// <summary>Makes each triangle of <paramref name="indices"/> whose three vertices are <paramref name="inside"/> empty (three equal indices); returns how many.</summary>
    public static int CutTriangles(uint[] indices, bool[] inside)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(inside);
        var n = 0;
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            if (inside[indices[t]] && inside[indices[t + 1]] && inside[indices[t + 2]])
            {
                indices[t + 1] = indices[t];
                indices[t + 2] = indices[t];
                n++;
            }
        }

        return n;
    }

    /// <summary>World axis-aligned box around <paramref name="local"/> placed at <paramref name="world"/>.</summary>
    public static (FVector Min, FVector Max) Around(FTransform world, BoundingBox local)
    {
        var min = new FVector(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new FVector(float.MinValue, float.MinValue, float.MinValue);
        for (var k = 0; k < 8; k++)
        {
            var p = world.TransformPosition(new FVector(
                (k & 1) == 0 ? local.Min.X : local.Max.X, (k & 2) == 0 ? local.Min.Y : local.Max.Y, (k & 4) == 0 ? local.Min.Z : local.Max.Z));
            min = FVector.Min(min, p);
            max = FVector.Max(max, p);
        }

        return (min, max);
    }

    private static readonly BoundingBox Huge = new(new(-1e8f), new(1e8f));

    private static bool Overlaps((FVector Min, FVector Max) a, (FVector Min, FVector Max) b) =>
        a.Min.X <= b.Max.X + Margin && a.Max.X >= b.Min.X - Margin
        && a.Min.Y <= b.Max.Y + Margin && a.Max.Y >= b.Min.Y - Margin
        && a.Min.Z <= b.Max.Z + Margin && a.Max.Z >= b.Min.Z - Margin;

    private static bool[] Inside(FPositionVertexBuffer positions, FTransform world, IReadOnlyList<CutBox> cuts)
    {
        var inside = new bool[positions.Verts.Length];
        for (var i = 0; i < inside.Length; i++)
        {
            var v = positions.Verts[i];
            var p = world.TransformPosition(new FVector(v.X, v.Y, v.Z));
            foreach (var c in cuts)
            {
                if (c.Contains(p, Margin))
                {
                    inside[i] = true;
                    break;
                }
            }
        }

        return inside;
    }

    private static uint[] Indices(FRawStaticIndexBuffer buffer) =>
        buffer.Indices32 is { Length: > 0 } wide ? wide : buffer.Indices16?.Select(i => (uint)i).ToArray() ?? [];

    private static byte[] Bytes(uint[] values, bool wide)
    {
        var bytes = new byte[values.Length * (wide ? 4 : 2)];
        for (var i = 0; i < values.Length; i++)
        {
            if (wide)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)values[i]);
            }
        }

        return bytes;
    }

    private static PropertyTag? Find(StructValue value, string name) => value.Properties.FirstOrDefault(t => t.Name == name);
}

/// <summary>A cooked package's files as read for an in-place patch, from the export's staging folder when it already wrote them.</summary>
public sealed class PackageFiles
{
    private PackageFiles(string virtualStem, string headerExtension, byte[] header, byte[] exports, byte[]? bulk)
    {
        VirtualStem = virtualStem;
        HeaderExtension = headerExtension;
        Header = header;
        Exports = exports;
        Bulk = bulk;
    }

    /// <summary>Path inside the pak without extension, e.g. <c>SCUM/Content/ConZ_Files/Maps/The_Island/The_Island</c>.</summary>
    public string VirtualStem { get; }

    /// <summary><c>.umap</c> or <c>.uasset</c>.</summary>
    public string HeaderExtension { get; }

    /// <summary>The header file.</summary>
    public byte[] Header { get; }

    /// <summary>The <c>.uexp</c>.</summary>
    public byte[] Exports { get; }

    /// <summary>The <c>.ubulk</c>, if any.</summary>
    public byte[]? Bulk { get; }

    /// <summary>True once something was patched.</summary>
    public bool Dirty { get; set; }

    /// <summary>Reads <paramref name="packagePath"/> from <paramref name="staging"/> when written there, else from <paramref name="catalog"/>; null when missing.</summary>
    public static PackageFiles? Read(AssetCatalog catalog, string packagePath, string staging)
    {
        if (!catalog.TryGetPackageFile(packagePath, out var file))
        {
            return null;
        }

        var path = file.Path.Replace('\\', '/');
        var dot = path.LastIndexOf('.');
        var stem = path[..dot];
        var extension = path[dot..];
        var staged = Path.Combine([staging, .. stem.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        if (File.Exists(staged + extension) && File.Exists(staged + ".uexp"))
        {
            return new PackageFiles(stem, extension, File.ReadAllBytes(staged + extension), File.ReadAllBytes(staged + ".uexp"),
                File.Exists(staged + ".ubulk") ? File.ReadAllBytes(staged + ".ubulk") : null);
        }

        if (!catalog.Provider.Files.TryGetValue(stem + ".uexp", out var uexp))
        {
            return null;
        }

        return new PackageFiles(stem, extension, file.Read(), uexp.Read(),
            catalog.Provider.Files.TryGetValue(stem + ".ubulk", out var ubulk) ? ubulk.Read() : null);
    }

    /// <summary>Replaces every occurrence of <paramref name="from"/> in the <c>.uexp</c> and <c>.ubulk</c> (same length); false when none.</summary>
    public bool Replace(byte[] from, byte[] to)
    {
        var found = false;
        foreach (var bytes in new[] { Exports, Bulk })
        {
            if (bytes is null)
            {
                continue;
            }

            for (var start = 0; ;)
            {
                var at = bytes.AsSpan(start).IndexOf(from);
                if (at < 0)
                {
                    break;
                }

                to.CopyTo(bytes.AsSpan(start + at));
                start += at + from.Length;
                found = true;
            }
        }

        Dirty |= found;
        return found;
    }

    /// <summary>Writes the files under <paramref name="staging"/> at their pak paths.</summary>
    public void Write(string staging)
    {
        var target = Path.Combine([staging, .. VirtualStem.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target + HeaderExtension, Header);
        File.WriteAllBytes(target + ".uexp", Exports);
        if (Bulk is not null)
        {
            File.WriteAllBytes(target + ".ubulk", Bulk);
        }
    }
}
