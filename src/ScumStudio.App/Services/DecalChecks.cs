using System.Text.Json;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Textures;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.App.Services;

/// <summary>
/// Decals drawn in the Decals tab reach the game only through Apply decals, which bakes them into the clone's own colour
/// texture and colour mask (owner, 2026-10-18: his Rager showed its stickers in the studio and none in the game; he had
/// painted it and exported without pressing Apply decals). Before an export, the clones whose saved decals are not in the
/// pak are named: never applied, or changed since they were last applied.
/// </summary>
public static class DecalChecks
{
    /// <summary>The slot Apply decals writes for every decalled material (the clone's own colour texture).</summary>
    private const string Diffuse = "Diffuse Map";

    /// <summary>Saves of the decals this close to the bake are the bake's own run (file times).</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The clones of <paramref name="state"/> whose decals saved in <paramref name="projectDirectory"/> (<c>decals/&lt;clone&gt;.json</c>,
    /// with a shown layer) are not baked into their textures: <c>NeverApplied</c>, or false when they changed after the last
    /// Apply decals. In clone order. A decals file the tab cannot read is left to the tab.
    /// </summary>
    public static IReadOnlyList<(string Clone, bool NeverApplied)> Pending(string projectDirectory, EditState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(state);
        var pending = new List<(string, bool)>();
        foreach (var clone in state.AssetClones.OrderBy(c => c.NewPrimary, StringComparer.OrdinalIgnoreCase))
        {
            var leaf = PackageMap.Leaf(PackageMap.Normalize(clone.NewPrimary));
            var file = DecalSet.PathOf(projectDirectory, leaf);
            if (!File.Exists(file))
            {
                continue;
            }

            DecalSet decals;
            try
            {
                decals = DecalSet.FromJson(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }

            // Apply decals records the pixels of the clone's own Diffuse Map texture of every decalled material
            // (ModulePageViewModel.TextureReplaceOp); an undone apply leaves the value empty. The newest picture's time is the bake's.
            DateTime? baked = null;
            foreach (var package in clone.Packages)
            {
                var texture = TextureWrites.NewPackage(clone.NewPrimary, package.New, Diffuse);
                if (TextureWrites.Parse(state.GetAssetValue(texture, PackageMap.Leaf(texture), TextureWrites.PixelsPath)?.Current) is not { } written)
                {
                    continue;
                }

                var picture = TextureWrites.ResolveFile(projectDirectory, written.File);
                var time = picture is not null && File.Exists(picture) ? File.GetLastWriteTimeUtc(picture) : DateTime.MinValue;
                baked = baked is null || time > baked ? time : baked;
            }

            // Only hidden layers: nothing to bake, unless stickers were baked before and hidden since (review 3.5.1: the studio
            // showed a clean car, the pak still had them). Apply decals again over the hidden ones takes them out.
            var shown = decals.Layers.Any(l => !l.Hidden);
            if (baked is null)
            {
                if (shown)
                {
                    pending.Add((leaf, true));
                }
            }
            else if (File.GetLastWriteTimeUtc(file) > baked.Value + Slack)
            {
                pending.Add((leaf, false));
            }
        }

        return pending;
    }

    /// <summary>The export warnings for <paramref name="pending"/> (English, like the report's other warnings).</summary>
    public static IReadOnlyList<string> Warnings(IReadOnlyList<(string Clone, bool NeverApplied)> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return [.. pending.Select(p => p.NeverApplied
            ? $"{p.Clone}: its decals are drawn but not applied, so the pak has none of them (Vehicles, Decals tab, Apply decals, then export again)."
            : $"{p.Clone}: its decals changed after they were last applied, so the pak has the earlier ones (Vehicles, Decals tab, Apply decals, then export again; "
                + "with every sticker hidden or removed, use Reset to the game's on its Diffuse Map and Color Mask in the Textures tab instead).")];
    }
}
