using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Export;

/// <summary>What bending a mesh needs: its bounds (UE centimetres), and a material the game could not draw bent (null = none).</summary>
/// <param name="Bounds">The mesh's render bounds, the range the game maps onto the curve.</param>
/// <param name="FlatMaterial">Null when every material was compiled for spline meshes; otherwise the first one that was not.</param>
/// <param name="Boxes">Its collision as boxes in its own space (<see cref="Model.PieceCollision.MeshBoxes"/>), bent with each piece.</param>
/// <param name="BodySetupGuid">The mesh's <c>BodySetupGuid</c>: written as each piece's <c>CachedMeshBodySetupGuid</c> so the game keeps the boxes.</param>
public sealed record BendMesh(BoundingBox Bounds, string? FlatMaterial = null, IReadOnlyList<CollisionBox>? Boxes = null, Formats.FGuid? BodySetupGuid = null)
{
    /// <summary>The collision profile the mesh gives a component by default (<see cref="MeshCollisionInfo.DefaultProfile"/>), or null.</summary>
    public string? DefaultProfile { get; init; }

    /// <summary>
    /// What the mesh's default collision lets through (<see cref="MeshCollisionInfo.LetsThrough"/>): <c>Pawn</c>,
    /// <c>PhysicsBody</c>; both when it has no collision body at all. Null when unknown.
    /// </summary>
    public IReadOnlyList<string>? LetsThrough { get; init; }

    /// <summary>
    /// For a mesh that collides with its own triangles and is open somewhere (SCUM's rocks and cliffs are shells open
    /// underneath): points along the open edges of the triangles it collides with, in its own space. Solid only from
    /// outside: whoever gets in through the opening stands inside the rock. Null or empty for a closed mesh.
    /// </summary>
    public IReadOnlyList<FVector>? Rim { get; init; }

    /// <summary>
    /// True for a road surface: every material comes from SCUM's gravel or asphalt road master. The game places such
    /// pieces drawing into the landscape's virtual texture (<c>RTV_Landscape</c>), which the ground shows; a copy must too.
    /// Most other masters (buildings, rocks, bridges) can also write that texture, but the game never asks them to.
    /// </summary>
    public bool DrawsIntoLandscape { get; init; }

    /// <summary>
    /// True for a gravel road or dirt runway piece (<c>M_DirtRoad_Master</c>): the game draws it only into the landscape
    /// texture (pass type Never, no shadow). On screen by itself it shows its material's placeholder, bright pink (owner,
    /// 2026-10-09: "the road I copied is purple").
    /// </summary>
    public bool OnlyIntoLandscape { get; init; }

    /// <summary>Why the mesh stays straight in an export, or null.</summary>
    public string? Problem => FlatMaterial is null ? null : $"its material {FlatMaterial[(FlatMaterial.LastIndexOf('/') + 1)..]} cannot be drawn bent in the game";
}

/// <summary>
/// Reads, once per mesh, what <see cref="BendMesh"/> holds. A mesh bends in the game only when every material's base
/// material has <c>bUsedWithSplineMeshes</c>: a cooked game has no shaders for the others and would draw the engine's
/// default material instead (SCUM's walls, bridges and roads have it; trees do not).
/// </summary>
public sealed class BendSupport(AssetCatalog catalog)
{
    /// <summary>
    /// True for the game's rocks and cliffs. They collide with their own triangles, which a bent piece cannot have in a
    /// cooked game (its collision would be rough boxes: owner, a bent cliff at B_4: "half my body is inside the rock",
    /// "he falls under the rocks"), so they are not bent: the export writes them straight.
    /// </summary>
    // ponytail: by folder (all of SCUM's rocks and cliffs live under Landscape/Rocks/); a mesh elsewhere bends as before.
    public static bool IsRock(string meshPath) => meshPath.Contains("/Landscape/Rocks/", StringComparison.OrdinalIgnoreCase);

