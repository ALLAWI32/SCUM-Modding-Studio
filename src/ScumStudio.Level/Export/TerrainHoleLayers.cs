using System.Numerics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Level.Export;

/// <summary>
/// Which landscape components of a tile can SHOW a hole (read from SCUM's island, 2026-10-16). A hole is drawn by the
/// landscape's hole material (<c>Mi_Landscape_Config_RVT_Hole</c>, masked at 0.3333) from the visibility layer
/// (<c>DataLayer</c>) of the component's weightmaps; only the components that already allocate that layer use a material
/// built with it, and a pak cannot add the shaders another layer combination would need. So: a component with a usable
/// visibility channel can show a hole (green on the Map); every other component can only open its collision (amber).
/// </summary>
public static class TerrainHoleLayers
{
    /// <summary>The visibility (hole) layer's name.</summary>
    public const string VisibilityLayer = "DataLayer";

    /// <summary>One weightmap layer of a component: which texture of <see cref="Component.Weightmaps"/> and which channel (0..3 = R, G, B, A).</summary>
    public sealed record Allocation(string Layer, int Texture, int Channel);

    /// <summary>A cooked <c>LandscapeComponent</c> as far as its weightmaps go.</summary>
    /// <param name="Export">Export index.</param>
    /// <param name="Name">Export name.</param>
    /// <param name="SectionBase">Section base (landscape quads); the collision component of the same place has the same.</param>
    /// <param name="SizeQuads">Quads per side.</param>
    /// <param name="SubsectionQuads">Quads per side of a subsection.</param>
    /// <param name="Subsections">Subsections per side.</param>
    /// <param name="Allocations">The weightmap layers in allocation order.</param>
    /// <param name="Weightmaps">The weightmap textures as export indices (-1 for one that is not an export of the tile).</param>
    /// <param name="Window">First texel of the component's window in its weightmaps (from <c>WeightmapScaleBias</c>), for a texture <paramref name="SizeQuads"/>-sized.</param>
    public sealed record Component(int Export, string Name, (int X, int Y) SectionBase, int SizeQuads, int SubsectionQuads, int Subsections,
        IReadOnlyList<Allocation> Allocations, IReadOnlyList<int> Weightmaps, (float U, float V) Window)
    {
        /// <summary>The visibility layer's allocation, or null when the component has none.</summary>
        public Allocation? Visibility => Allocations.FirstOrDefault(a => string.Equals(a.Layer, VisibilityLayer, StringComparison.OrdinalIgnoreCase));

        /// <summary>Texels per side of the component's weightmap window.</summary>
        public int WeightmapSize => (SubsectionQuads + 1) * Subsections;
    }

    /// <summary>The visibility channel of a component, where the export writes a hole.</summary>
    /// <param name="Component">The component.</param>
    /// <param name="Texture">Its visibility weightmap.</param>
    /// <param name="Channel">The channel (0..3 = R, G, B, A).</param>
    public sealed record Target(Component Component, LandscapeWeightmap.Texture Texture, int Channel);

    /// <summary>The landscape components of <paramref name="package"/>.</summary>
    public static List<Component> Components(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var result = new List<Component>();
        for (var e = 0; e < package.Exports.Count; e++)
        {
            if (package.GetExportClassName(e) != "LandscapeComponent")
            {
                continue;
            }

            var block = package.ReadProperties(e);
            int Int(string name, int fallback) => (block.Find(name)?.Value as IntValue)?.Value ?? fallback;
            var size = Int("ComponentSizeQuads", 0);
            var allocations = new List<Allocation>();
            foreach (var item in (block.Find("WeightmapLayerAllocations")?.Value as ArrayValue)?.Items.OfType<StructValue>() ?? [])
            {
                var info = item.Properties.FirstOrDefault(p => p.Name == "LayerInfo")?.Value as ObjectValue;
                var name = info is { Index: not 0 } ? package.GetObjectName(info.Index) ?? string.Empty : string.Empty;
                const string suffix = "_LayerInfo";
                allocations.Add(new Allocation(name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name[..^suffix.Length] : name,
                    Byte(item, "WeightmapTextureIndex"), Byte(item, "WeightmapTextureChannel")));
            }

            var weightmaps = ((block.Find("WeightmapTextures")?.Value as ArrayValue)?.Items ?? [])
                .Select(v => v is ObjectValue { Index: > 0 } o ? o.Index - 1 : -1).ToList();
            var bias = block.Find("WeightmapScaleBias")?.Value as Vector4Value;
            result.Add(new Component(e, package.GetObjectName(e + 1) ?? string.Empty, (Int("SectionBaseX", 0), Int("SectionBaseY", 0)), size,
                Int("SubsectionSizeQuads", size), Int("NumSubsections", 1), allocations, weightmaps, (bias?.Z ?? 0f, bias?.W ?? 0f)));
        }

        return result;
    }

