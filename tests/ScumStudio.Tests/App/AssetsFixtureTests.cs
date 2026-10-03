using Avalonia.Controls;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.App;

/// <summary>The Assets page over the loose cooked packages of the fixture archive (skipped without <c>SCUM_FIXTURES</c>).</summary>
public sealed class AssetsFixtureTests
{
    [FixturesFact]
    public async Task AssetsPageListsTheLoosePackagesAndExportsATexture()
    {
        using var ctx = AppTestContext.Create();
        using var page = new AssetsPageViewModel(ctx.Services);
        Assert.True(page.ShowEmptyState);

        Assert.True(await page.OpenLooseFolderAsync(FixturePaths.OrigRoot));

        Assert.True(ctx.Services.Workspace.IsLoose);
        Assert.False(page.ShowEmptyState);
        var index = page.Index!;
        Assert.True(index.Count > 100, $"only {index.Count} packages");
        Assert.True(index.HasClasses);
        var game = Assert.Single(page.Roots, r => r.Name == "Game");
        Assert.True(game.IsExpanded);
        Assert.Same(game, page.SelectedFolder);
        Assert.NotEmpty(page.Items);
        Assert.NotEmpty(page.ClassChips);
        Assert.Contains("packages", page.Subtitle, StringComparison.Ordinal);

        // Search-as-you-type: every result matches, class filter narrows.
        await page.SearchAsync("WolfsWagen");
        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, i => Assert.Contains("WolfsWagen", i.Entry.PackagePath, StringComparison.OrdinalIgnoreCase));
        await page.SearchAsync("class:Texture2D");
        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, i => Assert.Equal("Texture2D", i.ClassName));
        Assert.All(page.Items, i => Assert.Equal("Icon.Texture", i.IconKey));

        // Details: class, exports and size.
        var texture = page.Items.OrderBy(i => i.Entry.PackagePath, StringComparer.Ordinal).First();
        page.SelectedItem = texture;
        var details = await WaitForDetailsAsync(page);
        Assert.Null(details.Error);
        Assert.Equal("Texture2D", details.ClassName);
        Assert.NotEmpty(details.Exports);
        Assert.DoesNotContain("...", details.SizeText, StringComparison.Ordinal);
        Assert.True(details.CanExportPng);
        Assert.False(details.CanExportGltf);
        Assert.Contains(details.Rows, r => r.Name == "Exports");
        await page.DetailsCompletion;
        Assert.NotNull(details.Image);
        Assert.True(details.Image!.Width <= AssetsPageViewModel.PreviewTextureSize);
        Assert.Contains(" x ", details.ImageText, StringComparison.Ordinal);

        // Export PNG through ScumStudio.Assets.
        var png = ctx.Combine("export", texture.Name + ".png");
        Assert.True(await page.ExportPngToAsync(png));
        var bytes = await File.ReadAllBytesAsync(png);
        Assert.True(bytes.Length > 64);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.Title == "Texture exported");

        // A folder click lists that folder's packages.
        page.ApplySearch(string.Empty);
        await page.SearchAsync(string.Empty);
        var firstChild = game.Children[0];
        page.SelectedFolder = firstChild;
        Assert.All(page.Items, i => Assert.StartsWith(firstChild.Path, i.Entry.PackagePath, StringComparison.OrdinalIgnoreCase));
    }

    [FixturesFact]
    public async Task AssetsPageExportsAMeshAsGltf()
    {
        using var ctx = AppTestContext.Create();
        using var page = new AssetsPageViewModel(ctx.Services);
        Assert.True(await page.OpenLooseFolderAsync(FixturePaths.OrigRoot));

        await page.SearchAsync("class:StaticMesh");
        var mesh = page.Items.OrderBy(i => i.Entry.PackagePath, StringComparer.Ordinal).FirstOrDefault();
        Assert.NotNull(mesh);
        page.SelectedItem = mesh;
        var details = await WaitForDetailsAsync(page);
        Assert.True(details.CanExportGltf);
        await page.DetailsCompletion;
        Assert.NotNull(details.Preview);
        Assert.NotEmpty(details.Preview!.Parts);
        Assert.False(details.IsPreviewLoading);

        var gltf = ctx.Combine("export", mesh!.Name + ".gltf");
        Assert.True(await page.ExportGltfToAsync(gltf));
        Assert.True(File.Exists(gltf));
        Assert.Contains("\"asset\"", await File.ReadAllTextAsync(gltf), StringComparison.Ordinal);

        // A failing export is a toast, not an exception.
        await page.SearchAsync("class:Texture2D");
        page.SelectedItem = page.Items[0];
        await WaitForDetailsAsync(page);
        Assert.False(await page.ExportGltfToAsync(ctx.Combine("export", "bad.gltf")));
        Assert.Contains(ctx.Services.Notifications.Toasts, t => t.IsError);
    }

    [AvaloniaFixturesFact]
    public void AssetsPageRendersTheLooseFixture()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services);
        try
        {
            var page = (AssetsPageViewModel)vm.NavigateTo("assets")!;
            var open = page.OpenLooseFolderAsync(FixturePaths.OrigRoot);
            Assert.True(HeadlessUi.PumpUntil(() => open.IsCompleted, TimeSpan.FromSeconds(120)));
            Assert.True(open.Result, string.Join(Environment.NewLine, ctx.Services.Log.Entries.Select(e => e.Entry.ToString())));
            page.SearchText = "WolfsWagen class:Texture2D";
            Assert.True(HeadlessUi.PumpUntil(() => page.Items.Count > 0 && page.ResultText.Contains("match", StringComparison.Ordinal)));
            page.SelectedItem = page.Items[0];
            Assert.True(HeadlessUi.PumpUntil(() => page.Details is { IsLoading: false }));

            // Tiles by default: rows are virtualized and only the tiles on screen fetch their pictures.
            Assert.True(page.IsGridView);
            var grid = HeadlessUi.FindNamed<ItemsControl>(window, "PackageGrid")!;
            Assert.True(grid.IsEffectivelyVisible);
            Assert.True(grid.ItemCount > 0);
            Assert.True(page.Items[0].IsSelected);
            Assert.True(HeadlessUi.PumpUntil(() => page.Items.Any(i => i.Thumbnail is not null), TimeSpan.FromSeconds(60)), "no tile got a picture");
            HeadlessUi.SaveScreenshot(window, "page-assets-tiles");

            page.IsGridView = false;
            HeadlessUi.Pump();
            var list = HeadlessUi.FindNamed<ListBox>(window, "PackageList")!;
            Assert.True(list.IsEffectivelyVisible);
            Assert.True(list.ItemCount > 0);
            Assert.True(HeadlessUi.FindNamed<TreeView>(window, "FolderTree")!.ItemCount > 0);
            Assert.NotNull(HeadlessUi.FindNamed<Border>(window, "PreviewRegion"));
            Assert.True(HeadlessUi.FindNamed<Button>(window, "ExportPngButton")!.IsEffectivelyEnabled);
            Assert.False(HeadlessUi.FindNamed<Button>(window, "ExportGltfButton")!.IsEffectivelyEnabled);
            Assert.Equal("loose folder", vm.GamePill.Value);
            HeadlessUi.SaveScreenshot(window, "page-assets-fixture");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    private static async Task<AssetDetailsViewModel> WaitForDetailsAsync(AssetsPageViewModel page)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (page.Details is not { IsLoading: false } && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        return page.Details ?? throw new TimeoutException("Details did not load.");
    }
}
