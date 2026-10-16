using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Go to coordinates (F20, defolt_0999: "Is there a way to find it using its coordinates?"): the camera goes to typed
/// coordinates and the Entities list shows what stands nearest to them.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>How far the Entities list looks around typed coordinates (50 m).</summary>
    public const float CoordinateSearchRadius = 5000f;

    /// <summary>The Go to box's text: "X Y (Z)" in metres or the game's centimetres.</summary>
    [ObservableProperty]
    private string _goToText = string.Empty;

    /// <summary>What the last Go to did ("Read as metres …") or why it could not.</summary>
    [ObservableProperty]
    private string _goToResult = string.Empty;

    /// <summary>
    /// Sends the camera to <see cref="GoToText"/> (25 m away, looking down at the spot; on the ground when no Z is given) and
    /// lists the objects nearest to it in the Entities list.
    /// </summary>
    [RelayCommand]
    private void GoToCoordinates()
    {
        if (!MapCoordinates.TryParse(GoToText, out var at))
        {
            GoToResult = Loc.T("Map.GoTo.Bad");
            return;
        }

        var spot = new FVector(at.X, at.Y, at.Z ?? GroundAt(at.X, at.Y) ?? 0f);
        FrameSectorRequested?.Invoke(new CameraShot(spot + (new FVector(-0.54f, -0.54f, 0.64f) * 2500f), spot)); // 40 degrees down
        GoToResult = Loc.F(at.InCentimetres ? "Map.GoTo.ReadCm" : "Map.GoTo.ReadM", at.MetresText, at.CentimetresText);
        EntityFilter = GoToText.Trim();
    }

    /// <summary>
    /// The actors within <see cref="CoordinateSearchRadius"/> of the coordinates <paramref name="text"/> names (their root or
    /// any of their instances; flat distance when no Z is given), nearest first; null when the text is no coordinates.
    /// </summary>
    internal static List<ActorItemViewModel>? NearCoordinates(IEnumerable<ActorItemViewModel> actors, string text)
    {
        if (!MapCoordinates.TryParse(text, out var at))
        {
            return null;
        }

        return actors
            .Select(a => (Actor: a, Distance: a.Actor.InstanceTransforms.Select(i => i.WorldTransform.Translation)
                .Prepend(a.Actor.WorldTransform.Translation).Min(p => Distance(p))))
            .Where(d => d.Distance <= CoordinateSearchRadius)
            .OrderBy(d => d.Distance)
            .Select(d => d.Actor)
            .ToList();

        float Distance(FVector p)
        {
            var (dx, dy) = (p.X - at.X, p.Y - at.Y);
            var dz = at.Z is { } z ? p.Z - z : 0f;
            return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }
    }
}
