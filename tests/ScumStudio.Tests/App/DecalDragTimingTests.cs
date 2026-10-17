using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Mathematics;
using ScumStudio.Tests.Viewport;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// The live decal preview of the 3D view (owner: "dragging stutters"): a decal dragged over a car whose colour texture is
/// 2048 px keeps the UI thread under one frame per drag step, because the bake runs on a worker (a small one while the
/// drag lasts, the full size after the release), and the full-size bake arrives once the drag ends.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class DecalDragTimingTests(ITestOutputHelper output)
{
    private const string Body = "/Game/Cars/MI_Body.MI_Body";
    private const string Texture = "/Game/Cars/T_Body.T_Body";

    /// <summary>The preview bake's size while a decal is dragged.</summary>
    private const int DragSize = 1024;

    /// <summary>One frame at 60 Hz.</summary>
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(1000.0 / 60);

    [AvaloniaFact]
    public async Task DraggingADecalOverA2048TextureKeepsTheUiThreadUnderOneFrame()
    {
        using var ctx = AppTestContext.Create();
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Decals");
        using var page = new VehiclesPageViewModel(ctx.Services);
        var paint = VehicleStudioMaterialsTests.Paint("/Game/Cars/MI_Body");
        Assert.Equal(Body, paint.MaterialPath);
        page.UseDecalPreview(Car(), "BPC_MyCar", [paint],
            new Dictionary<string, TextureImage> { [Texture] = Image(2048, 128, 128, 128, srgb: true) },
            new Dictionary<string, TextureImage> { [Body] = Image(2048, 0, 255, 0, srgb: false) });
        page.ArmDecalCommand.Execute(page.DecalDesigns.Single(d => d.Design.Id == "flames-1"));
        Assert.True(page.PlaceDecal(DecalsViewModelTests.Ray(0)));
        Assert.True(HeadlessUi.PumpUntil(() => !page.DecalPreviewBusy, TimeSpan.FromSeconds(60)));

        var steps = new List<TimeSpan>();
        var sizesShown = new HashSet<int>();
        for (var i = 1; i <= 16; i++)
        {
            var clock = Stopwatch.StartNew();
            Assert.True(page.DragDecal(DecalsViewModelTests.Ray(i * 1.5f), commit: false));
            steps.Add(clock.Elapsed);

            // Every other step waits for the bake: a finished bake is shown on the UI thread, and that counts too.
            var wait = Stopwatch.StartNew();
            while (i % 2 == 0 && page.DecalPreviewBusy && wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(4);
                clock.Restart();
                Dispatcher.UIThread.RunJobs();
                steps.Add(clock.Elapsed);
            }

            clock.Restart();
            Dispatcher.UIThread.RunJobs();
            steps.Add(clock.Elapsed);
            sizesShown.Add(Shown(page)?.Width ?? 0);
        }

        Assert.Contains(DragSize, sizesShown); // the small bake shows while the drag lasts

        var release = Stopwatch.StartNew();
        Assert.True(page.DragDecal(DecalsViewModelTests.Ray(25), commit: true));
        steps.Add(release.Elapsed);
        output.WriteLine("UI thread per step (ms): " + string.Join(" ", steps.Select(s => s.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))));
        var worst = steps.Skip(2).Max(); // the first step warms the code up
        if (!Ci.On) Assert.True(worst < Frame, $"the UI thread was held {worst.TotalMilliseconds:0.0} ms in one drag step (one frame is {Frame.TotalMilliseconds:0.0} ms)");

        // After the release the full-size bake arrives, with the decal where the drag ended.
        Assert.True(HeadlessUi.PumpUntil(() => !page.DecalPreviewBusy && Shown(page) is { Width: 2048 }, TimeSpan.FromSeconds(60)));
        var shown = Shown(page)!;
        var under = ((1536 * 2048) + 2) * 4; // v = (x + 50) / 100 = 0.75, u ≈ 0 (the side the ray hits)
        var plain = PaintedGrey(page, shown);
        Assert.NotEqual(plain, (shown.Rgba[under], shown.Rgba[under + 1], shown.Rgba[under + 2]));
        Assert.Equal(25f, page.Decals.Layers.Single().Position.X, 1);
    }

    /// <summary>The colour texture the 3D view shows for the body (the paint with the decals).</summary>
    private static TextureImage? Shown(ModulePageViewModel page) =>
        page.Preview?.Textures.GetValueOrDefault("#paint/" + Body);

    /// <summary>The painted colour far from the decal (the tube's other side).</summary>
    private static (byte, byte, byte) PaintedGrey(ModulePageViewModel page, TextureImage shown)
    {
        _ = page;
        var far = ((1536 * shown.Width) + (shown.Width / 2)) * 4;
        return (shown.Rgba[far], shown.Rgba[far + 1], shown.Rgba[far + 2]);
    }

    /// <summary><see cref="DecalsViewModelTests.Car"/> wearing the colour texture <see cref="Texture"/>.</summary>
    internal static PreviewModel Car() => new("Car",
        [new PreviewPart("Door", DecalMeshes.Cylinder(50, 100, 128, 32), Texture, FTransform.Identity) { Material = Body, Attachment = "/Game/Cars/BPC_MyCar_Door_Left" }],
        new Dictionary<string, TextureImage>());

    internal static TextureImage Image(int size, byte r, byte g, byte b, bool srgb)
    {
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, 255);
        }

        return new TextureImage("T", size, size, rgba, "PF_B8G8R8A8", 0, size, size, srgb, false);
    }
}
