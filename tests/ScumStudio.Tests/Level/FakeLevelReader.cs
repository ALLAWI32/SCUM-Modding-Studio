using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;

namespace ScumStudio.Tests.Level;

/// <summary>In-memory <see cref="ILevelReader"/> for tests: levels are built with <see cref="FakeLevelBuilder"/>.</summary>
internal sealed class FakeLevelReader : ILevelReader
{
    private readonly Dictionary<string, LevelData> _levels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<StreamingLevelInfo>> _streaming = new(StringComparer.OrdinalIgnoreCase);

    public string DisplayName => "fake levels";

    public int ReadCount { get; private set; }

    public Exception? StreamingFailure { get; set; }

    public FakeLevelReader Add(LevelData level)
    {
        _levels[level.PackagePath] = level;
        return this;
    }

    public FakeLevelReader AddStreaming(string persistentPackage, IEnumerable<string> levelPackages)
    {
        _streaming[persistentPackage] = levelPackages
            .Select((p, i) => new StreamingLevelInfo($"LevelStreamingDynamic_{i}", "LevelStreamingDynamic",
                p + "." + p[(p.LastIndexOf('/') + 1)..], p, FTransform.Identity, InitiallyLoaded: true))
            .ToList();
        return this;
    }

    public bool LevelExists(string packagePath) => _levels.ContainsKey(packagePath);

    public LevelData ReadLevel(string packagePath, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return _levels.TryGetValue(packagePath, out var level)
            ? level
            : throw new FileNotFoundException($"Package not found: {packagePath}", packagePath);
    }

    public IReadOnlyList<StreamingLevelInfo> ReadStreamingLevels(string packagePath, CancellationToken cancellationToken = default)
    {
        if (StreamingFailure is not null)
        {
            throw StreamingFailure;
        }

        return _streaming.TryGetValue(packagePath, out var list)
            ? list
            : throw new FileNotFoundException($"Package not found: {packagePath}", packagePath);
    }
}

/// <summary>
/// Builds <see cref="LevelData"/> like a cooked level: export 0 is the <c>World</c>, export 1 the <c>PersistentLevel</c>,
/// actors are outered to the level and components to their actor.
/// </summary>
internal sealed class FakeLevelBuilder
{
    private readonly List<LevelExportData> _exports = [];
    private readonly List<int> _actors = [];
    private readonly List<string> _warnings = [];

    public FakeLevelBuilder(string packagePath)
    {
        PackagePath = packagePath;
        var name = packagePath[(packagePath.LastIndexOf('/') + 1)..];
        _exports.Add(new LevelExportData { Index = 0, Name = name, ClassName = "World", ClassPath = "/Script/Engine.World" });
        _exports.Add(new LevelExportData { Index = 1, Name = "PersistentLevel", ClassName = "Level", ClassPath = "/Script/Engine.Level", OuterIndex = 0 });
    }

    public string PackagePath { get; }

    public int Actor(string name, string classPath, bool blueprint = false)
    {
        var index = _exports.Count;
        var className = classPath[(classPath.LastIndexOfAny(['.', '/']) + 1)..];
        _exports.Add(new LevelExportData
        {
            Index = index,
            Name = name,
            ClassName = className,
            ClassPath = classPath,
            IsBlueprintClass = blueprint,
            OuterIndex = 1,
            IsLoaded = true,
        });
        _actors.Add(index);
        return index;
    }

    public int Component(
        int actor,
        string name,
        string className = "SceneComponent",
        FVector? location = null,
        FRotator? rotation = null,
        FVector? scale = null,
        int? attachTo = null,
        string? mesh = null,
        IReadOnlyList<FTransform>? instances = null,
        bool scene = true,
        bool absoluteRotation = false)
    {
        var index = _exports.Count;
        _exports.Add(new LevelExportData
        {
            Index = index,
            Name = name,
            ClassName = className,
            ClassPath = "/Script/Engine." + className,
            OuterIndex = actor,
            IsLoaded = true,
            IsComponent = true,
            IsSceneComponent = scene,
            RelativeLocation = location,
            RelativeRotation = rotation,
            RelativeScale3D = scale,
            AttachParent = attachTo,
            StaticMesh = mesh,
            Instances = instances,
            AbsoluteRotation = absoluteRotation,
        });
        return index;
    }

    /// <summary>A non-component subobject of an actor or component (e.g. a BodySetup).</summary>
    public int Subobject(int outer, string name, string className)
    {
        var index = _exports.Count;
        _exports.Add(new LevelExportData { Index = index, Name = name, ClassName = className, OuterIndex = outer });
        return index;
    }

    public FakeLevelBuilder Root(int actor, int component)
    {
        _exports[actor] = _exports[actor] with { RootComponent = component };
        return this;
    }

    /// <summary>Gives a trade post its traders (what the reader takes from <c>_traderMarkers</c>).</summary>
    public FakeLevelBuilder Traders(int actor, params ScumStudio.Level.Model.TraderMarker[] markers)
    {
        _exports[actor] = _exports[actor] with { TraderMarkers = markers };
        return this;
    }

    public FakeLevelBuilder Attach(int component, int parent)
    {
        _exports[component] = _exports[component] with { AttachParent = parent };
        return this;
    }

    /// <summary>Adds an entry to ULevel.Actors that does not exist in the export table.</summary>
    public FakeLevelBuilder DanglingActor(int index)
    {
        _actors.Add(index);
        return this;
    }

    public FakeLevelBuilder Warning(string warning)
    {
        _warnings.Add(warning);
        return this;
    }

    public LevelData Build() => new()
    {
        PackagePath = PackagePath,
        Exports = _exports.ToList(),
        ActorIndices = _actors.ToList(),
        LevelExportIndex = 1,
        WorldExportIndex = 0,
        Warnings = _warnings.ToList(),
    };
}
