using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using UeVector = CUE4Parse.UE4.Objects.Core.Math.FVector;
using UeVector2D = CUE4Parse.UE4.Objects.Core.Math.FVector2D;

namespace ScumStudio.Level.Reading;

/// <summary>
/// Spline mesh support of <see cref="Cue4ParseLevelReader"/>: reads the bend of a <c>SplineMeshComponent</c> into
/// <see cref="SplineMeshParams"/>.
/// </summary>
/// <remarks>
/// Cooked layout (SCUM 1.3.3, landscape tiles): the component stores <c>SplineParams</c> as a tagged struct with only
/// the members that differ from the defaults (on the island's roads: <c>StartPos</c>, <c>StartTangent</c>,
/// <c>StartRoll</c>, <c>StartOffset</c>, <c>EndPos</c>, <c>EndTangent</c>, <c>EndRoll</c>, <c>EndOffset</c>; scales are
/// left at 1), and <c>SplineUpDir</c>, <c>ForwardAxis</c>, <c>bSmoothInterpRollScale</c>, <c>SplineBoundaryMin/Max</c>
/// only when changed. CUE4Parse 1.2.2's <c>FSplineMeshParams</c> has no scale or roll, so the struct is read member by
/// member from its tagged properties, along the template chain like every other component value.
/// </remarks>
public sealed partial class Cue4ParseLevelReader
{
    private const string SplineMeshComponentClass = "SplineMeshComponent";

    /// <summary>The bend of a spline mesh component (or template); engine defaults for everything not stored.</summary>
    private SplineMeshParams ReadSplineMesh(TemplateChain templates)
    {
        var ignored = false;
        var result = new SplineMeshParams();
        if (TryGetProperty(templates, "SplineParams", out FStructFallback stored, ref ignored))
        {
            result = result with
            {
                StartPos = ReadVector(stored, "StartPos", result.StartPos),
                StartTangent = ReadVector(stored, "StartTangent", result.StartTangent),
                StartScale = ReadVector2(stored, "StartScale", result.StartScale),
                StartRoll = stored.TryGetValue(out float startRoll, "StartRoll") ? startRoll : result.StartRoll,
                StartOffset = ReadVector2(stored, "StartOffset", result.StartOffset),
                EndPos = ReadVector(stored, "EndPos", result.EndPos),
                EndTangent = ReadVector(stored, "EndTangent", result.EndTangent),
                EndScale = ReadVector2(stored, "EndScale", result.EndScale),
                EndRoll = stored.TryGetValue(out float endRoll, "EndRoll") ? endRoll : result.EndRoll,
                EndOffset = ReadVector2(stored, "EndOffset", result.EndOffset),
            };
        }
        else if (TryGetProperty(templates, "SplineParams", out FSplineMeshParams typed, ref ignored))
        {
            // CUE4Parse mapped the struct onto its own (incomplete) type: positions, tangents and offsets only.
            result = result with
            {
                StartPos = Convert(typed.StartPos),
                StartTangent = Convert(typed.StartTangent),
                StartOffset = Convert(typed.StartOffset),
                EndPos = Convert(typed.EndPos),
                EndTangent = Convert(typed.EndTangent),
                EndOffset = Convert(typed.EndOffset),
            };
        }

        if (TryGetProperty(templates, "SplineUpDir", out UeVector up, ref ignored))
        {
            result = result with { SplineUpDir = Convert(up) };
        }

        if (TryGetProperty(templates, "bSmoothInterpRollScale", out bool smooth, ref ignored))
        {
            result = result with { SmoothInterpRollScale = smooth };
        }

        if (TryGetProperty(templates, "SplineBoundaryMin", out float boundaryMin, ref ignored))
        {
            result = result with { SplineBoundaryMin = boundaryMin };
        }

        if (TryGetProperty(templates, "SplineBoundaryMax", out float boundaryMax, ref ignored))
        {
            result = result with { SplineBoundaryMax = boundaryMax };
        }

        if (TryGetProperty(templates, "ForwardAxis", out FName axisName, ref ignored) && !axisName.IsNone)
        {
            var text = axisName.Text;
            var cut = text.LastIndexOf(':');
            result = result with { ForwardAxis = Enum.TryParse<SplineMeshAxis>(cut >= 0 ? text[(cut + 1)..] : text, out var axis) ? axis : SplineMeshAxis.X };
        }
        else if (TryGetProperty(templates, "ForwardAxis", out byte axisByte, ref ignored) && axisByte <= (byte)SplineMeshAxis.Z)
        {
            result = result with { ForwardAxis = (SplineMeshAxis)axisByte };
        }

        return result;
    }

    private static FVector ReadVector(FStructFallback s, string name, FVector fallback) => s.TryGetValue(out UeVector v, name) ? Convert(v) : fallback;

    private static Vector2 ReadVector2(FStructFallback s, string name, Vector2 fallback) => s.TryGetValue(out UeVector2D v, name) ? Convert(v) : fallback;

    private static FVector Convert(UeVector v) => new(v.X, v.Y, v.Z);

    private static Vector2 Convert(UeVector2D v) => new(v.X, v.Y);
}
