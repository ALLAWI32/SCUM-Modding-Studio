using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Localization;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.App.ViewModels;

/// <summary>A category chip of the decal library (empty key: all).</summary>
/// <param name="Key"><c>Tribal</c>, <c>Flames</c> …, <c>GameTattoos</c>, <c>Stickers</c>, or empty.</param>
public sealed record DecalCategoryItem(string Key)
{
    /// <summary>Text on the chip.</summary>
    public string Label => LabelOf(Key.Length == 0 ? "All" : Key);

    /// <summary>The text of a library category (<c>Tribal</c> …, <c>GameTattoos</c>, <c>Stickers</c>, <c>All</c>).</summary>
    public static string LabelOf(string category) => Loc.T(string.Concat("Studio.Decals.Category.", category));
}

/// <summary>
/// A design in the library grid: its name and a thumbnail, drawn from its paths for the studio's own designs, or the
/// game's own picture of one of its tattoos (read from the user's game files and kept in the thumbnail cache).
/// </summary>
public sealed class DecalDesignItem : ObservableObject
{
    private const int ThumbnailSize = 96;
    private readonly Func<Task<string?>>? _picture;
    private Bitmap? _thumbnail;
    private bool _asked;

    /// <param name="design">The design.</param>
    /// <param name="number">Its number within its category (1 first).</param>
    public DecalDesignItem(DecalDesign design, int number)
    {
        Design = design;
        Name = Loc.F("Studio.Decals.Design", DecalCategoryItem.LabelOf(design.Category), number);
    }

    /// <param name="tattoo">One of the game's tattoos.</param>
    /// <param name="number">Its number in the grid (1 first).</param>
    /// <param name="picture">Makes (or finds in the cache) the PNG of its thumbnail; null when there is none.</param>
    public DecalDesignItem(GameTattoo tattoo, int number, Func<Task<string?>> picture)
    {
        ArgumentNullException.ThrowIfNull(tattoo);
        Tattoo = tattoo;
        Design = new DecalDesign(tattoo.Id, GameTattoos.Category, 1, 1, [new DecalRegion(string.Empty, GameTattoos.Ink)]);
        Name = Loc.F("Studio.Decals.Design", DecalCategoryItem.LabelOf(GameTattoos.Category), number);
        _picture = picture;
    }

    /// <summary>The design (for a game tattoo: its id and colour, its true shape once the atlas is read).</summary>
    public DecalDesign Design { get; }

    /// <summary>The game's tattoo this item stands for, or null for a design of the studio's library.</summary>
    public GameTattoo? Tattoo { get; }

    /// <summary>"Tribal 3", "Game tattoo 12".</summary>
    public string Name { get; }

    /// <summary>The design in its own colours, drawn when first shown (a game tattoo's picture comes a moment later).</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (_picture is null)
            {
                return _thumbnail ??= DecalBitmaps.Of(new DecalLayer().Render(Design.Rasterize(ThumbnailSize), Design));
            }

            if (!_asked)
            {
                _asked = true;
                _ = LoadAsync();
            }

            return _thumbnail;
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            if (await _picture!().ConfigureAwait(true) is { } png)
            {
                _thumbnail = await Task.Run(() => new Bitmap(png)).ConfigureAwait(true);
                OnPropertyChanged(nameof(Thumbnail));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // no picture: the name stays
        }
    }
}

/// <summary>One colour of a decal (a region's, or one of the palette's) in the colour pickers.</summary>
public sealed partial class DecalColourItem : ObservableObject
{
    private readonly Action<DecalColourItem> _changed;

    /// <summary>The colour.</summary>
    [ObservableProperty]
    private Color _colour;

    /// <param name="label">"Colour 1".</param>
    /// <param name="colour">The colour.</param>
    /// <param name="changed">Called after the user picks another colour.</param>
    public DecalColourItem(string label, Color colour, Action<DecalColourItem> changed)
    {
        Label = label;
        _colour = colour;
        _changed = changed;
    }

    /// <summary>Text beside the picker.</summary>
    public string Label { get; }

    /// <summary>The colour as <c>#RRGGBB</c>.</summary>
    public string Hex => $"#{Colour.R:X2}{Colour.G:X2}{Colour.B:X2}";

    partial void OnColourChanged(Color value) => _changed(this);

    /// <summary><c>#RRGGBB</c> as a colour (white when it is not one).</summary>
    public static Color Parse(string? hex) => DecalLayer.Parse(hex) is { } c
        ? Color.FromRgb((byte)MathF.Round(c.X * 255), (byte)MathF.Round(c.Y * 255), (byte)MathF.Round(c.Z * 255))
        : Colors.White;
}

/// <summary>A decal of the selected clone in the layer list, with its settings bound to the panel's controls.</summary>
public sealed partial class DecalLayerViewModel : ObservableObject
{
    private readonly Action<DecalLayer> _edited;
    private readonly Action<DecalLayerViewModel, Vector3>? _moved;
    private bool _quiet;

