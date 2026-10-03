using System.Numerics;

namespace ScumStudio.Rendering.SceneGraph;

/// <summary>
/// A world-space line segment drawn on top of the scene (no depth test), in the renderer's GL space: used for the
/// transform gizmo and other editor helpers. Colours are linear RGBA.
/// </summary>
/// <param name="Start">First end point.</param>
/// <param name="End">Second end point.</param>
/// <param name="Color">Linear RGBA colour.</param>
public readonly record struct OverlayLine(Vector3 Start, Vector3 End, Vector4 Color);
