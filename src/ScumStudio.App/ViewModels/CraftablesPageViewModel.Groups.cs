using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// A recipe's ingredients in the order the user wants (igor8802, 2026-10-21: "make it possible to rearrange places"; the game
/// lists them in the recipe's order), copied from one craftable to another (igor8802, 2026-10-20: "copy and paste the
/// ingredients"), and the project's ingredient groups (igor8802: "create, change, and view groups of ingredients";
/// redriver6155: "maybe let us create new groups?"). Every change is one Undo step and is saved at once.
/// </summary>
public sealed partial class CraftablesPageViewModel
{
    private IReadOnlyList<CraftIngredient>? _copied;
    private IngredientSource? _source;

    /// <summary>The project's ingredient groups.</summary>
    public ObservableCollection<CraftGroupRowViewModel> Groups { get; } = [];

    /// <summary>The name box of a new group.</summary>
    [ObservableProperty]
    private string _newGroupName = string.Empty;

    /// <summary>"Ingredient groups of this project (2)".</summary>
    public string GroupsTitle => Loc.F("Craftables.Groups.Title", Groups.Count);

    /// <summary>Paste has ingredients to put in.</summary>
    public bool HasCopied => _copied is { Count: > 0 };

    /// <summary>Moves <paramref name="ingredient"/> one place up in the recipe.</summary>
    [RelayCommand]
    private void MoveIngredientUp(IngredientRowViewModel? ingredient) => MoveIngredient(ingredient, -1);

    /// <summary>Moves <paramref name="ingredient"/> one place down in the recipe.</summary>
    [RelayCommand]
    private void MoveIngredientDown(IngredientRowViewModel? ingredient) => MoveIngredient(ingredient, 1);

    /// <summary>Moves an ingredient of the selected craftable by <paramref name="step"/> places (each row keeps its badge and picture).</summary>
    internal void MoveIngredient(IngredientRowViewModel? ingredient, int step)
    {
        if (ingredient is null || SelectedItem is not { } row)
        {
            return;
        }

        var at = row.Ingredients.IndexOf(ingredient);
        var to = at + step;
        if (at < 0 || to < 0 || to >= row.Ingredients.Count)
        {
            return;
        }

        Remember();
        row.Ingredients.Move(at, to);
        Save();
    }

    /// <summary>Keeps the selected craftable's ingredients for Paste.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyIngredients()
    {
        if (SelectedItem is not { } row)
        {
            return;
        }

        _copied = row.ToModel().Ingredients;
        OnPropertyChanged(nameof(HasCopied));
        PasteIngredientsCommand.NotifyCanExecuteChanged();
        _services.Notifications.Info(Title, Loc.F("Craftables.Ingredients.Copied", _copied.Count, row.Name));
    }

    /// <summary>Puts the copied ingredients in place of the selected craftable's (amounts, tools, per-level amounts and new tags as copied).</summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void PasteIngredients()
    {
        if (SelectedItem is not { } row || _copied is not { Count: > 0 } copied)
        {
            return;
        }

        Remember();
        row.SetIngredients(copied);
        Save();
        RefreshIngredients();
    }

    private bool CanPaste() => HasSelection && HasCopied;

