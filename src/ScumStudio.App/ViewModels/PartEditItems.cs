using System.Numerics;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <summary>What the parts gizmo does when dragged (G, R, S).</summary>
public enum PartTool
{
    /// <summary>Arrows: move.</summary>
    Move,

    /// <summary>Rings: turn.</summary>
    Rotate,

    /// <summary>Boxes: scale.</summary>
    Scale,
}

/// <summary>How the 3D view draws one part while it is edited: its socket's place relative to the game's, or hidden.</summary>
/// <param name="Delta">Applied after the part's drawn transform (vehicle space: <c>drawn * Delta</c>; identity = where the game puts it).</param>
/// <param name="Hidden">Drawn hidden.</param>
public sealed record PartOverride(FTransform Delta, bool Hidden);

/// <summary>A part edit the studio refuses, with the string key that tells the user why.</summary>
public sealed class PartRefusedException(string key, string detail) : InvalidOperationException(detail)
{
    /// <summary>Localization key of the reason (<c>Studio.Parts.WheelNote</c> …).</summary>
    public string Key { get; } = key;
}

/// <summary>A part of a vehicle clone as the parts studio edits it.</summary>
/// <param name="Attachment">The clone's attachment package (<c>…/Attachments/BPC_Hunter_Door_FrontLeft</c>).</param>
/// <param name="Label">Readable name (<c>Door FrontLeft</c>).</param>
/// <param name="Socket">Its ParentSocket (a socket of the vehicle mesh, or a bone for wheels).</param>
/// <param name="Game">The socket where the game has it (skeleton or stock mesh), null for a bone.</param>
/// <param name="Place">The socket where the project has it now.</param>
/// <param name="Mesh">Its model (<c>MeshSetup.Mesh</c>).</param>
public sealed record StudioPart(string Attachment, string Label, string Socket, VehicleSocket? Game, VehicleSocket? Place, string? Mesh)
{
    /// <summary>True for a wheel: it rolls on the car's physics bones, its place is not data.</summary>
    public bool IsWheel => Game is null || Socket.StartsWith("b_Wheel", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The parts of one vehicle clone, journaled (owner 2026-10-12: "every part of the car editable like Blender"): a part's
/// place is its ParentSocket on the clone's own copy of the vehicle mesh (<see cref="VehicleSockets"/>; the skeleton stays
/// the game's), hide = scale 0.001 (the part keeps its function), remove = its slot empty in every cloned spawn preset,
/// another model = <c>MeshSetup.Mesh</c>, a new part = a renamed copy of the vehicle's roof rack with its own model and
/// socket in a new slot of the part that holds the roof rack (<see cref="VehicleAttachmentEdits"/>). Each user step is
/// one <see cref="BatchOp"/>. No UI: the Vehicles page drives it.
/// </summary>
/// <param name="clone">The vehicle clone.</param>
/// <param name="read">Reads a package as the mod starts from (stock, or the in-memory clone of its template), null when missing.</param>
/// <param name="state">The project's edits.</param>
public sealed class PartsStudio(CloneAssetOp clone, Func<string, CookedPackage?> read, EditState state)
{
    private const string Text = "Text";

    /// <summary>The clone.</summary>
    public CloneAssetOp Clone { get; } = clone;

    /// <summary>The clone's own copy of the vehicle mesh, or null when the clone shares the game's (cloned without paint).</summary>
    public string? Mesh
    {
        get
        {
            var stock = read(Clone.Template) is { } vehicle ? VehicleSockets.ImportedPackage(vehicle, "SkeletalMesh") : null;
            return stock is null ? null : Clone.Packages.FirstOrDefault(p => string.Equals(p.Old, stock, StringComparison.OrdinalIgnoreCase))?.New;
        }
    }

    /// <summary>The clone package of a stock attachment (the 3D view is drawn from the template), or null.</summary>
    public string? CloneOf(string stockAttachment) =>
        Clone.Packages.FirstOrDefault(p => string.Equals(p.Old, stockAttachment, StringComparison.OrdinalIgnoreCase))?.New;

    /// <summary>
    /// The part drawn from <paramref name="stockAttachment"/> (or a part "Add part…" bolted on, by its own package), or null
    /// when the clone has no copy of it.
    /// </summary>
    public StudioPart? Select(string stockAttachment)
    {
        if ((CloneOf(stockAttachment) ?? (IsAdded(stockAttachment) ? stockAttachment : null)) is not { } attachment || read(attachment) is not { } part)
        {
            return null;
        }

        var cdo = Defaults(part);
        var socket = Current(attachment, cdo, VehicleAttachmentEdits.SocketPath, VehicleAttachmentEdits.ParentSocketOf(part) ?? string.Empty);
        var mesh = Current(attachment, cdo, VehicleAttachmentEdits.MeshPath, VehicleAttachmentEdits.MeshOf(part) ?? string.Empty);
        var game = GameSocket(socket) ?? AddedSocket(socket);
        return new StudioPart(attachment, VehicleParts.Label(VehicleAttachmentEdits.ClassPath(attachment)), socket, game, game is null || Mesh is null ? game : Placed(game), mesh.Length > 0 ? mesh : null);
    }

    /// <summary>The parts "Add part…" bolted onto this clone (each a renamed copy of its roof rack), where the project has them now.</summary>
    public IReadOnlyList<StudioPart> AddedParts() =>
        state.AssetClones.Where(c => c.Kind == VehicleAttachmentEdits.PartKind && IsAdded(c.NewPrimary))
            .Select(c => Select(c.NewPrimary))
            .OfType<StudioPart>()
            .ToList();

    /// <summary>True for a part package "Add part…" made for this clone (<c>…/BPC_&lt;clone&gt;_CustomA</c>).</summary>
    public bool IsAdded(string package)
    {
        var token = PackageMap.Leaf(Clone.NewPrimary)["BPC_".Length..];
        return PackageMap.Leaf(package).StartsWith("BPC_" + token + "_Custom", StringComparison.OrdinalIgnoreCase)
               && state.AssetClones.Any(c => c.Kind == VehicleAttachmentEdits.PartKind && string.Equals(c.NewPrimary, package, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when <paramref name="part"/> no longer spawns with the clone: every cloned spawn preset that had it in a slot has
    /// that slot emptied (<see cref="Remove"/>). The 3D view hides it.
    /// </summary>
    public bool IsRemoved(StudioPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var cls = VehicleAttachmentEdits.ClassPath(part.Attachment);
        var had = false;
        foreach (var (path, preset) in Presets())
        {
            foreach (var slot in VehicleParts.Read(preset, _ => null).Where(s => string.Equals(s.Value, cls, StringComparison.OrdinalIgnoreCase)))
            {
                had = true;
                if (string.Equals(state.GetAssetValue(path, slot.Export, slot.Path)?.Current ?? slot.Value, cls, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return had;
    }

    /// <summary>The step that puts <paramref name="part"/>'s socket at a new place (socket space: cm, degrees, scale).</summary>
    /// <exception cref="PartRefusedException">A wheel, or a clone that shares the vehicle mesh with the game.</exception>
    public BatchOp? Move(StudioPart part, Vector3 location, Vector3 rotation, Vector3 scale)
    {
        var mesh = Editable(part);
        var game = part.Game!;
        var ops = new List<EditOp>();
        var key = MeshKey(mesh);
        void Set(string member, string kind, string value, string stockValue)
        {
            var path = VehicleSockets.Path(part.Socket, member);
            var old = state.GetAssetValue(mesh, key, path)?.Current ?? stockValue;
            if (!TunableValue.AreEqual(Enum.TryParse<TunableKind>(kind, out var k) ? k : TunableKind.Text, old, value))
            {
                ops.Add(new SetAssetValueOp(mesh, key, path, kind, old, value));
            }
        }

        // A socket the game keeps on the skeleton gets a copy on the clone's mesh: every member starts absent ("").
        var own = game.OnMesh;
        Set(VehicleSockets.Bone, Text, game.Bone, own ? game.Bone : string.Empty);
        Set(VehicleSockets.Location, nameof(TunableKind.Vector), VehicleSockets.Format(location), own ? VehicleSockets.Format(game.Location) : string.Empty);
        Set(VehicleSockets.Rotation, nameof(TunableKind.Rotator), VehicleSockets.Format(rotation), own ? VehicleSockets.Format(game.Rotation) : string.Empty);
        Set(VehicleSockets.Scale, nameof(TunableKind.Vector), VehicleSockets.Format(scale), own ? VehicleSockets.Format(game.Scale) : string.Empty);
        return ops.Count == 0 ? null : new BatchOp($"Move {part.Label}", ops);
    }

    /// <summary>The step that hides the part (scaled to 0.001: it stays on the car and keeps its function) or shows it again.</summary>
    public BatchOp? Hide(StudioPart part, bool hidden)
    {
        var place = part.Place ?? part.Game ?? throw new PartRefusedException("Studio.Parts.WheelNote", $"{part.Label} hangs on a bone.");
        return Move(part, place.Location, place.Rotation, hidden ? new Vector3(0.001f) : part.Game?.Scale is { X: > 0.001f } scale ? scale : Vector3.One);
    }

    /// <summary>The step that puts the part back where the game has it (its socket overrides removed).</summary>
    public BatchOp? Reset(StudioPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var cls = VehicleAttachmentEdits.ClassPath(part.Attachment);
        var ops = new List<EditOp>();
        foreach (var (path, _) in Presets())
        {
            // A part taken off the spawn comes back too (the slots Remove emptied).
            ops.AddRange(state.GetAssetValues(path)
                .Where(v => v.Current.Length == 0 && string.Equals(v.Base, cls, StringComparison.OrdinalIgnoreCase))
                .Select(v => (EditOp)new SetAssetValueOp(path, v.Export, v.Path, v.ValueKind, v.Current, v.Base)));
        }

        var mesh = ops.Count > 0 && (part.IsWheel || Mesh is null) ? null : Editable(part);
        var key = mesh is null ? string.Empty : MeshKey(mesh);
        if (mesh is not null && key.Length > 0)
        {
            ops.AddRange(state.GetAssetValues(mesh)
                .Where(v => VehicleSockets.Parse(v.Path)?.Socket == part.Socket)
                .Select(v => (EditOp)new SetAssetValueOp(mesh, v.Export, v.Path, v.ValueKind, v.Current, v.Base)));
        }

        return ops.Count == 0 ? null : new BatchOp($"Reset {part.Label}", ops);
    }

    /// <summary>
    /// The step that spawns the clone without the part: its slot is emptied in every cloned spawn preset. A part "Add part…"
    /// bolted on is taken off whole instead (its copy, its socket, its slot and its preset entries), as if never added.
    /// </summary>
    public BatchOp? Remove(StudioPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (IsAdded(part.Attachment))
        {
            return RemoveAdded(part);
        }

        Editable(part, needsMesh: false);
        var cls = VehicleAttachmentEdits.ClassPath(part.Attachment);
        var ops = new List<EditOp>();
        foreach (var (path, preset) in Presets())
        {
            foreach (var slot in VehicleParts.Read(preset, _ => null))
            {
                var current = state.GetAssetValue(path, slot.Export, slot.Path)?.Current ?? slot.Value;
                if (string.Equals(current, cls, StringComparison.OrdinalIgnoreCase))
                {
                    ops.Add(new SetAssetValueOp(path, slot.Export, slot.Path, nameof(TunableKind.Part), current, string.Empty));
                }
            }
        }

        return ops.Count == 0 ? null : new BatchOp($"Remove {part.Label}", ops);
    }

    /// <summary>The step that takes an added part off: every value of the add set back, then its copy removed.</summary>
    private BatchOp RemoveAdded(StudioPart part)
    {
        var cls = VehicleAttachmentEdits.ClassPath(part.Attachment);
        var ops = new List<EditOp>();
        void Back(AssetValueOverride v) => ops.Add(new SetAssetValueOp(v.Package, v.Export, v.Path, v.ValueKind, v.Current, v.Base));
        foreach (var v in state.GetAssetValues(part.Attachment))
        {
            Back(v);
        }

        if (Mesh is { } mesh)
        {
            foreach (var v in state.GetAssetValues(mesh).Where(v => VehicleSockets.Parse(v.Path)?.Socket == part.Socket))
            {
                Back(v);
            }
        }

        foreach (var package in Clone.Packages.Select(p => p.New))
        {
            foreach (var v in state.GetAssetValues(package).Where(v => v.Current.StartsWith(cls, StringComparison.OrdinalIgnoreCase)))
            {
                Back(v); // the holder's new slot ("<class>|<donor slot>") and the presets' Children entries
            }
        }

        var copy = state.AssetClones.First(c => string.Equals(c.NewPrimary, part.Attachment, StringComparison.OrdinalIgnoreCase));
        ops.Add(new RemoveAssetCloneOp(copy.NewPrimary, copy));
        return new BatchOp($"Remove {part.Label}", ops);
    }

    /// <summary>The socket of a part "Add part…" bolted on: the clone mesh's own, as the project has it (null for any other).</summary>
    private VehicleSocket? AddedSocket(string socket)
    {
        if (Mesh is not { } mesh || MeshKey(mesh) is not { Length: > 0 } key
            || state.GetAssetValue(mesh, key, VehicleSockets.Path(socket, VehicleSockets.Location))?.Current is not { Length: > 0 } location)
        {
            return null;
        }

        Vector3 Get(string member, Vector3 fallback) =>
            state.GetAssetValue(mesh, key, VehicleSockets.Path(socket, member))?.Current is { Length: > 0 } v && TunableValue.ParseFloats(v, 3) is var f
                ? new Vector3(f[0], f[1], f[2])
                : fallback;
        var at = TunableValue.ParseFloats(location, 3);
        return new VehicleSocket(socket, state.GetAssetValue(mesh, key, VehicleSockets.Path(socket, VehicleSockets.Bone))?.Current ?? "Root",
            new Vector3(at[0], at[1], at[2]), Get(VehicleSockets.Rotation, Vector3.Zero), Get(VehicleSockets.Scale, Vector3.One), OnMesh: true);
    }

    /// <summary>The step that shows another model of the game for the part (same kind of model: static or skeletal).</summary>
    /// <exception cref="InvalidOperationException">A model of the other kind.</exception>
    public BatchOp? SwapMesh(StudioPart part, string meshObjectPath, Func<string, string?> classOf)
    {
        Editable(part, needsMesh: false);
        var package = read(part.Attachment) ?? throw new InvalidOperationException($"{part.Attachment} cannot be read.");
        var edit = VehicleAttachmentEdits.SwapMesh(package, meshObjectPath, classOf);
        var old = state.GetAssetValue(part.Attachment, edit.Export, edit.Path)?.Current ?? VehicleAttachmentEdits.MeshOf(package) ?? string.Empty;
        return string.Equals(old, meshObjectPath, StringComparison.OrdinalIgnoreCase)
            ? null
            : new BatchOp($"Model of {part.Label}", [new SetAssetValueOp(part.Attachment, edit.Export, edit.Path, Text, old, meshObjectPath)]);
    }

    /// <summary>
    /// The step that bolts <paramref name="meshObjectPath"/> onto the car at <paramref name="location"/> (vehicle space, cm):
    /// a copy of the roof rack (<see cref="VehicleAttachmentEdits.DefaultDonor"/>) with that model on a new socket of the
    /// clone's mesh, in a new slot of the part that holds the roof rack, spawned by every cloned spawn preset.
    /// </summary>
    /// <param name="meshObjectPath">The model (<c>/Game/…/SM_X.SM_X</c>).</param>
    /// <param name="location">Where it goes (vehicle space, cm).</param>
    /// <param name="exists">True for a package path the game or the project already has.</param>
    /// <exception cref="PartRefusedException">The clone shares the vehicle mesh with the game.</exception>
    /// <exception cref="InvalidOperationException">The vehicle has no roof rack to copy, or no part holds it.</exception>
    public (BatchOp Step, string Package) Add(string meshObjectPath, Vector3 location, Func<string, bool> exists)
    {
        var mesh = Mesh ?? throw new PartRefusedException("Studio.Parts.SharedMesh", $"{Clone.NewPrimary} shares the vehicle mesh with the game.");
        var donor = Clone.Packages.FirstOrDefault(p => p.New.Contains("/Attachments/", StringComparison.OrdinalIgnoreCase)
                        && p.New.EndsWith("_" + VehicleAttachmentEdits.DefaultDonor, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"{Clone.NewPrimary} has no {VehicleAttachmentEdits.DefaultDonor} to copy a part from.");
        var donorClass = VehicleAttachmentEdits.ClassPath(donor.New);
        var (holder, donorSlot) = ((string?)null, -1);
        foreach (var p in Clone.Packages.Where(p => p.New.Contains("/Attachments/", StringComparison.OrdinalIgnoreCase)))
        {
            var slots = read(p.New) is { } pkg ? VehicleAttachmentEdits.SlotsOf(pkg) : [];
            for (var i = 0; i < slots.Count && holder is null; i++)
            {
                (holder, donorSlot) = slots[i].Contains(donorClass, StringComparer.OrdinalIgnoreCase) ? (p.New, i) : (null, -1);
            }
        }

        if (holder is null || read(holder) is not { } holderPackage || read(donor.Old) is not { } donorPackage)
        {
            throw new InvalidOperationException($"No part of {Clone.NewPrimary} holds its {VehicleAttachmentEdits.DefaultDonor}.");
        }

        var taken = state.AssetClones.SelectMany(c => c.Packages.Select(p => p.New)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var token = PackageMap.Leaf(Clone.NewPrimary)["BPC_".Length..];
        var package = VehicleAttachmentEdits.CustomName(PackageMap.Folder(donor.New), token, p => taken.Contains(p) || exists(p));
        var cls = VehicleAttachmentEdits.ClassPath(package);
        var socket = VehicleAttachmentEdits.CustomSocket(package);
        var cdo = "Default__" + PackageMap.Leaf(package) + "_C";

        // The new slot comes after the holder's own and those earlier steps appended (the preset node's Children follow the slots).
        var holderCdo = Defaults(holderPackage);
        var slot = VehicleAttachmentEdits.SlotsOf(holderPackage).Count + state.GetAssetValues(holder).Count(v => v.Path.StartsWith(VehicleAttachmentEdits.SlotsPrefix, StringComparison.Ordinal));
        var holderClass = VehicleAttachmentEdits.ClassPath(holder);
        var key = MeshKey(mesh);
        var ops = new List<EditOp>
        {
            new CloneAssetOp(VehicleAttachmentEdits.PartKind, donor.Old, package, [new PackagePair(donor.Old, package)]),
            new SetAssetValueOp(package, cdo, VehicleAttachmentEdits.MeshPath, Text, VehicleAttachmentEdits.MeshOf(donorPackage) ?? string.Empty, meshObjectPath),
            new SetAssetValueOp(package, cdo, VehicleAttachmentEdits.SocketPath, Text, VehicleAttachmentEdits.ParentSocketOf(donorPackage) ?? string.Empty, socket),
            new SetAssetValueOp(mesh, key, VehicleSockets.Path(socket, VehicleSockets.Bone), Text, string.Empty, "Root"),
            new SetAssetValueOp(mesh, key, VehicleSockets.Path(socket, VehicleSockets.Location), nameof(TunableKind.Vector), string.Empty, VehicleSockets.Format(location)),
            new SetAssetValueOp(holder, holderCdo, VehicleAttachmentEdits.SlotPath(slot), Text, string.Empty, VehicleAttachmentEdits.SlotValue(cls, donorSlot)),
        };
        foreach (var (path, preset) in Presets())
        {
            if (VehicleParts.Read(preset, _ => null).FirstOrDefault(t => string.Equals(t.ExportClass, holderClass, StringComparison.OrdinalIgnoreCase)) is { } node)
            {
                ops.Add(new SetAssetValueOp(path, node.Export, $"Children[{slot}]", nameof(TunableKind.Part), string.Empty, cls));
            }
        }

        return (new BatchOp($"Add part {PackageMap.Leaf(meshObjectPath.Split('.')[0])}", ops), package);
    }

    /// <summary>The 3D view's override of a part: its project place relative to the game's (null: where the game has it).</summary>
    public static PartOverride? OverrideOf(StudioPart part)
    {
        if (part.Game is not { } game || part.Place is not { } place || place == game)
        {
            return null;
        }

        var delta = Transform(game).Inverse() * Transform(place);
        return new PartOverride(delta, place.Scale.X <= 0.001f && place.Scale.Y <= 0.001f && place.Scale.Z <= 0.001f);
    }

    /// <summary>A socket's place as a transform (UE space of its bone).</summary>
    public static FTransform Transform(VehicleSocket s) =>
        new(new FRotator(s.Rotation.X, s.Rotation.Y, s.Rotation.Z), new FVector(s.Location.X, s.Location.Y, s.Location.Z), new FVector(s.Scale.X, s.Scale.Y, s.Scale.Z));

    /// <summary>
    /// The place after a gizmo drag of <paramref name="delta"/> (vehicle space): moved by its translation, turned about the
    /// part's own pivot, scaled by its scale. SCUM's car sockets hang on Root, whose space is the vehicle's.
    /// </summary>
    public static (Vector3 Location, Vector3 Rotation, Vector3 Scale) Apply(VehicleSocket place, FTransform delta)
    {
        var turned = (delta.Rotation * Transform(place).Rotation).Rotator();
        return (place.Location + new Vector3(delta.Translation.X, delta.Translation.Y, delta.Translation.Z),
            new Vector3(turned.Pitch, turned.Yaw, turned.Roll),
            place.Scale * new Vector3(delta.Scale3D.X, delta.Scale3D.Y, delta.Scale3D.Z));
    }

    private string Editable(StudioPart part, bool needsMesh = true)
    {
        if (!needsMesh)
        {
            return string.Empty;
        }

        var mesh = Mesh ?? throw new PartRefusedException("Studio.Parts.SharedMesh", $"{Clone.NewPrimary} shares the vehicle mesh with the game.");
        return part.IsWheel ? throw new PartRefusedException("Studio.Parts.WheelNote", $"{part.Label} rolls on the bone {part.Socket}.") : mesh;
    }

    /// <summary>The game's socket: the vehicle mesh's own (the clone's copy, or the game's when shared), else the skeleton's; null for a bone.</summary>
    private VehicleSocket? GameSocket(string socket)
    {
        var mesh = Mesh ?? (read(Clone.Template) is { } vehicle ? VehicleSockets.ImportedPackage(vehicle, "SkeletalMesh") : null);
        if (mesh is null || read(mesh) is not { } package)
        {
            return null;
        }

        return VehicleSockets.Read(package).FirstOrDefault(s => s.Name == socket)
               ?? (VehicleSockets.ImportedPackage(package, "Skeleton") is { } skeleton && read(skeleton) is { } k ? VehicleSockets.Read(k).FirstOrDefault(s => s.Name == socket) : null);
    }

    /// <summary>The socket with the project's overrides of the clone mesh applied.</summary>
    private VehicleSocket Placed(VehicleSocket game)
    {
        var mesh = Mesh!;
        Vector3 Get(string member, Vector3 fallback) =>
            state.GetAssetValue(mesh, MeshKey(mesh), VehicleSockets.Path(game.Name, member))?.Current is { Length: > 0 } v && TunableValue.ParseFloats(v, 3) is var f
                ? new Vector3(f[0], f[1], f[2])
                : fallback;
        return game with { Location = Get(VehicleSockets.Location, game.Location), Rotation = Get(VehicleSockets.Rotation, game.Rotation), Scale = Get(VehicleSockets.Scale, game.Scale) };
    }

    private string Current(string package, string export, string path, string fallback) => state.GetAssetValue(package, export, path)?.Current ?? fallback;

    private string MeshKey(string mesh) => read(mesh) is { } p && VehicleSockets.MeshExport(p) is var i and >= 0 ? TunableReader.ExportKeys(p)[i] : string.Empty;

    private IEnumerable<(string Path, CookedPackage Package)> Presets() =>
        Clone.Packages.Where(p => p.New.StartsWith(CloneFamilyPlanner.ManualSpawnPresets + "/", StringComparison.OrdinalIgnoreCase))
            .Select(p => (p.New, Package: read(p.New)))
            .Where(p => p.Package is not null)
            .Select(p => (p.New, p.Package!));

    private static string Defaults(CookedPackage package) =>
        TunableReader.ExportKeys(package).FirstOrDefault(k => k.StartsWith("Default__", StringComparison.Ordinal)) ?? string.Empty;
}
