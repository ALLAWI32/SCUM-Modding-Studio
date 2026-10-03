using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;

namespace ScumStudio.App.ViewModels;

/// <summary>One actor of the loaded levels in the entity list.</summary>
public sealed partial class ActorItemViewModel : ViewModelBase
{
    /// <summary>Creates an item.</summary>
    public ActorItemViewModel(LevelDocument level, ActorRecord actor, uint selectableId)
    {
        Level = level;
        Actor = actor;
        SelectableId = selectableId;
        Reference = new ActorRef(level.PackagePath, actor.Name);
        var mesh = actor.StaticMeshPath ?? actor.Components.FirstOrDefault(c => c.StaticMeshPath is not null)?.StaticMeshPath;
        MeshName = mesh is null ? string.Empty : mesh[(mesh.LastIndexOf('/') + 1)..].Split('.')[0];
        var l = actor.WorldTransform.Translation;
        LocationText = string.Create(CultureInfo.InvariantCulture, $"{l.X / 100f:0.0}, {l.Y / 100f:0.0}, {l.Z / 100f:0.0} m");
    }

    /// <summary>The level document.</summary>
    public LevelDocument Level { get; }

    /// <summary>The actor.</summary>
    public ActorRecord Actor { get; }

    /// <summary>Id used by the viewport.</summary>
    public uint SelectableId { get; }

    /// <summary>Journal reference (level package + actor name).</summary>
    public ActorRef Reference { get; }

    /// <summary>Actor name.</summary>
    public string Name => Actor.Name;

    /// <summary>Short class name.</summary>
    public string ClassName => Actor.ClassName;

    /// <summary>Short mesh name, or empty.</summary>
    public string MeshName { get; }

    /// <summary>World location in metres.</summary>
    public string LocationText { get; }

    /// <summary>Level name.</summary>
    public string LevelName => Level.Name;

    /// <summary>Category.</summary>
    public ActorKind Kind => Actor.Kind;

    /// <summary>Icon key for the kind.</summary>
    public string IconKey => Actor.Kind switch
    {
        ActorKind.StaticMeshActor => "Icon.Mesh",
        ActorKind.Blueprint => "Icon.Blueprint",
        ActorKind.Light => "Icon.Sun",
        ActorKind.Volume => "Icon.Cube3d",
        _ => "Icon.File",
    };

    /// <summary>True when the actor has geometry in the viewport.</summary>
    public bool HasMesh => MeshName.Length > 0 || Actor.InstanceTransforms.Count > 0;

    /// <summary>True for an actor the project added (a duplicate or a new actor), not stored in the level package.</summary>
    public bool IsAdded { get; init; }

    /// <summary>For a duplicate: selectable id of the copied actor (0 otherwise); the viewport clones its placements.</summary>
    public uint SourceId { get; init; }

    /// <summary>Deleted in the open project.</summary>
    [ObservableProperty]
    private bool _isDeleted;

    /// <summary>Whether the item matches <paramref name="text"/> (name, class, mesh, level).</summary>
    public bool Matches(string text) =>
        text.Length == 0
        || Name.Contains(text, StringComparison.OrdinalIgnoreCase)
        || ClassName.Contains(text, StringComparison.OrdinalIgnoreCase)
        || MeshName.Contains(text, StringComparison.OrdinalIgnoreCase)
        || LevelName.Contains(text, StringComparison.OrdinalIgnoreCase);
}
