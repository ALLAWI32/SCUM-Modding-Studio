using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Controls;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// Pictures for a person to look at (Discord, 2026-10-17: "the map is inverted"): the game's spawn screen next to the
/// studio's Spawns map, both from the user's paks. Left: the game's map texture turned as the spawn screen turns it, the
/// spawn screen's own sector labels and each landmark's name in the game's cell its name gives. Right: the studio's
/// <see cref="SpawnMap"/> with the picture the app makes, its grid and labels, and the landmarks at their world positions.
/// Real game files (<c>SCUM_PAKS</c>) and <c>SCUMSTUDIO_SCREENSHOTS</c> only; it asserts what the pictures need.
/// </summary>
public sealed class SpawnMapGameViewRealTests
{
    private static readonly string[] NamedLandmarks =
    [
        "A_0_Outpost", "B_4_Outpost", "B_4_Outpost_Airfield", "D_4_Samobor_54_Ruins", "C_0_NuclearPlant_01_ControlBuilding",
        "D_0_RadarStation_01_OfficeBuilding_Ext", "B_0_Hospital_Building", "D_2_Castle_Ruins", "A_3_Farm_01",
    ];

    [AvaloniaFact]
    public void DrawsTheSpawnsMapNextToTheGamesSpawnScreen()
    {
        if (HeadlessUi.ScreenshotDirectory is not { } folder
            || Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var temp = Directory.CreateTempSubdirectory("scumstudio-mapview-").FullName;
        try
        {
            // The studio's picture as the Spawns page makes it, and the texture as stored (row 0 at the top).
            var result = new IslandMapPicture(Path.Combine(temp, "cache"), NullLogger.Instance, terrainDirectory: null).GetAsync(catalog).GetAwaiter().GetResult();
            Assert.Equal(IslandMapSource.Game, result.Source);
            var studioPicture = new Bitmap(result.PngPath!);
            var rawPath = Path.Combine(temp, "raw.png");
            PngWriter.Save(IslandMapPicture.FromCatalog(catalog)!.Decode()!, rawPath);
            var gamePicture = new Bitmap(rawPath);
            var screen = GameMapScreen.Read(catalog);
            var landmarks = Landmarks(catalog);

            var game = new GameScreenView(gamePicture, screen, landmarks) { Width = 1000, Height = 1000 };
            var map = new SpawnMap
            {
                Tool = SpawnMapTool.Pan,
                Picture = studioPicture,
                Markers = landmarks.Select((l, i) => new SpawnMapMarker(i, l.X, l.Y, SpawnMapLayer.Vehicles)).ToList(),
                HighlightedIds = landmarks.Select((_, i) => i).ToHashSet(),
            };
            var names = new Canvas { IsHitTestVisible = false };
            var studio = new Panel { Width = 1000, Height = 1000, Children = { map, names } };
            var root = new Grid { Background = Brushes.Black, ColumnDefinitions = new ColumnDefinitions("1000,1000"), RowDefinitions = new RowDefinitions("40,1000") };
            root.Children.Add(Title($"GAME spawn screen ({Leaf(GameMapScreen.SpawnScreen)}): {screen.MapTexture} turned {screen.MapAngle:0} deg, its own sector labels, each landmark's name in the cell its name gives", 0));
            root.Children.Add(Title("STUDIO Spawns map (SpawnMap): the picture the app makes, its grid and labels, the landmarks at their world positions", 1));
            Grid.SetRow(game, 1);
            Grid.SetRow(studio, 1);
            Grid.SetColumn(studio, 1);
            root.Children.Add(game);
            root.Children.Add(studio);
            var window = new Window { Width = 2000, Height = 1040, Content = root };
            window.Show();
            HeadlessUi.Pump();
            foreach (var l in landmarks)
            {
                var at = map.WorldToScreen(l.X, l.Y);
                var label = new Border { Background = new SolidColorBrush(Colors.Black, 0.7), Padding = new Thickness(3, 1), Child = new TextBlock { Text = l.Name, Foreground = Brushes.Yellow, FontSize = 10 } };
                Canvas.SetLeft(label, at.X + 6);
                Canvas.SetTop(label, at.Y - 7);
                names.Children.Add(label);
            }

            Assert.NotNull(HeadlessUi.SaveScreenshot(window, "spawns-map-vs-game"));
            window.Close();

            // Close in (2 px a metre: the 10 m grid shows) on a vehicle place facing +X (yaw 0), a zombie place 150 m along
            // +X and an NPC place 150 m along +Y: the picked place's arrow must point at the zombie place.
            var (cx, cy) = (60_000f, 50_000f);
            var picked = new SpawnMap
            {
                Tool = SpawnMapTool.Select,
                Picture = studioPicture,
                Markers = [new SpawnMapMarker(1, cx, cy, SpawnMapLayer.Vehicles, Yaw: 0f), new SpawnMapMarker(2, cx + 15_000f, cy, SpawnMapLayer.Zombies), new SpawnMapMarker(3, cx, cy + 15_000f, SpawnMapLayer.Npcs)],
            };
            var close = new Window { Width = 800, Height = 600, Content = picked };
            close.Show();
            HeadlessUi.Pump();
            picked.CenterOn(cx + 5_000f, cy + 5_000f, 2);
            picked.SelectedId = 1;
            Assert.NotNull(HeadlessUi.SaveScreenshot(close, "spawns-map-picked-faces-plus-x"));
            close.Close();
            File.WriteAllLines(Path.Combine(folder, "spawns-map-landmarks.txt"), landmarks.Select(l =>
                string.Create(CultureInfo.InvariantCulture, $"{l.Name,-45} {l.Cell}  X {l.X,10:0} Y {l.Y,10:0}  game button {screen.ButtonOf(l.Cell)}  studio picture {IslandMap.ToMap(l.X, l.Y)}")));
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // Per cell the first place sublevel with a real tile box, plus the named ones; at their tile centres.
    private static List<Landmark> Landmarks(AssetCatalog catalog)
    {
        var index = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog);
        var landmarks = new List<Landmark>();
        foreach (var cell in MapCell.All)
        {
            var place = index.Packages
                .Where(p => p.IsMap && p.Kind == WorldPackageKind.Poi && p.Cell == cell && p.Tile is { BoundsValid: true } t && t.Size.X > 1000f && !(t.Center.X == 0f && t.Center.Y == 0f))
                .OrderBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault();
            if (place is not null)
            {
                landmarks.Add(new Landmark(place.Name, cell, place.Tile!.Center.X, place.Tile.Center.Y));
            }
        }

        foreach (var name in NamedLandmarks)
        {
            if (index.Find(name) is { Tile: { BoundsValid: true } tile, Cell: { } cell } && landmarks.All(l => l.Name != name))
            {
                landmarks.Add(new Landmark(name, cell, tile.Center.X, tile.Center.Y));
            }
        }

        return landmarks;
    }

    private static TextBlock Title(string text, int column)
    {
        var block = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(8, 4), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(block, column);
        return block;
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private sealed record Landmark(string Name, MapCell Cell, float X, float Y);

    // The game's spawn screen: the texture as stored, turned by the screen's angle about its centre, the 5 x 5 lines, the
    // screen's sector labels at their buttons and each landmark's name under the button its cell is named by.
    private sealed class GameScreenView(Bitmap picture, GameMapScreen screen, List<Landmark> landmarks) : Control
    {
        public override void Render(DrawingContext context)
        {
            var side = Math.Min(Bounds.Width, Bounds.Height);
            var turn = Matrix.CreateTranslation(-side / 2, -side / 2) * Matrix.CreateRotation(screen.MapAngle * Math.PI / 180) * Matrix.CreateTranslation(side / 2, side / 2);
            using (context.PushTransform(turn))
            {
                context.DrawImage(picture, new Rect(picture.Size), new Rect(0, 0, side, side));
            }

            var pen = new Pen(new SolidColorBrush(Colors.White, 0.5), 1);
            for (var k = 0; k <= 5; k++)
            {
                context.DrawLine(pen, new Point(k * side / 5, 0), new Point(k * side / 5, side));
                context.DrawLine(pen, new Point(0, k * side / 5), new Point(side, k * side / 5));
            }

            var face = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
            foreach (var ((row, column), text) in screen.Sectors)
            {
                var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 22, Brushes.Yellow);
                var at = new Point((column * side / 5) + 8, (row * side / 5) + 6);
                context.DrawRectangle(new SolidColorBrush(Colors.Black, 0.7), null, new Rect(at.X - 4, at.Y - 2, label.Width + 8, label.Height + 4));
                context.DrawText(label, at);
            }

            var perButton = new Dictionary<(int, int), int>();
            foreach (var l in landmarks)
            {
                if (screen.ButtonOf(l.Cell) is not { } button)
                {
                    continue;
                }

                var n = perButton.GetValueOrDefault(button);
                perButton[button] = n + 1;
                var label = new FormattedText(l.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10, Brushes.Cyan);
                var at = new Point((button.Column * side / 5) + 8, (button.Row * side / 5) + 40 + (n * 14));
                context.DrawRectangle(new SolidColorBrush(Colors.Black, 0.6), null, new Rect(at.X - 2, at.Y - 1, label.Width + 4, label.Height + 2));
                context.DrawText(label, at);
            }
        }
    }
}