    private readonly Dictionary<string, BendMesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private const string DirtRoadMaster = "/Game/ConZ_Files/Materials/Road/Gravel_Road/M_DirtRoad_Master";
    private const string AsphaltRoadMaster = "/Game/ConZ_Files/Materials/Road/AsphaltRoad_Master/M_Asphalt_Road_Master";
    private readonly Dictionary<string, (bool Bends, string Base)> _materials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The <see cref="BendMesh"/> of <paramref name="meshPath"/>, or null when it is not a readable static mesh. Answers are
    /// kept: the client's answers serve the server export too (same meshes; the server cook has no render geometry).
    /// </summary>
    public BendMesh? Describe(string meshPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meshPath);
        lock (_meshes)
        {
            if (!_meshes.TryGetValue(meshPath, out var known))
            {
                try
                {
                    known = Read(meshPath);
                }
                catch (ObjectDisposedException)
                {
                    known = null; // the catalog it was made for is closed
                }

                _meshes[meshPath] = known;
            }

            return known;
        }
    }

    private BendMesh? Read(string meshPath)
    {
        if (!catalog.TryLoadObject<UStaticMesh>(meshPath, out var mesh))
        {
            return null;
        }

        var info = MeshExtractor.DescribeStaticMesh(mesh);
        var materials = info.Materials.Select(m => m.MaterialPath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var flat = materials.FirstOrDefault(p => !Material(p).Bends);
        // The body first: a dedicated server's cook keeps it but strips the render geometry the columns are read from.
        MeshCollisionInfo? simple = null;
        Formats.FGuid? guid = null;
        var noBody = false;
        try
        {
            simple = MeshCollision.Read(mesh);
            noBody = simple is null;
            if (simple?.BodySetupGuid is { } g && (g.A | g.B | g.C | g.D) != 0)
            {
                guid = new Formats.FGuid(g.A, g.B, g.C, g.D);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            simple = null;
        }

        IReadOnlyList<CollisionBox>? boxes;
        try
        {
            boxes = Model.PieceCollision.MeshBoxes(MeshExtractor.ExtractStaticMesh(mesh, 0), simple);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No geometry (a server cook): the mesh's simple shapes still do; triangle-collision meshes get none here.
            boxes = simple is { Boxes.Count: > 0 } && simple.TraceFlag != "CTF_UseComplexAsSimple" ? simple.Boxes : null;
        }

        return new BendMesh(info.Bounds, flat, boxes, guid)
        {
            DefaultProfile = simple?.DefaultProfile,
            LetsThrough = simple?.LetsThrough ?? (noBody ? ["Pawn", "PhysicsBody"] : null),
            Rim = simple?.TraceFlag == "CTF_UseComplexAsSimple" ? RimOf(mesh) : null,
            DrawsIntoLandscape = materials.Count > 0 && materials.All(p => IsUnder(Material(p).Base, DirtRoadMaster) || IsUnder(Material(p).Base, AsphaltRoadMaster)),
            OnlyIntoLandscape = materials.Count > 0 && materials.All(p => IsUnder(Material(p).Base, DirtRoadMaster)),
        };
    }

    /// <summary>The open edges of the triangles the mesh collides with (<c>LODForCollision</c>): both ends and the middle of each.</summary>
    private static IReadOnlyList<FVector>? RimOf(UStaticMesh mesh)
    {
        MeshData lod;
        try
        {
            lod = MeshExtractor.ExtractStaticMesh(mesh, mesh.GetOrDefault("LODForCollision", 0));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // no geometry (a server cook)
        }

        // Corners split by UV seams are one corner: welded by position (1 mm).
        var p = lod.Positions;
        var ids = new Dictionary<(int, int, int), int>();
        var corner = new int[lod.VertexCount];
        var at = new List<FVector>();
        for (var i = 0; i < lod.VertexCount; i++)
        {
            var key = ((int)MathF.Round(p[i * 3] * 10f), (int)MathF.Round(p[(i * 3) + 1] * 10f), (int)MathF.Round(p[(i * 3) + 2] * 10f));
            if (!ids.TryGetValue(key, out var id))
            {
                ids[key] = id = at.Count;
                at.Add(new FVector(p[i * 3], p[(i * 3) + 1], p[(i * 3) + 2]));
            }

            corner[i] = id;
        }

        var edges = new Dictionary<(int, int), int>();
        for (var t = 0; t + 2 < lod.Indices.Length; t += 3)
        {
            for (var e = 0; e < 3; e++)
            {
                var (a, b) = (corner[lod.Indices[t + e]], corner[lod.Indices[t + ((e + 1) % 3)]]);
                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }

        var rim = new List<FVector>();
        foreach (var ((a, b), _) in edges.Where(e => e.Value == 1))
        {
            rim.Add(at[a]);
            rim.Add((at[a] + at[b]) * 0.5f);
            rim.Add(at[b]);
        }

        return rim;
    }

    private static bool IsUnder(string materialPath, string master) =>
        materialPath.Equals(master, StringComparison.OrdinalIgnoreCase) || materialPath.StartsWith(master + ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the material can be drawn bent, and its base material (the end of its parent chain).</summary>
    private (bool Bends, string Base) Material(string materialPath)
    {
        if (!_materials.TryGetValue(materialPath, out var known))
        {
            try
            {
                var info = new MaterialInspector(catalog).Inspect(materialPath);
                known = (info.UsedWithSplineMeshes, info.ParentChain.Count > 0 ? info.ParentChain[^1] : info.ObjectPath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                known = (false, string.Empty);
            }

            _materials[materialPath] = known;
        }

        return known;
    }
}
