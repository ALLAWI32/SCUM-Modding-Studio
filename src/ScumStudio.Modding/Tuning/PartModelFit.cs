using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Tuning;

/// <summary>
/// Puts another model in the place of a vehicle part's own (Discord 2026-10-19, defolt_0999: "I replaced all four wheels
/// with Zil wheels … this is what I saw in the game": huge wheels, one lying behind the truck, one standing in the bed).
/// The game draws a part's model (<c>MeshSetup.Mesh</c>) on its socket or bone with the model's own pivot and size; the
/// ZIL-131 prop wheel (<c>SM_Wheel_01</c>) has its pivot at the bottom of the tyre, its axle along X and is 117 cm across,
/// the Rager's wheels are centred on the axle bone, axle along Y, 90 cm. So the part's <c>MeshSetup.Offset</c> (a
/// <c>Transform</c> the game puts between the socket and the model, as on the Kinglet's weapon mounts) is set to the fit:
/// the new model's bounds centred on the game model's, scaled uniformly to its largest size, and for a wheel its axle
/// turned onto the game wheel's (the side of the tyre nearer its pivot towards the side the game wheel's pivot is on, the
/// outside on SCUM's cars). A wheel keeps its driving size either way: the radius is the wheel class's
/// <c>WheelData.Radius</c> (Rager 45 cm), not the model.
/// </summary>
public static class PartModelFit
{
    /// <summary>The two largest sizes of a round part are within this ratio (a wheel: 90.19 × 90.18 cm).</summary>
    private const float RoundRatio = 0.85f;

    /// <summary>A round part's axle is at most this share of its diameter (a Rager wheel: 32.5 of 90.2 cm).</summary>
    private const float AxleRatio = 0.8f;

    /// <summary>The pivot counts as off-centre along the axle beyond this share of the axle's length.</summary>
    private const float SideRatio = 0.02f;