    /// <summary>Width on the car, cm (the height follows, keeping the design's shape).</summary>
    [ObservableProperty]
    private double _size;

    /// <summary>Height on the car, cm (changing it stretches the design).</summary>
    [ObservableProperty]
    private double _height;

    /// <summary>Centre on the car, vehicle space X (forward), cm.</summary>
    [ObservableProperty]
    private double _x;

    /// <summary>Centre on the car, vehicle space Y (right), cm.</summary>
    [ObservableProperty]
    private double _y;

    /// <summary>Centre on the car, vehicle space Z (up), cm.</summary>
    [ObservableProperty]
    private double _z;

    /// <summary>Turn on the surface, degrees.</summary>
    [ObservableProperty]
    private double _rotation;

    /// <summary>Opacity, percent.</summary>
    [ObservableProperty]
    private double _opacity;

    /// <summary>Mirrored left to right.</summary>
    [ObservableProperty]
    private bool _flip;

    /// <summary>A copy on the other side of the car.</summary>
    [ObservableProperty]
    private bool _mirror;

    /// <summary>Index into <see cref="Modes"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPalette), nameof(ShowsRegions), nameof(ShownRegions))]
    private int _modeIndex;

    /// <param name="layer">The layer.</param>
    /// <param name="name">Its name in the list.</param>
    /// <param name="regions">How many colour regions its design has (1 for a picture).</param>
    /// <param name="defaults">The design's own colours.</param>
    /// <param name="edited">Called with the changed layer after every edit.</param>
    /// <param name="colourable">Its colours can be chosen (a design, a game tattoo, an SVG); false for a picture.</param>
    /// <param name="moved">Called with a centre typed in the position fields (vehicle space, cm).</param>
    public DecalLayerViewModel(DecalLayer layer, string name, int regions, IReadOnlyList<uint> defaults, Action<DecalLayer> edited,
        bool colourable = true, Action<DecalLayerViewModel, Vector3>? moved = null)
    {
        Layer = layer;
        Name = name;
        _edited = edited;
        _moved = moved;
        IsSticker = !colourable;
        _size = layer.WidthCm;
        _height = Math.Round(layer.HeightCm, 1);
        _rotation = layer.RotationDeg;
        _opacity = Math.Round(layer.Opacity * 100);
        _flip = layer.FlipX;
        _mirror = layer.Mirror;
        _modeIndex = (int)layer.ColourMode;
        var shown = layer.RegionColours(regions, defaults);
        Regions = [.. Enumerable.Range(0, regions).Select(k => new DecalColourItem(Loc.F("Studio.Decals.Region", k + 1), ToColour(shown[k]), _ => Commit(true)))];
        Palette = [.. (layer.Palette.Count > 0 ? layer.Palette : DefaultPalette).Select((h, k) => new DecalColourItem(Loc.F("Studio.Decals.Region", k + 1), DecalColourItem.Parse(h), _ => Commit(true)))];
    }

    /// <summary>The colours a new palette starts with.</summary>
    public static IReadOnlyList<string> DefaultPalette { get; } = ["#D21E1E", "#F0B414", "#1E64C8", "#14A050", "#F0F0F0", "#141414"];

    /// <summary>The colour modes in the choice.</summary>
    public static IReadOnlyList<string> Modes => [Loc.T("Studio.Decals.Mode.Single"), Loc.T("Studio.Decals.Mode.Regions"), Loc.T("Studio.Decals.Mode.Palette")];

    /// <summary>The layer as last edited.</summary>
    public DecalLayer Layer { get; private set; }

    /// <summary>Text in the list.</summary>
    public string Name { get; }

    /// <summary>A picture of the user's (its colours are its own).</summary>
    public bool IsSticker { get; }

    /// <summary>Not drawn (the list's eye is closed).</summary>
    public bool IsHidden => Layer.Hidden;

    /// <summary>The 3D view cannot pick or move it (the list's lock).</summary>
    public bool IsLocked => Layer.Locked;

    /// <summary>Region colour pickers (one in single-colour mode).</summary>
    public IReadOnlyList<DecalColourItem> Regions { get; }

    /// <summary>The pickers shown: the first alone sets the one colour.</summary>
    public IReadOnlyList<DecalColourItem> ShownRegions => ModeIndex == (int)DecalColourMode.Single ? [.. Regions.Take(1)] : Regions;

    /// <summary>The palette's colours.</summary>
    public IReadOnlyList<DecalColourItem> Palette { get; }

    /// <summary>The palette editor is shown.</summary>
    public bool IsPalette => !IsSticker && ModeIndex == (int)DecalColourMode.Palette;

    /// <summary>The region pickers are shown.</summary>
    public bool ShowsRegions => !IsSticker && ModeIndex != (int)DecalColourMode.Palette;

