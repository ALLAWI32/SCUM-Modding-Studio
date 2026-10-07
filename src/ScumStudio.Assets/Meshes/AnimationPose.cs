using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Animations;
using ScumStudio.Core.Mathematics;
using FQuat = ScumStudio.Core.Mathematics.FQuat;
using FTransform = ScumStudio.Core.Mathematics.FTransform;
using FVector = ScumStudio.Core.Mathematics.FVector;

namespace ScumStudio.Assets.Meshes;

/// <summary>Reads one frame of a cooked animation as bone-local transforms (CUE4Parse decompresses the tracks).</summary>
public static class AnimationPose
{
    /// <summary>
    /// The bone-local transforms at frame <paramref name="frame"/> of <paramref name="sequence"/>, by bone name of the
    /// sequence's skeleton; bones without a track are left out (they keep their bind pose, see <see cref="Skinning.Pose"/>).
    /// </summary>
    /// <exception cref="InvalidDataException">The sequence has no skeleton or no tracks.</exception>
    public static Dictionary<string, FTransform> Locals(UAnimSequence sequence, int frame = 0)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var skeleton = sequence.Skeleton.Load<USkeleton>() ?? throw new InvalidDataException($"{sequence.Name} has no skeleton.");
        var set = skeleton.ConvertAnims(sequence);
        var converted = set.Sequences.FirstOrDefault() ?? throw new InvalidDataException($"{sequence.Name} has no tracks.");
        var bones = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
        var refPose = skeleton.ReferenceSkeleton.FinalRefBonePose;
        var locals = new Dictionary<string, FTransform>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < converted.Tracks.Count && i < bones.Length; i++)
        {
            var track = converted.Tracks[i];
            if (track is null || !track.HasKeys())
            {
                continue;
            }

            // A track keeps only the channels that move (a rotation-only bone): the others stay the skeleton's bind pose.
            var bind = i < refPose.Length ? refPose[i] : CUE4Parse.UE4.Objects.Core.Math.FTransform.Identity;
            var rotation = bind.Rotation;
            var position = bind.Translation;
            var scale = bind.Scale3D;
            track.GetBoneTransform(frame, converted.NumFrames, ref rotation, ref position, ref scale);
            locals[bones[i].Name.Text] = new FTransform(
                new FQuat(rotation.X, rotation.Y, rotation.Z, rotation.W).GetNormalized(),
                new FVector(position.X, position.Y, position.Z),
                scale.IsZero() ? FVector.One : new FVector(scale.X, scale.Y, scale.Z));
        }

        return locals;
    }
}
