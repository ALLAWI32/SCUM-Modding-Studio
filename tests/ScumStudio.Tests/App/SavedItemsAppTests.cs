using Microsoft.Data.Sqlite;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Level.Saves;
using ScumStudio.Tests.Level.Saves;

namespace ScumStudio.Tests.App;

/// <summary>
/// Clean saved items on the Map (D02, "phantom fridges": defolt_0999, O Piqueno): choose the save, Find lists the removed
/// spawners' items, an unticked row stays, Remove ticked makes a backup first, Restore backup puts it back, and a save the
/// game still holds is refused in the user's language.
/// </summary>
[Collection(LanguageCollection.Name)] // reads localized text
public sealed class SavedItemsAppTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-saveditems-" + Guid.NewGuid().ToString("N"));

    public SavedItemsAppTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task ChooseFindRemoveTheTickedAndRestoreTheBackup()
    {
        var save = SyntheticSave.Create(_dir);
        var original = File.ReadAllBytes(save);
        var vm = new SavedItemsViewModel(new PickFile(save), () => SavedItemCleanerTests.Targets, () => null)
        {
            RunningGames = [],
            Now = () => new DateTime(2026, 10, 16, 19, 0, 0),
        };
        Assert.False(vm.FindCommand.CanExecute(null));

        await vm.ChooseCommand.ExecuteAsync(null);
        Assert.Equal(save, vm.SavePath);
        Assert.Equal(Loc.T("Map.SavedItems.Ready"), vm.Status);

        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal([100L, 102L, 103L], vm.Rows.Select(r => r.Item.Id));
        Assert.Equal("Refrigerator", vm.Rows[0].Name);
        Assert.Contains("Refrigerator_Added", vm.Rows[0].WhyText, StringComparison.Ordinal);

        vm.Rows.Single(r => r.Item.Id == 103).IsChecked = false; // kept
        await vm.RemoveCommand.ExecuteAsync(null);
        Assert.Empty(vm.Rows);
        Assert.Equal(save + ".studio-backup-20261016-190000", vm.Backup);
        Assert.Equal(original, File.ReadAllBytes(vm.Backup!));
        Assert.Equal([103L], SavedItemCleaner.Find(save, SavedItemCleanerTests.Targets, null, runningGames: []).Select(f => f.Id));

        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(original, File.ReadAllBytes(save));
    }

    [Fact]
    public async Task ASaveInUseOrAnAreaWithoutASelectionIsSaidNotDone()
    {
        var save = SyntheticSave.Create(_dir);
        var vm = new SavedItemsViewModel(new PickFile(save), () => SavedItemCleanerTests.Targets, () => null) { RunningGames = [] };
        vm.Use(save);

        vm.UseArea = true;
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("Map.SavedItems.NoSelection"), vm.Status);
        vm.UseArea = false;

        using (new FileStream(save, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            await vm.FindCommand.ExecuteAsync(null);
            Assert.Equal(Loc.T("Map.SavedItems.Busy.Open"), vm.Status);
            Assert.Empty(vm.Rows);
        }

        var running = new SavedItemsViewModel(new PickFile(save), () => SavedItemCleanerTests.Targets, () => null) { RunningGames = ["SCUMServer"] };
        running.Use(save);
        Assert.Equal(Loc.F("Map.SavedItems.Busy.Running", "SCUMServer"), running.Status);

        var none = new SavedItemsViewModel(new PickFile(save), () => [], () => null) { RunningGames = [] };
        none.Use(save);
        await none.FindCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("Map.SavedItems.NoTargets"), none.Status);
    }

    private sealed class PickFile(string path) : IDialogService
    {
        public Task<string?> PickFolderAsync(string title, string? startFolder = null) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedName, string extension, string filterName) => Task.FromResult<string?>(null);

        public Task<string?> OpenFileAsync(string title, string extension, string filterName, string? startFolder = null) => Task.FromResult<string?>(path);

        public Task SetClipboardTextAsync(string text) => Task.CompletedTask;
    }
}
