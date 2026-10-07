using System.Numerics;

namespace ScumStudio.Rendering.SceneGraph;

/// <summary>
/// A world-space line segment drawn on top of the scene (no depth test), in the renderer's GL space: used for the
/// transform gizmo and other editor helpers. Colours are linear RGBA. Drawn as a screen-space quad of
/// <paramref name="Width"/> pixels with anti-aliased edges, so it looks the same on every driver (core profiles refuse
/// wide <c>glLineWidth</c>).
/// </summary>
/// <param name="Start">First end point.</param>
/// <param name="End">Second end point.</param>
/// <param name="Color">Linear RGBA colour.</param>
/// <param name="Width">Thickness in pixels.</param>
/// <param name="Prev">The point before <paramref name="Start"/> on a polyline: the start is mitered to meet that segment instead of getting a square cap.</param>
/// <param name="Next">The point after <paramref name="End"/> on a polyline: the end is mitered to meet that segment instead of getting a square cap.</param>
public readonly record struct OverlayLine(Vector3 Start, Vector3 End, Vector4 Color, float Width = 2f, Vector3? Prev = null, Vector3? Next = null);

/// <summary>A filled world-space triangle drawn on top of the scene (no depth test, blended): arrow heads, handle cubes.</summary>
/// <param name="A">First corner.</param>
/// <param name="B">Second corner.</param>
/// <param name="C">Third corner.</param>
/// <param name="Color">Linear RGBA colour.</param>
public readonly record struct OverlayTriangle(Vector3 A, Vector3 B, Vector3 C, Vector4 Color);
