using System.Globalization;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// A near clip distance for the Map's camera (Settings › Near clip, Ctrl+Shift+N on the Map; F15, seviscache: "the camera
/// clips the mesh, making it impossible to see exactly where you want to place the item").
/// </summary>
/// <param name="Centimetres">How close to the camera the Map still draws.</param>
public sealed record NearClipOption(float Centimetres)
{
    /// <summary>The choices, nearest first (10 cm is the default).</summary>
    public static IReadOnlyList<NearClipOption> All { get; } = [new(1f), new(2f), new(5f), new(10f), new(20f), new(50f)];

    /// <summary>The choice closest to <paramref name="centimetres"/> (a hand-edited settings file may hold any number).</summary>
    public static NearClipOption Nearest(float centimetres) =>
        All.MinBy(o => float.IsFinite(centimetres) ? MathF.Abs(MathF.Log(o.Centimetres) - MathF.Log(MathF.Max(centimetres, 0.01f))) : MathF.Abs(o.Centimetres - 10f))!;

    /// <summary>The next choice after <paramref name="centimetres"/> (back to the nearest after the last one).</summary>
    public static NearClipOption Next(float centimetres) => All[(All.ToList().IndexOf(Nearest(centimetres)) + 1) % All.Count];

    /// <summary>"10 cm".</summary>
    public override string ToString() => Centimetres.ToString("0.#", CultureInfo.CurrentCulture) + " cm";
}
