using System.Buffers.Binary;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Level.Spawns;

/// <summary>What to change in the island's spawn places.</summary>
public sealed record SpawnPlacesEditRequest
{
    /// <summary>Stock places to remove.</summary>
    public IReadOnlyCollection<(SpawnPlaceKind Kind, int Index)> Deleted { get; init; } = [];

    /// <summary>Stock places moved (root transform as the map shows it: place, turn, and for zones and areas the size in metres).</summary>
    public IReadOnlyDictionary<(SpawnPlaceKind Kind, int Index), TransformValue> Moved { get; init; } = new Dictionary<(SpawnPlaceKind, int), TransformValue>();

    /// <summary>New places: a copy of a stock place (its group and settings) at a transform.</summary>
    public IReadOnlyList<(SpawnPlaceKind Kind, int Source, TransformValue Transform)> Added { get; init; } = [];

    /// <summary>True when nothing changes.</summary>
    public bool IsEmpty => Deleted.Count == 0 && Moved.Count == 0 && Added.Count == 0;
}

/// <summary>What <see cref="SpawnPlacesEditor.Apply"/> wrote.</summary>
/// <param name="Deleted">Places removed.</param>
/// <param name="Moved">Places moved or resized.</param>
/// <param name="Added">Places added.</param>
/// <param name="Warnings">Edits that could not be written.</param>
public sealed record SpawnPlacesReport(int Deleted, int Moved, int Added, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes spawn place edits into <c>The_Island_LevelStaticData</c>: each touched array is rebuilt from its stock entries
/// (kept, moved in place, or left out) followed by copies of stock entries at their new places; its count and the array
/// and inner tag sizes follow. A zone's bounding boxes (plain, activation, low-population activation) are recomputed from
/// its ellipse, the way the stock data has them.
/// </summary>
public static class SpawnPlacesEditor
{
    /// <summary>The spawn place edits of <paramref name="state"/> (actors of the <see cref="SpawnPlaces.StaticDataPath"/> document).</summary>
    public static SpawnPlacesEditRequest Plan(EditState state, List<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var deleted = new HashSet<(SpawnPlaceKind, int)>();
        foreach (var actor in state.DeletedActors.Where(a => SpawnPlaces.IsStaticData(a.Level)))
        {
            if (SpawnPlaces.ParseName(actor.Actor) is { } stock)
            {
                deleted.Add(stock);
            }
        }

        var moved = new Dictionary<(SpawnPlaceKind, int), TransformValue>();
        foreach (var (actor, component, value) in state.TransformOverrides.Where(t => SpawnPlaces.IsStaticData(t.Actor.Level)))
        {
            if (component.Length == 0 && SpawnPlaces.ParseName(actor.Actor) is { } stock && !deleted.Contains(stock))
            {
                moved[stock] = value;
            }
        }

        var added = new List<(SpawnPlaceKind, int, TransformValue)>();
        foreach (var (actor, op) in state.AddedActors.Where(a => SpawnPlaces.IsStaticData(a.Key.Level)).OrderBy(a => a.Key.Actor, StringComparer.Ordinal))
        {
            if (state.IsDeleted(actor) || state.GetAddedTransform(actor) is not { } transform)
            {
                continue;
            }

            // A copy of a copy is a copy of the stock place the chain starts from.
            var source = op;
            var hops = 0;
            while (source is DuplicateActorOp d && SpawnPlaces.ParseName(d.Source.Actor) is null && state.AddedActors.TryGetValue(d.Source, out var parent) && hops++ < 64)
            {
                source = parent;
            }

            if (source is DuplicateActorOp copy && SpawnPlaces.ParseName(copy.Source.Actor) is { } stock)
            {
                added.Add((stock.Kind, stock.Index, transform));
            }
            else
            {
                warnings?.Add($"{actor.Actor}: not a copy of a spawn place; not written.");
            }
        }

        return new SpawnPlacesEditRequest { Deleted = deleted, Moved = moved, Added = added };
    }

    /// <summary>Applies <paramref name="request"/> to the static data <paramref name="package"/>.</summary>
    public static (PackageBytes Bytes, SpawnPlacesReport Report) Apply(CookedPackage package, SpawnPlacesEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(request);
        var exportIndex = SpawnPlaces.StaticDataExport(package);
        var payload = package.GetExportData(exportIndex).ToArray();
        var block = package.ReadProperties(exportIndex);
        var warnings = new List<string>();
        int deleted = 0, moved = 0, added = 0;

        var kinds = request.Deleted.Select(d => d.Kind).Concat(request.Moved.Keys.Select(m => m.Kind)).Concat(request.Added.Select(a => a.Kind)).Distinct();
        var arrays = kinds
            .Select(k => (Kind: k, Tag: block.Find(SpawnPlaces.ArrayOf(k))))
            .Where(a => a.Tag is { Value: ArrayValue { InnerTag: not null } })
            .OrderByDescending(a => a.Tag!.Offset) // from the back: the offsets in front stay valid
            .ToList();
        foreach (var (kind, tag) in arrays)
        {
            var array = (ArrayValue)tag!.Value;
            var inner = array.InnerTag!;
            var items = new List<byte[]>(array.Items.Count + 16);
            for (var i = 0; i < array.Items.Count; i++)
            {
                var item = array.Items[i];
                if (request.Deleted.Contains((kind, i)))
                {
                    deleted++;
                    continue;
                }

                var bytes = payload.AsSpan(item.Offset, item.Size).ToArray();
                if (request.Moved.TryGetValue((kind, i), out var value))
                {
                    Place(bytes, kind, (StructValue)item, value, -item.Offset);
                    moved++;
                }

                items.Add(bytes);
            }

            foreach (var (_, source, transform) in request.Added.Where(a => a.Kind == kind))
            {
                if (source < 0 || source >= array.Items.Count)
                {
                    warnings.Add($"{SpawnPlaces.ActorName(kind, source)}: no such stock place to copy.");
                    continue;
                }

                var item = array.Items[source];
                var bytes = payload.AsSpan(item.Offset, item.Size).ToArray();
                Place(bytes, kind, (StructValue)item, transform, -item.Offset);
                items.Add(bytes);
                added++;
            }

            payload = ReplaceStructArray(payload, tag, array, items);
        }

        var data = Enumerable.Range(0, package.Exports.Count).Select(i => i == exportIndex ? payload : package.GetExportData(i)).ToArray();
        var bytesOut = PackageWriter.Build(PackageWriter.ToBuildInput(package) with { ExportData = data });
        return (bytesOut, new SpawnPlacesReport(deleted, moved, added, warnings));
    }

    /// <summary>
    /// <paramref name="payload"/> with the array-of-structs <paramref name="tag"/> holding <paramref name="items"/> (each
    /// the bytes of one tagged struct element): the count, the inner struct tag (its size = all items) and the items; the
    /// tag's size follows. Offsets after the tag move; read the result again before patching further.
    /// </summary>
    internal static byte[] ReplaceStructArray(byte[] payload, PropertyTag tag, ArrayValue array, IReadOnlyList<byte[]> items)
    {
        var inner = array.InnerTag ?? throw new ArgumentException("Not an array of structs.", nameof(array));
        var itemsLength = items.Sum(b => b.Length);
        var header = payload.AsSpan(inner.Offset, inner.ValueOffset - inner.Offset).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(inner.SizeFieldOffset - inner.Offset), itemsLength);
        var value = new byte[4 + header.Length + itemsLength];
        BinaryPrimitives.WriteInt32LittleEndian(value, items.Count);
        header.CopyTo(value, 4);
        var at = 4 + header.Length;
        foreach (var b in items)
        {
            b.CopyTo(value, at);
            at += b.Length;
        }

        var result = new byte[payload.Length - tag.Size + value.Length];
        payload.AsSpan(0, tag.ValueOffset).CopyTo(result);
        value.CopyTo(result, tag.ValueOffset);
        payload.AsSpan(tag.EndOffset).CopyTo(result.AsSpan(tag.ValueOffset + value.Length));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(tag.SizeFieldOffset), value.Length);
        return result;
    }

    /// <summary>
    /// Writes where a spawn point is into the bytes of its struct element (offsets shifted by <paramref name="shift"/>): a
    /// tagged <c>Transform</c> member (an item spawner marker: rotation, translation, scale) or else the element's first
    /// vector member (a sentry's <c>LocationRelativeToSentry</c>: the location only). False when it has neither.
    /// </summary>
    internal static bool WritePoint(byte[] bytes, StructValue item, TransformValue value, int shift)
    {
        if (SpawnPlaces.Member(item, "Transform")?.Value is StructValue t)
        {
            var q = value.ToTransform().Rotation;
            if (SpawnPlaces.Member(t, "Rotation")?.Value is QuatValue r)
            {
                Floats(bytes, r.Offset + shift, q.X, q.Y, q.Z, q.W);
            }

            if (SpawnPlaces.Member(t, "Translation")?.Value is VectorValue p)
            {
                Floats(bytes, p.Offset + shift, value.Location.X, value.Location.Y, value.Location.Z);
            }

            if (SpawnPlaces.Member(t, "Scale3D")?.Value is VectorValue s)
            {
                Floats(bytes, s.Offset + shift, value.Scale.X, value.Scale.Y, value.Scale.Z);
            }

            return true;
        }

        if (item.Properties.Select(m => m.Value).OfType<VectorValue>().FirstOrDefault() is { } location)
        {
            Floats(bytes, location.Offset + shift, value.Location.X, value.Location.Y, value.Location.Z);
            return true;
        }

        return false;
    }

    /// <summary>Writes a place's transform (and a zone's or area's size) into its item bytes (offsets shifted by <paramref name="shift"/>).</summary>
    private static void Place(byte[] bytes, SpawnPlaceKind kind, StructValue item, TransformValue value, int shift)
    {
        var transformName = kind switch
        {
            SpawnPlaceKind.Vehicle => "SpawnTransform",
            SpawnPlaceKind.Zone => "_transform",
            _ => "Transform",
        };
        if (SpawnPlaces.Member(item, transformName)?.Value is not StructValue t)
        {
            return;
        }

        var q = value.ToTransform().Rotation;
        if (SpawnPlaces.Member(t, "Rotation")?.Value is QuatValue r)
        {
            Floats(bytes, r.Offset + shift, q.X, q.Y, q.Z, q.W);
        }

        if (SpawnPlaces.Member(t, "Translation")?.Value is VectorValue p)
        {
            Floats(bytes, p.Offset + shift, value.Location.X, value.Location.Y, value.Location.Z);
        }

        var (sizeX, sizeY) = SpawnPlaces.SizeOf(value.Scale);
        if (kind == SpawnPlaceKind.Animal && SpawnPlaces.Member(item, "Radius")?.Value is FloatValue radius)
        {
            Floats(bytes, radius.Offset + shift, MathF.Max(sizeX, sizeY));
        }

        if (kind != SpawnPlaceKind.Zone)
        {
            return;
        }

        Set(item, "_semiXAxisSize", sizeX);
        Set(item, "_semiYAxisSize", sizeY);
        var activation = Get(item, "_zoneActivationRadiusMultiplier", 1f);
        var lpc = Get(item, "_LPCZoneActivationRadiusMultiplier", 1f);
        Box("_boundingBox", 1f);
        Box("_zoneActivationBoundingBox", activation);
        Box("_LPCZoneActivationBoundingBox", lpc);

        void Set(StructValue s, string name, float v)
        {
            if (SpawnPlaces.Member(s, name)?.Value is FloatValue f)
            {
                Floats(bytes, f.Offset + shift, v);
            }
        }

        static float Get(StructValue s, string name, float fallback) => SpawnPlaces.Member(s, name)?.Value is FloatValue f ? f.Value : fallback;

        // The axis-aligned box around the zone's ellipse (turned by its yaw), flat at the zone's height.
        void Box(string name, float multiplier)
        {
            if (SpawnPlaces.Member(item, name)?.Value is not BoxValue box)
            {
                return;
            }

            var yaw = value.Rotation.Yaw * MathF.PI / 180f;
            var (a, b) = (sizeX * multiplier, sizeY * multiplier);
            var ex = MathF.Sqrt((a * MathF.Cos(yaw) * a * MathF.Cos(yaw)) + (b * MathF.Sin(yaw) * b * MathF.Sin(yaw)));
            var ey = MathF.Sqrt((a * MathF.Sin(yaw) * a * MathF.Sin(yaw)) + (b * MathF.Cos(yaw) * b * MathF.Cos(yaw)));
            var c = value.Location;
            Floats(bytes, box.Offset + shift, c.X - ex, c.Y - ey, c.Z, c.X + ex, c.Y + ey, c.Z);
        }
    }

    internal static void Floats(byte[] bytes, int offset, params float[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + (i * 4)), values[i]);
        }
    }
}