    /// <summary>
    /// Makes a group named <see cref="NewGroupName"/>: its items are picked in the picker (several at once); its tag is
    /// <c>CI_Group_</c> + the name, never one the game or another group of the project has.
    /// </summary>
    [RelayCommand]
    private async Task NewGroupAsync()
    {
        var name = NewGroupName.Trim();
        if (!HasProject)
        {
            _services.Notifications.Warning(Title, Loc.T("Craftables.NoProject"));
            return;
        }

        if (name.Length == 0)
        {
            _services.Notifications.Warning(Title, Loc.T("Craftables.Groups.NameFirst"));
            return;
        }

        if (Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _services.Notifications.Warning(Title, Loc.F("Craftables.Groups.NameTaken", name));
            return;
        }

        EnsurePicker();
        if (await Picker.PickManyAsync(name, []).ConfigureAwait(true) is not { Count: > 0 } picked)
        {
            return;
        }

        var members = await KeepMembersAsync(picked).ConfigureAwait(true);
        if (members.Count == 0)
        {
            _services.Notifications.Warning(Title, Loc.T("Craftables.Groups.Empty"));
            return;
        }

        // The game's tags (read once per catalog): a group never takes one of their names (the export refuses it too).
        IEnumerable<string> gameTags = [];
        try
        {
            if (_services.Workspace.Catalog is { } catalog)
            {
                gameTags = (await IngredientIndex.OfAsync(catalog).ConfigureAwait(true)).Tags.Select(t => t.Name).ToList();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Ingredient tags not read: {Message}", ex.Message);
        }

        var group = new CraftGroup(name, CraftGroups.TagFor(name, gameTags.Concat(Groups.Select(g => g.Tag))), members);
        Remember();
        Groups.Add(new CraftGroupRowViewModel(group));
        NewGroupName = string.Empty;
        Save();
        OnGroupsChanged();
        _services.Notifications.Info(Title, Loc.F("Craftables.Groups.Made", group.Name, group.Tag, members.Count));
    }

    /// <summary>Picks the items of <paramref name="group"/> again (its tag and the recipes that ask for it stay).</summary>
    [RelayCommand]
    private async Task EditGroupAsync(CraftGroupRowViewModel? group)
    {
        if (group is null)
        {
            return;
        }

        EnsurePicker();
        if (await Picker.PickManyAsync(group.Name, group.Members).ConfigureAwait(true) is not { } picked || !Groups.Contains(group))
        {
            return;
        }

        var members = await KeepMembersAsync(picked).ConfigureAwait(true);
        if (members.Count == 0)
        {
            _services.Notifications.Warning(Title, Loc.T("Craftables.Groups.Empty"));
            return;
        }

        if (members.SequenceEqual(group.Members, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Remember();
        group.SetMembers(members);
        Save();
        OnGroupsChanged();
    }

    /// <summary>Deletes <paramref name="group"/>, refused while a recipe of the project asks for it (named in the warning).</summary>
    [RelayCommand]
    private void DeleteGroup(CraftGroupRowViewModel? group)
    {
        if (group is null)
        {
            return;
        }

        var users = CraftGroups.UsedBy(Items.Select(i => i.ToModel()), group.Tag);
        if (users.Count > 0)
        {
            _services.Notifications.Warning(Title, Loc.F("Craftables.Groups.InUse", group.Name, string.Join(", ", users)));
            return;
        }

        Remember();
        Groups.Remove(group);
        Save();
        OnGroupsChanged();
    }

    /// <summary>
    /// The picked items that can carry a group's tag (an entity setup of the game's own, not a DLC's); the others are named in a
    /// warning and left out.
    /// </summary>
    private async Task<IReadOnlyList<string>> KeepMembersAsync(IReadOnlyList<string> picked)
    {
        if (_source?.WhyNotMember is not { } why)
        {
            return picked;
        }

        try
        {
            var checkedItems = await Task.Run(() => picked.Select(p => (Item: p, Why: why(p))).ToList()).ConfigureAwait(true);
            var refused = checkedItems.Where(c => c.Why is not null).ToList();
            if (refused.Count > 0)
            {
                _services.Notifications.Warning(Title, Loc.F("Craftables.Groups.LeftOut",
                    string.Join("; ", refused.Select(r => r.Item[(r.Item.LastIndexOf('/') + 1)..] + " (" + r.Why + ")"))));
            }

            return checkedItems.Where(c => c.Why is null).Select(c => c.Item).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Group items not checked: {Message}", ex.Message); // the export checks them again
            return picked;
        }
    }

    /// <summary>The groups of <paramref name="file"/> (Load, Undo).</summary>
    private void ShowGroups(CraftablesFile file)
    {
        Groups.Clear();
        foreach (var group in file.Groups)
        {
            Groups.Add(new CraftGroupRowViewModel(group));
        }

        OnGroupsChanged();
    }

    /// <summary>The picker lists the groups as they are now; the open recipe shows their members.</summary>
    private void OnGroupsChanged()
    {
        OnPropertyChanged(nameof(GroupsTitle));
        Picker.SetProjectGroups(Groups.Select(g => new IngredientGroup(g.Tag, g.Name, g.Members.FirstOrDefault(), g.Members, IsProject: true)).ToList());
        RefreshIngredients();
    }
}

/// <summary>A group of the project as the page lists it: name, tag and members.</summary>
public sealed class CraftGroupRowViewModel : ObservableObject
{
    private IReadOnlyList<string> _members;

    /// <summary>Creates the row of <paramref name="group"/>.</summary>
    public CraftGroupRowViewModel(CraftGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        Name = group.Name;
        Tag = group.Tag;
        _members = group.Members;
    }

    /// <summary>Its name (what the crafting menu shows).</summary>
    public string Name { get; }

    /// <summary>Its tag (<c>CI_Group_…</c>).</summary>
    public string Tag { get; }

    /// <summary>Its items (package paths).</summary>
    public IReadOnlyList<string> Members => _members;

    /// <summary>"Members: A, B, C …".</summary>
    public string? MembersText => GroupMembers.Text(_members);

    /// <summary>Every member, one per line.</summary>
    public string? MembersTip => GroupMembers.Full(_members);

    /// <summary>The group as the project file keeps it.</summary>
    public CraftGroup ToModel() => new(Name, Tag, _members);

    /// <summary>New members (the tag stays).</summary>
    internal void SetMembers(IReadOnlyList<string> members)
    {
        _members = members;
        OnPropertyChanged(nameof(Members));
        OnPropertyChanged(nameof(MembersText));
        OnPropertyChanged(nameof(MembersTip));
    }
}

/// <summary>The places of a craftable's ingredients (Up and Down).</summary>
public sealed partial class CraftableRowViewModel
{
    /// <summary>Tells each ingredient row whether it can move up or down (after every change of the list).</summary>
    private void UpdateIngredientPlaces()
    {
        for (var i = 0; i < Ingredients.Count; i++)
        {
            Ingredients[i].CanMoveUp = i > 0;
            Ingredients[i].CanMoveDown = i < Ingredients.Count - 1;
        }
    }
}

/// <summary>An ingredient's place in the recipe.</summary>
public sealed partial class IngredientRowViewModel
{
    /// <summary>Not the first ingredient.</summary>
    [ObservableProperty]
    private bool _canMoveUp;

    /// <summary>Not the last ingredient.</summary>
    [ObservableProperty]
    private bool _canMoveDown;
}
