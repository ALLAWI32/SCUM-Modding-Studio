namespace ScumStudio.Level.Editing;

/// <summary>
/// Renames what edits create, for a shared mod applied to another project: an added actor whose name the receiver already
/// uses, an added tree whose instance index is taken, a placed trader whose name (its economy section) is taken. Every
/// reference to the old name follows, nested edits included (a remover's original, a batch's children).
/// </summary>
public static class EditOpRemap
{
    /// <summary>
    /// Every edit type <see cref="Rename"/> rewrites. An edit type added to <see cref="EditOp"/> must be added here (a test
    /// compares this set with the journal's types), or sharing a project that uses it is refused.
    /// </summary>
    public static IReadOnlySet<Type> KnownTypes { get; } = new HashSet<Type>
    {
        typeof(DeleteActorOp), typeof(RestoreActorOp), typeof(DeleteAllOfKindOp), typeof(RestoreAllOfKindOp), typeof(DuplicateActorOp),
        typeof(SetTransformOp), typeof(SetInstanceTransformOp), typeof(DeleteInstanceOp), typeof(RestoreInstanceOp), typeof(AddInstanceOp),
        typeof(RemoveAddedInstanceOp), typeof(AddStaticMeshActorOp), typeof(AddBlueprintActorOp), typeof(RemoveAddedActorOp),
        typeof(CloneAssetOp), typeof(RemoveAssetCloneOp), typeof(SetAssetValueOp), typeof(ReplaceAssetOp), typeof(BatchOp),
        typeof(BendActorOp), typeof(SwaySegmentOp), typeof(SetSpawnPointsOp), typeof(ReplaceMeshOp), typeof(SetLootOp), typeof(SetClearGrassOp),
        typeof(SetNoLootOp), typeof(DeleteComponentsOp), typeof(RestoreComponentsOp), typeof(SetSpawnChoiceOp), typeof(AddSpawnPlaceOp),
        typeof(SetEconomyEntryOp), typeof(SetEconomySettingOp), typeof(SetEconomySectionOp), typeof(ReplaceEconomyOp),
        typeof(TerrainHoleOp), typeof(AddSubZoneOp), typeof(RemoveSubZoneOp),
    };

    /// <summary>
    /// <paramref name="op"/> with every actor, instance and trader name of the maps replaced. An instance not in
    /// <paramref name="instances"/> follows its actor's new name.
    /// </summary>
    /// <param name="op">The edit.</param>
    /// <param name="actors">Old actor to new actor (same level).</param>
    /// <param name="instances">Old instance to new instance (its actor already renamed).</param>
    /// <param name="traders">Old trader name to new trader name.</param>
    /// <exception cref="NotSupportedException">An edit type this version does not know (see <see cref="KnownTypes"/>).</exception>
    public static EditOp Rename(
        EditOp op, IReadOnlyDictionary<ActorRef, ActorRef> actors, IReadOnlyDictionary<InstanceRef, InstanceRef> instances, IReadOnlyDictionary<string, string> traders)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(traders);
        ActorRef A(ActorRef actor) => actors.TryGetValue(actor, out var to) ? to : actor;
        string Name(string level, string actor) => A(new ActorRef(level, actor)).Actor;
        InstanceRef I(InstanceRef instance) =>
            instances.TryGetValue(instance, out var to) ? to
            : actors.TryGetValue(instance.ActorRef, out var owner) ? instance with { Level = owner.Level, Actor = owner.Actor } : instance;
        EditOp R(EditOp child) => Rename(child, actors, instances, traders);

        return op switch
        {
            DeleteActorOp d => d with { Target = A(d.Target) },
            RestoreActorOp r => r with { Target = A(r.Target) },
            DeleteAllOfKindOp all => all with { Actors = all.Actors.Select(A).ToList(), Instances = all.Instances.Select(I).ToList() },
            RestoreAllOfKindOp all => all with { Actors = all.Actors.Select(A).ToList(), Instances = all.Instances.Select(I).ToList() },
            DuplicateActorOp dup => dup with { Source = A(dup.Source), NewName = Name(dup.Source.Level, dup.NewName) },
            SetTransformOp set => set with { Target = A(set.Target) },
            SetInstanceTransformOp set => set with { Target = I(set.Target) },
            DeleteInstanceOp delete => delete with { Target = I(delete.Target) },
            RestoreInstanceOp restore => restore with { Target = I(restore.Target) },
            AddInstanceOp add => add with { Target = I(add.Target) },
            RemoveAddedInstanceOp remove => remove with { Target = I(remove.Target), Original = R(remove.Original) },
            AddStaticMeshActorOp add => add with { NewName = Name(add.Level, add.NewName) },
            AddBlueprintActorOp bp => bp with
            {
                NewName = Name(bp.Level, bp.NewName),
                Source = A(bp.Source),
                Trader = bp.Trader is { } t && traders.TryGetValue(t.Name, out var trader) ? t with { Name = trader } : bp.Trader,
                Mechanic = bp.Mechanic is { } post ? Name(bp.Level, post) : null,
            },
            RemoveAddedActorOp remove => remove with { Target = A(remove.Target), Original = R(remove.Original) },
            CloneAssetOp or SetAssetValueOp or ReplaceAssetOp => op,
            RemoveAssetCloneOp remove => remove with { Original = R(remove.Original) },
            BatchOp batch => batch with { Ops = batch.Ops.Select(R).ToList() },
            BendActorOp bend => bend with { Target = A(bend.Target) },
            SwaySegmentOp sway => sway with { Target = A(sway.Target) },
            SetSpawnPointsOp points => points with { Target = A(points.Target) },
            ReplaceMeshOp mesh => mesh with { Target = A(mesh.Target) },
            SetLootOp loot => loot with { Target = A(loot.Target) },
            SetClearGrassOp grass => grass with { Target = A(grass.Target) },
            SetNoLootOp noLoot => noLoot with { Target = A(noLoot.Target) },
            DeleteComponentsOp parts => parts with { Target = A(parts.Target) },
            RestoreComponentsOp parts => parts with { Target = A(parts.Target) },
            SetSpawnChoiceOp choice => choice with { Target = A(choice.Target) },
            AddSpawnPlaceOp place => place with { NewName = Name(place.Created.Level, place.NewName) },
            SetEconomyEntryOp entry => traders.TryGetValue(entry.Trader, out var renamed) ? entry with { Trader = renamed } : entry,
            SetEconomySectionOp section => traders.TryGetValue(section.Trader, out var renamed) ? section with { Trader = renamed } : section,
            SetEconomySettingOp or ReplaceEconomyOp => op, // a share carries the economy as a file of its own, never these
            TerrainHoleOp hole => hole, // a place on the ground: no names to follow
            AddSubZoneOp or RemoveSubZoneOp => op, // a box in a stock zone: no names to follow
            _ => throw new NotSupportedException($"Edits of type {op.GetType().Name} cannot be shared by this version."),
        };
    }
}
