using System.Numerics;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Textures;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The 3D view's live decal preview off the UI thread (owner: "dragging stutters"; a bake at 2048 px takes about half a
/// second): a decalled material shows its last finished bake at once while a worker bakes the new one, at most
/// <see cref="DecalDragSize"/> px while a decal is dragged and at the colour texture's full size after the release. One
/// bake runs per material; a newer request waits behind it and replaces any older one waiting, so a fast drag never
/// queues up. The paint goes on in the same worker step, so a finished bake costs the UI thread only the swap.
/// </summary>
public abstract partial class ModulePageViewModel
{
    /// <summary>Longest side of the preview bake while a decal is dragged (a quarter of the texels of 2048 px).</summary>
    private const int DecalDragSize = 1024;

    private readonly Dictionary<string, DecalLiveBake> _decalLive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DecalLiveJob> _decalRunning = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DecalLiveJob> _decalWaiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _decalSharedTold = new(StringComparer.OrdinalIgnoreCase);
    private int _decalVersion;

    /// <summary>A bake of the 3D view's decals is running on a worker (or waiting for one).</summary>
    internal bool DecalPreviewBusy => _decalRunning.Count > 0 || _decalWaiting.Count > 0;

    /// <summary>
    /// The painted colour texture of the decalled material <paramref name="material"/> for the 3D view: the newest finished
    /// bake now (the plain texture before the first one), a worker bake of the current decals shortly. Null without a UI
    /// thread to come back to (the caller then bakes at once, as tests and tools on a plain thread expect).
    /// </summary>
    private TextureImage? LiveDecalBake(string material, TextureImage diffuse, TextureImage mask, Vector4 a, Vector4? b)
    {
        if (SynchronizationContext.Current is null || DecalTarget() is not { } target)
        {
            return null;
        }

        var full = Math.Max(diffuse.Width, diffuse.Height);
        var size = DecalsDragging ? Math.Min(full, DecalDragSize) : full;
        _decalLive.TryGetValue(material, out var live);
        if (live is not null && live.Version == _decalVersion && live.Size >= size && live.A == a && live.B == b)
        {
            return live.Image;
        }

        // The same decals at the same size with another paint: only the paint goes on again.
        var reuse = live is not null && live.Version == _decalVersion && live.Size == size ? live : null;
        var parts = target.Model.Parts.Where(p => string.Equals(p.Material, material, StringComparison.OrdinalIgnoreCase)).ToList();
        var job = new DecalLiveJob(material, _decalVersion, size, a, b, diffuse, mask, parts, reuse is null ? DecalStamps(target.Model, material) : [], reuse);
        if (_decalRunning.TryGetValue(material, out var running))
        {
            if (running.Same(job))
            {
                _decalWaiting.Remove(material); // the bake running is the one wanted
            }
            else
            {
                _decalWaiting[material] = job; // replaces an older one waiting
            }
        }
        else
        {
            StartDecalBake(job);
        }

        return live?.Image ?? diffuse;
    }

    /// <summary>Forgets the finished preview bakes (another vehicle or clone: its decals are not these).</summary>
    private void ForgetDecalPreview()
    {
        _decalLive.Clear();
        _decalWaiting.Clear();
        _decalSharedTold.Clear();
    }

    private void StartDecalBake(DecalLiveJob job)
    {
        _decalRunning[job.Material] = job;
        _ = RunDecalBakeAsync(job);
    }

    private async Task RunDecalBakeAsync(DecalLiveJob job)
    {
        DecalLiveBake? done = null;
        try
        {
            done = await Task.Run(() =>
            {
                var (diffuse, mask, shared) = job.Reuse is { } kept ? (kept.Diffuse, kept.Mask, kept.SharedUv) : Compose(job);
                return new DecalLiveBake(job.Version, job.Size, job.A, job.B, diffuse, mask, PaintBaker.Bake(diffuse, mask, job.A, job.B, job.Parts)) { SharedUv = shared };
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Decal preview of {Material}: {Message}", job.Material, ex.Message);
        }

        _decalRunning.Remove(job.Material);
        if (done is not null && (!_decalLive.TryGetValue(job.Material, out var shown) || shown.Version <= done.Version))
        {
            _decalLive[job.Material] = done;
        }

        if (_decalWaiting.Remove(job.Material, out var next))
        {
            StartDecalBake(next);
        }

        if (done is not null)
        {
            Repaint(); // shows it (a newer bake waiting shows the moment it is done)
            if (!DecalsDragging && done.Version == _decalVersion)
            {
                TellSharedUv(job.Material, done.SharedUv);
            }
        }

        static (TextureImage Diffuse, TextureImage Mask, IReadOnlyList<string> SharedUv) Compose(DecalLiveJob job)
        {
            var baked = DecalBaker.Bake(job.Diffuse, job.Mask, job.Parts, job.Stamps, job.Size);
            return (baked.Diffuse, baked.Mask, baked.SharedUv);
        }
    }

    /// <summary>
    /// Says once (until it changes) which places of the car show a decal of <paramref name="material"/> too because they use
    /// the same texels (a door whose UVs are the other door's, mirrored): the game cannot show it on one of them only.
    /// </summary>
    private void TellSharedUv(string material, IReadOnlyList<string> shared)
    {
        var names = string.Join(", ", shared);
        if (_decalSharedTold.GetValueOrDefault(material, string.Empty) == names)
        {
            return;
        }

        _decalSharedTold[material] = names;
        if (shared.Count > 0)
        {
            _services.Notifications.Warning(Loc.T("Studio.Decals.Title"), Loc.F("Studio.Decals.SharedUv", names));
        }
    }

    /// <summary>A preview bake asked for: the decals of <see cref="Material"/> as they were at <see cref="Version"/>.</summary>
    private sealed record DecalLiveJob(string Material, int Version, int Size, Vector4 A, Vector4? B, TextureImage Diffuse, TextureImage Mask,
        IReadOnlyList<PreviewPart> Parts, IReadOnlyList<DecalStamp> Stamps, DecalLiveBake? Reuse)
    {
        public bool Same(DecalLiveJob other) => other.Version == Version && other.Size == Size && other.A == A && other.B == B;
    }

    /// <summary>A finished preview bake: the decals at <see cref="Version"/> composed in, then painted.</summary>
    private sealed record DecalLiveBake(int Version, int Size, Vector4 A, Vector4? B, TextureImage Diffuse, TextureImage Mask, TextureImage Image)
    {
        /// <summary>The places that show these decals too (<see cref="DecalBakeResult.SharedUv"/>).</summary>
        public IReadOnlyList<string> SharedUv { get; init; } = [];
    }
}
