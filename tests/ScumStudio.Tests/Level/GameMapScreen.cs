using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.World;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The game's own spawn screen, read from the paks: the widget <see cref="SpawnScreen"/> (the sector picker every player
/// sees when spawning) draws the island map texture in its Image <c>MapImage</c> turned by <see cref="MapAngle"/> under a
/// 5 x 5 grid of sector buttons (<see cref="Sectors"/>: row 0 at the top, column 0 at the left, texts such as "D4").
/// </summary>
internal sealed record GameMapScreen(float MapAngle, string? MapTexture, IReadOnlyDictionary<(int Row, int Column), string> Sectors)
{
    /// <summary>Package of the game's spawn screen sector picker.</summary>
    public const string SpawnScreen = "/Game/ConZ_Files/UI/SpawnScreen/UI_SpawnSectorSelector";

    /// <summary>Reads the spawn screen's map image and sector buttons from <paramref name="catalog"/>.</summary>
    public static GameMapScreen Read(AssetCatalog catalog)
    {
        var exports = catalog.LoadPackage(SpawnScreen).GetExports().ToList();
        var image = exports.Single(e => e.ExportType == "Image" && e.Name == "MapImage");
        var angle = image.GetOrDefault<FStructFallback>("RenderTransform")?.GetOrDefault<float>("Angle") ?? 0f;
        var texture = image.GetOrDefault<FStructFallback>("Brush")?.GetOrDefault<FPackageIndex>("ResourceObject")?.Name;
        var sectors = new Dictionary<(int Row, int Column), string>();
        foreach (var slot in exports.Where(e => e.ExportType == "UniformGridSlot"))
        {
            if (TextOf(slot.GetOrDefault<FPackageIndex?>("Content")?.Load(), exports, 0) is { } text)
            {
                sectors[(slot.GetOrDefault<int>("Row"), slot.GetOrDefault<int>("Column"))] = new string(text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
            }
        }

        return new GameMapScreen(angle, texture, sectors);
    }

    /// <summary>The game's button (row, column) labelled with <paramref name="cell"/>'s name ("D4" for D_4), or null.</summary>
    public (int Row, int Column)? ButtonOf(MapCell cell)
    {
        var name = $"{cell.Column}{cell.Row}";
        foreach (var (at, text) in Sectors)
        {
            if (text == name)
            {
                return at;
            }
        }

        return null;
    }

    // A TextBlock's text, or the text inside a Button (Border, Overlay ...): what its slots hold.
    private static string? TextOf(UObject? widget, IReadOnlyList<UObject> exports, int depth)
    {
        if (widget is null || depth > 4)
        {
            return null;
        }

        if (widget.TryGetValue(out FText text, "Text"))
        {
            return text.Text;
        }

        foreach (var inner in exports.Where(e => e.ExportType.EndsWith("Slot", StringComparison.Ordinal) && (ReferenceEquals(e.Outer, widget) || e.Outer?.Name == widget.Name)))
        {
            if (TextOf(inner.GetOrDefault<FPackageIndex?>("Content")?.Load(), exports, depth + 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