    /// <summary>
    /// The visibility channel of <paramref name="component"/> (one of <paramref name="all"/>, the components of
    /// <paramref name="package"/> whose <c>.ubulk</c> has <paramref name="ubulkLength"/> bytes), or null with the reason in
    /// <paramref name="why"/>: no visibility layer, a weightmap shared with another component or not at the window the
    /// island's components use, or one laid out in a way <see cref="LandscapeWeightmap.Read"/> does not know.
    /// </summary>
    public static Target? VisibilityOf(CookedPackage package, IReadOnlyList<Component> all, Component component, long ubulkLength, out string? why)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(component);
        why = null;
        if (component.Visibility is not { } visibility)
        {
            why = "no visibility layer";
            return null;
        }

        var export = visibility.Texture < component.Weightmaps.Count ? component.Weightmaps[visibility.Texture] : -1;
        if (export < 0 || visibility.Channel is < 0 or > 3)
        {
            why = "the visibility layer points at no weightmap of the tile";
            return null;
        }

        if (all.Count(c => c.Weightmaps.Contains(export)) != 1)
        {
            why = "its weightmap is shared with another component";
            return null;
        }

        if (LandscapeWeightmap.Read(package, export, ubulkLength) is not { } texture || texture.Width != component.WeightmapSize || texture.Height != texture.Width)
        {
            why = "its weightmap is not laid out as the island's are";
            return null;
        }

        if (LandscapeWeightmapOrigin(component.Window.U, texture.Width) != 0 || LandscapeWeightmapOrigin(component.Window.V, texture.Height) != 0)
        {
            why = "its window in the weightmap is not at the corner";
            return null;
        }