    /// <summary>"Variation 7".</summary>
    public string SeedText => Loc.F("Studio.Decals.Seed", Layer.Seed);

    /// <summary>Takes the layer's values again (after an undo, a move, a key) without counting as an edit.</summary>
    public void Show(DecalLayer layer)
    {
        _quiet = true;
        try
        {
            Layer = layer;
            (Size, Height, Rotation, Opacity, Flip, Mirror, ModeIndex) =
                (layer.WidthCm, Math.Round(layer.HeightCm, 1), layer.RotationDeg, Math.Round(layer.Opacity * 100), layer.FlipX, layer.Mirror, (int)layer.ColourMode);
        }
        finally
        {
            _quiet = false;
        }

        OnPropertyChanged(nameof(SeedText));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsLocked));
    }

    /// <summary>Shows the centre on the car (vehicle space, cm) in the position fields without counting as an edit.</summary>
    public void ShowPosition(Vector3 centre)
    {
        _quiet = true;
        try
        {
            (X, Y, Z) = (Math.Round(centre.X, 1), Math.Round(centre.Y, 1), Math.Round(centre.Z, 1));
        }
        finally
        {
            _quiet = false;
        }
    }

    /// <summary>A new variation of the palette pick.</summary>
    public void Shuffle()
    {
        Layer = Layer with { Seed = Layer.Seed + 1 };
        OnPropertyChanged(nameof(SeedText));
        Commit(true);
    }

    partial void OnSizeChanged(double value) => Commit(true);

    partial void OnHeightChanged(double value) => Commit(false);

    partial void OnRotationChanged(double value) => Commit(true);

    partial void OnOpacityChanged(double value) => Commit(true);

    partial void OnFlipChanged(bool value) => Commit(true);

    partial void OnMirrorChanged(bool value) => Commit(true);

    partial void OnModeIndexChanged(int value) => Commit(true);

    partial void OnXChanged(double value) => Move();

    partial void OnYChanged(double value) => Move();

    partial void OnZChanged(double value) => Move();

    private void Move()
    {
        if (!_quiet)
        {
            _moved?.Invoke(this, new Vector3((float)X, (float)Y, (float)Z));
        }
    }

    /// <summary>The settings as the layer; <paramref name="keepShape"/>: the height follows the width (else the typed height stretches it).</summary>
    private void Commit(bool keepShape)
    {
        if (_quiet)
        {
            return;
        }

        var aspect = Layer.WidthCm > 0 ? Layer.HeightCm / Layer.WidthCm : 1f;
        var mode = (DecalColourMode)Math.Clamp(ModeIndex, 0, 2);
        var width = (float)Math.Clamp(double.IsFinite(Size) ? Size : Layer.WidthCm, 1, 500);
        var height = keepShape ? width * aspect : (float)Math.Clamp(double.IsFinite(Height) ? Height : Layer.HeightCm, 1, 500);
        Layer = Layer with
        {
            WidthCm = width, HeightCm = height, RotationDeg = (float)Rotation,
            Opacity = (float)Math.Clamp(Opacity / 100, 0, 1), FlipX = Flip, Mirror = Mirror, ColourMode = mode,
            Colours = mode == DecalColourMode.Single ? [Regions[0].Hex] : [.. Regions.Select(r => r.Hex)],
            Palette = [.. Palette.Select(p => p.Hex)],
        };
        if (keepShape && Math.Abs(Height - Math.Round(height, 1)) > 0.05)
        {
            _quiet = true;
            Height = Math.Round(height, 1);
            _quiet = false;
        }

        _edited(Layer);
    }

    private static Color ToColour(Vector3 srgb) => Color.FromRgb((byte)MathF.Round(srgb.X * 255), (byte)MathF.Round(srgb.Y * 255), (byte)MathF.Round(srgb.Z * 255));
}

/// <summary>Decals as Avalonia bitmaps (thumbnails).</summary>
internal static class DecalBitmaps
{
    /// <summary>A decal (premultiplied linear) as a premultiplied BGRA bitmap.</summary>
    public static Bitmap Of(DecalImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var bytes = new byte[image.Width * image.Height * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var a = image.Rgba[i + 3];
            static byte S(float v, float a) => (byte)MathF.Round(Math.Clamp(a <= 0 ? 0 : MathF.Pow(Math.Clamp(v / a, 0, 1), 1 / 2.2f) * a, 0, 1) * 255);
            (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]) = (S(image.Rgba[i + 2], a), S(image.Rgba[i + 1], a), S(image.Rgba[i], a), (byte)MathF.Round(Math.Clamp(a, 0, 1) * 255));
        }

        using (var frame = bitmap.Lock())
        {
            for (var y = 0; y < image.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bytes, y * image.Width * 4, frame.Address + (y * frame.RowBytes), image.Width * 4);
            }
        }

        return bitmap;
    }
}
