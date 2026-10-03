using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Level.Export;

/// <summary>What bending a mesh needs: its bounds (UE centimetres), and a material the game could not draw bent (null = none).</summary>
/// <param name="Bounds">The mesh's render bounds, the range the game maps onto the curve.</param>
/// <param name="FlatMaterial">Null when every material was compiled for spline meshes; otherwise the first one that was not.</param>
/// <param name="Boxes">Its collision as boxes in its own space (<see cref="Model.PieceCollision.MeshBoxes"/>), bent with each piece.</param>
/// <param name="BodySetupGuid">The mesh's <c>BodySetupGuid</c>: written as each piece's <c>CachedMeshBodySetupGuid</c> so the game keeps the boxes.</param>
public sealed record BendMesh(BoundingBox Bounds, string? FlatMaterial = null, IReadOnlyList<CollisionBox>? Boxes = null, Formats.FGuid? BodySetupGuid = null)
{
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
    private readonly Dictionary<string, BendMesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _materials = new(StringComparer.OrdinalIgnoreCase);

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
        var flat = info.Materials.Select(m => m.MaterialPath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(p => !BendsInGame(p));
        // The body first: a dedicated server's cook keeps it but strips the render geometry the columns are read from.
        MeshCollisionInfo? simple = null;
        Formats.FGuid? guid = null;
        try
        {
            simple = MeshCollision.Read(mesh);
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

        return new BendMesh(info.Bounds, flat, boxes, guid);
    }

    private bool BendsInGame(string materialPath)
    {
        if (!_materials.TryGetValue(materialPath, out var bends))
        {
            try
            {
                bends = new MaterialInspector(catalog).Inspect(materialPath).UsedWithSplineMeshes;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                bends = false;
            }

            _materials[materialPath] = bends;
        }

        return bends;
    }
}
