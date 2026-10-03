using System.Numerics;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Model;

/// <summary>Mesh axis that runs along the spline (<c>ESplineMeshAxis</c>).</summary>
public enum SplineMeshAxis
{
    /// <summary>The mesh's X axis follows the spline (engine default).</summary>
    X,

    /// <summary>The mesh's Y axis follows the spline.</summary>
    Y,

    /// <summary>The mesh's Z axis follows the spline.</summary>
    Z,
}

/// <summary>
/// The bend of a <c>SplineMeshComponent</c>, as cooked: <c>SplineParams</c> plus the component's own
/// <c>SplineUpDir</c>, <c>ForwardAxis</c>, <c>bSmoothInterpRollScale</c> and <c>SplineBoundaryMin/Max</c>. Positions and
/// tangents are in component space (centimetres); rolls are radians; scales and offsets are (X, Y) across the spline.
/// Values the package does not store are the engine's defaults (scale 1, up (0, 0, 1), axis X, no smoothing, no boundary).
/// </summary>
/// <remarks>
/// SCUM's roads, road borders and river banks are landscape spline segments: each segment is one of these components
/// owned by the landscape streaming proxy, attached to its <c>LandscapeSplinesComponent</c>. The game bends the mesh
/// along the cubic Hermite curve (<c>StartPos</c>, <c>StartTangent</c>) → (<c>EndPos</c>, <c>EndTangent</c>) in the
/// vertex shader; a viewer that draws the plain mesh at the component transform shows disconnected slices.
/// </remarks>
public sealed record SplineMeshParams
{
    /// <summary>Start of the curve (component space).</summary>
    public FVector StartPos { get; init; }

    /// <summary>Tangent at the start (its length matters: Hermite).</summary>
    public FVector StartTangent { get; init; }

    /// <summary>Cross-section scale at the start (X, Y across the spline).</summary>
    public Vector2 StartScale { get; init; } = Vector2.One;

    /// <summary>Roll around the spline at the start, radians.</summary>
    public float StartRoll { get; init; }

    /// <summary>Cross-section offset at the start (X, Y across the spline), centimetres.</summary>
    public Vector2 StartOffset { get; init; }

    /// <summary>End of the curve (component space).</summary>
    public FVector EndPos { get; init; }

    /// <summary>Tangent at the end.</summary>
    public FVector EndTangent { get; init; }

    /// <summary>Cross-section scale at the end.</summary>
    public Vector2 EndScale { get; init; } = Vector2.One;

    /// <summary>Roll at the end, radians.</summary>
    public float EndRoll { get; init; }

    /// <summary>Cross-section offset at the end, centimetres.</summary>
    public Vector2 EndOffset { get; init; }

    /// <summary>Up direction of the spline frame (default Z up).</summary>
    public FVector SplineUpDir { get; init; } = FVector.Up;

    /// <summary>Mesh axis that runs along the spline.</summary>
    public SplineMeshAxis ForwardAxis { get; init; } = SplineMeshAxis.X;

    /// <summary><c>bSmoothInterpRollScale</c>: roll, scale and offset use a smooth-step of the spline ratio.</summary>
    public bool SmoothInterpRollScale { get; init; }

    /// <summary>Lower end of the mesh range mapped onto the spline; with <see cref="SplineBoundaryMax"/> equal, the mesh bounds are used.</summary>
    public float SplineBoundaryMin { get; init; }

    /// <summary>Upper end of the mesh range mapped onto the spline.</summary>
    public float SplineBoundaryMax { get; init; }
}