    /// <summary>True for a part that hangs on a wheel bone (<c>b_Wheel_FrontLeft</c>, <c>b_Wheel_R</c>): it turns with the wheel.</summary>
    public static bool IsWheelSocket(string? parentSocket) => parentSocket?.StartsWith("b_Wheel", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The transform that puts a model with bounds <paramref name="model"/> where one with bounds <paramref name="game"/> is
    /// (both in their own model space): centred on it, scaled uniformly to its largest size, and for a
    /// <paramref name="wheel"/> whose two models are both round, its axle turned onto the game model's. Null when either
    /// box is empty or flat (nothing to fit to).
    /// </summary>
    public static FTransform? Fit(BoundingBox game, BoundingBox model, bool wheel)
    {
        if (!Usable(game) || !Usable(model))
        {
            return null;
        }

        var turn = FQuat.Identity;
        if (wheel && AxleOf(game.Size) is { } gameAxle && AxleOf(model.Size) is { } modelAxle)
        {
            var from = Unit(modelAxle) * SideOf(model, modelAxle);
            var to = Unit(gameAxle) * SideOf(game, gameAxle);
            turn = FQuat.FindBetweenNormals(from, to);
        }

        var turned = turn.RotateVector(FVector.FromVector3(model.Size));
        var largest = MathF.Max(MathF.Abs(turned.X), MathF.Max(MathF.Abs(turned.Y), MathF.Abs(turned.Z)));
        var scale = Largest(game.Size) / largest;
        var translation = FVector.FromVector3(game.Center) - turn.RotateVector(FVector.FromVector3(model.Center) * scale);
        return new FTransform(turn, translation, new FVector(scale));
    }

    /// <summary>The bounds of <paramref name="box"/> once moved by <paramref name="transform"/> (its eight corners).</summary>
    public static BoundingBox Apply(FTransform transform, BoundingBox box)
    {
        var result = BoundingBox.Empty;
        for (var i = 0; i < 8; i++)
        {
            var corner = new FVector((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
            result = result.Include(transform.TransformPosition(corner).ToVector3());
        }

        return result;
    }

    /// <summary>
    /// The bounds of a static or skeletal mesh as the game cooked them (<c>RenderData.Bounds</c> / <c>ImportedBounds</c>;
    /// a server cook keeps them too), or null when it cannot be read.
    /// </summary>
    public static BoundingBox? BoundsOf(AssetCatalog catalog, string meshObjectPath, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            var path = meshObjectPath.Contains('.', StringComparison.Ordinal) ? meshObjectPath : meshObjectPath + "." + PackageMap.Leaf(meshObjectPath);
            if (!catalog.PackageExists(VehicleParts.PackageOf(path)) || !catalog.TryLoadObject<UObject>(path, out var mesh) || !MeshExtractor.IsMesh(mesh))
            {
                return null;
            }

            var box = MeshExtractor.Describe(mesh).Bounds;
            return Usable(box) ? box : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger?.LogDebug("Bounds of {Mesh}: {Message}", meshObjectPath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The <c>#MeshSetup.Offset</c> edit that fits the new model an edit set gives <paramref name="attachment"/> (a part's
    /// package as cloned, before the edits) in place of its own: null when the edits change no model, already set the
    /// offset, or bring back the part's own model. <paramref name="boundsOf"/> reads a mesh's bounds by its object path
    /// (the game's, through <paramref name="gamePathOf"/> for a model the clone renamed).
    /// </summary>
    /// <returns>The edit, or (null, a reason) when the new model cannot be fitted (its or the part's size is unknown).</returns>
    public static (TunableEdit? Edit, string? Problem) OffsetEdit(CookedPackage attachment, IReadOnlyList<TunableEdit> edits, Func<string, BoundingBox?> boundsOf,
        Func<string, string>? gamePathOf = null)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(boundsOf);
        var swap = edits.LastOrDefault(e => e.Path == VehicleAttachmentEdits.MeshPath);
        if (swap is null || edits.Any(e => e.Path == VehicleAttachmentEdits.OffsetPath) || VehicleAttachmentEdits.MeshOf(attachment) is not { } own)
        {
            return (null, null);
        }

        var model = swap.Value.Trim();
        own = gamePathOf?.Invoke(own) ?? own;
        if (model.Length == 0 || string.Equals(model, own, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var wheel = IsWheelSocket(VehicleAttachmentEdits.ParentSocketOf(attachment));
        if (boundsOf(own) is not { } game || boundsOf(model) is not { } box || Fit(game, box, wheel) is not { } fit)
        {
            return (null, $"the size of {Leaf(model)} or of {Leaf(own)} could not be read, so {Leaf(model)} keeps its own size and pivot (check it in the game)");
        }

        var offset = fit * (VehicleAttachmentEdits.OffsetOf(attachment) ?? FTransform.Identity);
        return (new TunableEdit(swap.Export, VehicleAttachmentEdits.OffsetPath, Format(offset)), null);
    }

    /// <summary>A transform as edit text: <c>tx, ty, tz | qx, qy, qz, qw | sx, sy, sz</c>.</summary>
    public static string Format(FTransform t) => string.Join(" | ",
        TunableValue.Format(t.Translation.X, t.Translation.Y, t.Translation.Z),
        TunableValue.Format(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W),
        TunableValue.Format(t.Scale3D.X, t.Scale3D.Y, t.Scale3D.Z));

    /// <summary>Reads <see cref="Format"/>'s text.</summary>
    /// <exception cref="FormatException">Not three groups of 3, 4 and 3 numbers.</exception>
    public static FTransform Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var groups = text.Split('|');
        if (groups.Length != 3)
        {
            throw new FormatException($"'{text}' is not 'translation | rotation | scale'.");
        }

        var t = TunableValue.ParseFloats(groups[0], 3);
        var q = TunableValue.ParseFloats(groups[1], 4);
        var s = TunableValue.ParseFloats(groups[2], 3);
        return new FTransform(new FQuat(q[0], q[1], q[2], q[3]), new FVector(t[0], t[1], t[2]), new FVector(s[0], s[1], s[2]));
    }

    /// <summary>The axle of a round box (0 = X, 1 = Y, 2 = Z): its short side when the other two are about equal; null otherwise.</summary>
    internal static int? AxleOf(Vector3 size)
    {
        var sides = new[] { size.X, size.Y, size.Z };
        var order = new[] { 0, 1, 2 }.OrderByDescending(i => sides[i]).ToArray();
        var (large, middle, small) = (sides[order[0]], sides[order[1]], sides[order[2]]);
        return large > 0 && middle >= RoundRatio * large && small <= AxleRatio * middle ? order[2] : null;
    }

    /// <summary>+1 or -1: the side of the box's centre, along <paramref name="axis"/>, its pivot (the origin) is on (+1 when centred).</summary>
    private static float SideOf(BoundingBox box, int axis)
    {
        var centre = Component(box.Center, axis);
        var length = Component(box.Size, axis);
        return MathF.Abs(centre) <= SideRatio * length ? 1f : -MathF.Sign(centre);
    }

    private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static FVector Unit(int axis) => axis switch { 0 => FVector.Forward, 1 => FVector.Right, _ => FVector.Up };

    private static float Largest(Vector3 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));

    private static bool Usable(BoundingBox box) =>
        !box.IsEmpty && float.IsFinite(box.Min.X) && float.IsFinite(box.Max.X) && float.IsFinite(box.Min.Y) && float.IsFinite(box.Max.Y)
        && float.IsFinite(box.Min.Z) && float.IsFinite(box.Max.Z) && Largest(box.Size) > 0.01f;

    private static string Leaf(string objectPath) => PackageMap.Leaf(VehicleParts.PackageOf(objectPath));
}
