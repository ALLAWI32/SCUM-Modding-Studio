using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.IO;

namespace ScumStudio.Formats.Packages;

/// <summary>
/// The World Composition tile record (<c>FWorldTileInfo</c>) that every streaming sublevel of a World Composition map
/// carries in its package summary at <see cref="PackageSummary.WorldTileInfoDataOffset"/>. The cooked game rebuilds
/// the tile list from these records (<c>UWorldComposition::Rescan</c>); the persistent level itself holds no list.
/// Verified on SCUM 1.3.3 (UE 4.27): 137 bytes for <c>A_0_Outpost_Exterior</c>, layout below.
/// </summary>
/// <param name="Position">Tile position offset (FIntVector). SCUM stores 0,0,0: bounds are absolute.</param>
/// <param name="BoundsMin">World-space bounds minimum, centimetres (from <c>FBox</c>).</param>
/// <param name="BoundsMax">World-space bounds maximum, centimetres.</param>
/// <param name="BoundsValid">The FBox <c>IsValid</c> byte.</param>
/// <param name="Layer">Streaming layer.</param>
/// <param name="HideInTileView">Editor flag (uint32 bool).</param>
/// <param name="ParentTilePackageName">Parent tile package (long package name), or <c>None</c> for roots.</param>
/// <param name="LodList">LOD streaming records.</param>
/// <param name="ZOrder">Draw order in the world composition view.</param>
public sealed record WorldTileInfo(
    (int X, int Y, int Z) Position,
    FVector BoundsMin,
    FVector BoundsMax,
    bool BoundsValid,
    WorldTileLayer Layer,
    bool HideInTileView,
    string ParentTilePackageName,
    IReadOnlyList<WorldTileLodInfo> LodList,
    int ZOrder)
{
    /// <summary>Byte length of the record as read (useful for same-size patching).</summary>
    public int SerializedLength { get; init; }

    /// <summary>Centre of the bounds in world centimetres.</summary>
    public FVector Center => new((BoundsMin.X + BoundsMax.X) * 0.5f, (BoundsMin.Y + BoundsMax.Y) * 0.5f, (BoundsMin.Z + BoundsMax.Z) * 0.5f);

    /// <summary>Size of the bounds in world centimetres.</summary>
    public FVector Size => new(BoundsMax.X - BoundsMin.X, BoundsMax.Y - BoundsMin.Y, BoundsMax.Z - BoundsMin.Z);

    /// <summary>True when the tile has a parent tile (the game streams it relative to that parent).</summary>
    public bool HasParent => !string.IsNullOrEmpty(ParentTilePackageName) && ParentTilePackageName != "None";

    /// <summary>
    /// Reads the record of <paramref name="package"/>, or null when the package is not a World Composition tile
    /// (<see cref="PackageSummary.WorldTileInfoDataOffset"/> is 0, e.g. the persistent level, or any non-level package).
    /// </summary>
    public static WorldTileInfo? TryRead(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var offset = package.Summary.WorldTileInfoDataOffset;
        if (offset <= 0 || offset >= package.UAsset.Length)
        {
            return null;
        }

        return Read(new ByteReader(package.UAsset, offset, package.UAsset.Length - offset));
    }

    /// <summary>Reads an <c>FWorldTileInfo</c> at the reader's position (UE 4.27 layout: all versioned fields present).</summary>
    public static WorldTileInfo Read(ByteReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var start = r.Position;
        var position = (r.I32(), r.I32(), r.I32());
        var min = new FVector(r.F32(), r.F32(), r.F32());
        var max = new FVector(r.F32(), r.F32(), r.F32());
        var valid = r.U8() != 0;
        var layer = new WorldTileLayer(r.FString(), r.I32(), (r.I32(), r.I32()), r.I32(), r.U32() != 0);   // FWorldTileLayer
        var hide = r.U32() != 0;                                                                        // VER_UE4_WORLD_LEVEL_INFO_UPDATED
        var parent = r.FString();
        var lodCount = r.I32();                                                                          // VER_UE4_WORLD_LEVEL_INFO_LOD_LIST
        if (lodCount < 0 || lodCount > 64)
        {
            throw new InvalidDataException($"FWorldTileInfo: implausible LOD count {lodCount}.");
        }

        var lods = new WorldTileLodInfo[lodCount];
        for (var i = 0; i < lodCount; i++)
        {
            lods[i] = new WorldTileLodInfo(r.I32(), r.F32(), r.F32(), r.I32(), r.I32());
        }

        var zOrder = r.I32();                                                                            // VER_UE4_WORLD_LEVEL_INFO_ZORDER
        return new WorldTileInfo(position, min, max, valid, layer, hide, parent, lods, zOrder) { SerializedLength = r.Position - start };
    }

    /// <summary>Serialises the record in the same layout as <see cref="Read"/>.</summary>
    public void Write(ByteWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.I32(Position.X); w.I32(Position.Y); w.I32(Position.Z);
        w.F32(BoundsMin.X); w.F32(BoundsMin.Y); w.F32(BoundsMin.Z);
        w.F32(BoundsMax.X); w.F32(BoundsMax.Y); w.F32(BoundsMax.Z);
        w.U8(BoundsValid ? (byte)1 : (byte)0);
        w.FString(Layer.Name); w.I32(Layer.Reserved0); w.I32(Layer.Reserved1.X); w.I32(Layer.Reserved1.Y);
        w.I32(Layer.StreamingDistance); w.U32(Layer.DistanceStreamingEnabled ? 1u : 0u);
        w.U32(HideInTileView ? 1u : 0u);
        w.FString(ParentTilePackageName);
        w.I32(LodList.Count);
        foreach (var lod in LodList)
        {
            w.I32(lod.RelativeStreamingDistance); w.F32(lod.Reserved0); w.F32(lod.Reserved1); w.I32(lod.Reserved2); w.I32(lod.Reserved3);
        }

        w.I32(ZOrder);
    }

    /// <summary>One-line summary for CLI output.</summary>
    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"bounds ({BoundsMin.X:0.#}, {BoundsMin.Y:0.#}, {BoundsMin.Z:0.#}) .. ({BoundsMax.X:0.#}, {BoundsMax.Y:0.#}, {BoundsMax.Z:0.#}) layer '{Layer.Name}' streaming {Layer.StreamingDistance} cm{(Layer.DistanceStreamingEnabled ? "" : " (disabled)")} parent {ParentTilePackageName} z {ZOrder}");
}

/// <summary><c>FWorldTileLayer</c>: the streaming layer a tile belongs to.</summary>
/// <param name="Name">Layer name, e.g. <c>CityBlock10000</c>, <c>Outpost_Buildings</c>, <c>Landscape_0_5</c>.</param>
/// <param name="Reserved0">Unused int32.</param>
/// <param name="Reserved1">Unused FIntPoint.</param>
/// <param name="StreamingDistance">Distance in centimetres at which tiles of this layer stream in.</param>
/// <param name="DistanceStreamingEnabled">False for layers that are always loaded.</param>
public sealed record WorldTileLayer(string Name, int Reserved0, (int X, int Y) Reserved1, int StreamingDistance, bool DistanceStreamingEnabled);

/// <summary><c>FWorldTileLODInfo</c>: a LOD level of a tile.</summary>
public sealed record WorldTileLodInfo(int RelativeStreamingDistance, float Reserved0, float Reserved1, int Reserved2, int Reserved3);