        return new Target(component, texture, visibility.Channel);
    }

    /// <summary>
    /// Writes the visibility of <paramref name="target"/> for <paramref name="samples"/> (component sample coordinates) to
    /// 255 in mip 0 of <paramref name="uexp"/>/<paramref name="ubulk"/> (in place), then the same channel of every smaller
    /// mip where the editor's mip rule (<see cref="LandscapeWeightmap.BuildMips"/>) gives a different value than for the
    /// stock texture; every other byte stays as cooked. Returns how many mip-0 samples changed.
    /// </summary>
    public static int Open(Target target, IEnumerable<(int X, int Y)> samples, byte[] uexp, byte[]? ubulk)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(samples);
        var (component, texture) = (target.Component, target.Texture);
        var channelByte = LandscapeWeightmap.ByteOfChannel(target.Channel);
        var stock = texture.Mips.Select(m => LandscapeWeightmap.Bytes(m, uexp, ubulk).ToArray()).ToList();
        var opened = stock.Select(m => (byte[])m.Clone()).ToList();
        var changed = 0;
        foreach (var (x, y) in samples.Distinct())
        {
            if (x < 0 || y < 0 || x > component.SizeQuads || y > component.SizeQuads)
            {
                continue;
            }

            var any = false;
            foreach (var (tx, ty) in LandscapeWeightmap.TexelsOf(x, y, component.SubsectionQuads, component.Subsections))
            {
                ref var b = ref opened[0][(((ty * texture.Width) + tx) * 4) + channelByte];
                any |= b != 255;
                b = 255;
            }

            changed += any ? 1 : 0;
        }

        if (changed == 0)
        {
            return 0;
        }

        var before = stock.Select(m => (byte[])m.Clone()).ToList();
        LandscapeWeightmap.BuildMips(before, texture.Width, component.SubsectionQuads, component.Subsections, channelByte);
        var after = opened.Select(m => (byte[])m.Clone()).ToList();
        LandscapeWeightmap.BuildMips(after, texture.Width, component.SubsectionQuads, component.Subsections, channelByte);
        for (var m = 1; m < opened.Count; m++)
        {
            for (var i = channelByte; i < opened[m].Length; i += 4)
            {
                if (after[m][i] != before[m][i])
                {
                    opened[m][i] = after[m][i];
                }
            }
        }

        for (var m = 0; m < opened.Count; m++)
        {
            opened[m].CopyTo(LandscapeWeightmap.Bytes(texture.Mips[m], uexp, ubulk));
        }

        return changed;
    }

    /// <summary>One landscape component's ground on the Map and whether a hole cut there can be seen in the game.</summary>
    /// <param name="Tile">Package path of the landscape tile.</param>
    /// <param name="Component">The component's export name.</param>
    /// <param name="Min">World X/Y corner of its ground.</param>
    /// <param name="Max">The opposite corner.</param>
    /// <param name="Visible">True when the export can write the hole into its visibility layer (green); false: collision only (amber).</param>
    /// <param name="Why">Why it is collision only (null when <paramref name="Visible"/>).</param>
    public sealed record Place(string Tile, string Component, Vector2 Min, Vector2 Max, bool Visible, string? Why)
    {
        /// <summary>True when world (<paramref name="x"/>, <paramref name="y"/>) is on this ground.</summary>
        public bool Contains(float x, float y) => Min.X <= x && x <= Max.X && Min.Y <= y && y <= Max.Y;
    }

    /// <summary>
    /// The components of the landscape tile <paramref name="tile"/> (its parsed package, whose <c>.ubulk</c> has
    /// <paramref name="ubulkLength"/> bytes) as <see cref="Place"/>s: each placed by the collision component at the same
    /// section base (the grid the export cuts on).
    /// </summary>
    public static List<Place> Places(string tile, CookedPackage package, long ubulkLength)
    {
        ArgumentNullException.ThrowIfNull(package);
        var grids = new Dictionary<(int, int), TerrainHoles.Grid>();
        for (var e = 0; e < package.Exports.Count; e++)
        {
            if (package.GetExportClassName(e) == "LandscapeHeightfieldCollisionComponent")
            {
                var block = package.ReadProperties(e);
                if (TerrainHoles.GridOf(package, e, block) is { } grid)
                {
                    grids[((block.Find("SectionBaseX")?.Value as IntValue)?.Value ?? 0, (block.Find("SectionBaseY")?.Value as IntValue)?.Value ?? 0)] = grid;
                }
            }
        }

        var all = Components(package);
        var result = new List<Place>();
        foreach (var component in all)
        {
            if (!grids.TryGetValue(component.SectionBase, out var grid))
            {
                continue;
            }

            var corners = new[] { grid.World(0, 0), grid.World(grid.SizeQuads, 0), grid.World(0, grid.SizeQuads), grid.World(grid.SizeQuads, grid.SizeQuads) };
            var visible = VisibilityOf(package, all, component, ubulkLength, out var why) is not null;
            result.Add(new Place(tile, component.Name, new Vector2(corners.Min(c => c.X), corners.Min(c => c.Y)), new Vector2(corners.Max(c => c.X), corners.Max(c => c.Y)), visible, why));
        }

        return result;
    }

    /// <summary>
    /// <see cref="Places"/> of every landscape tile of <paramref name="catalog"/> (reads each tile's header and exports, not
    /// its bulk file: about 10 s for the island). Tiles that cannot be read are left out.
    /// </summary>
    public static List<Place> Scan(AssetCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var result = new List<Place>();
        foreach (var (tile, _) in GroundHeights.LandscapeTiles(catalog))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!catalog.TryGetPackageFile(tile, out var file))
                {
                    continue;
                }

                var path = file.Path.Replace('\\', '/');
                var stem = path[..path.LastIndexOf('.')];
                if (!catalog.Provider.Files.TryGetValue(stem + ".uexp", out var uexp))
                {
                    continue;
                }

                var ubulk = catalog.Provider.Files.TryGetValue(stem + ".ubulk", out var bulk) ? bulk.Size : 0;
                result.AddRange(Places(tile, CookedPackage.Parse(file.Read(), uexp.Read(), null, tile), ubulk));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or ArgumentException or InvalidOperationException)
            {
                // A tile that cannot be read stays uncoloured; the export reports it if a hole lands there.
            }
        }

        return result;
    }

    /// <summary>The visibility sample the editor's collision cook turns into the PhysX hole of render quad (<paramref name="qx"/>, <paramref name="qy"/>): (qx + 1, qy).</summary>
    public static (int X, int Y) SampleOfQuad(int qx, int qy) => (qx + 1, qy);

    private static int LandscapeWeightmapOrigin(float bias, int size) => (int)MathF.Round((bias * size) - 0.5f);

    private static int Byte(StructValue item, string name) => item.Properties.FirstOrDefault(p => p.Name == name)?.Value switch
    {
        ByteValue b => b.Value,
        IntValue i => i.Value,
        EnumValue { Value: var v } when int.TryParse(v, out var n) => n,
        _ => 0,
    };
}
