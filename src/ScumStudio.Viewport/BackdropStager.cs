using ScumStudio.Rendering;

namespace ScumStudio.Viewport;

/// <summary>
/// Sends the whole-island backdrop (every landscape tile, and the game's far models once they join it) to the GPU a few
/// milliseconds a frame while the previous one stays drawn, then shows it at once (render thread, GL context current). It
/// was uploaded in one frame: about 1,600 tile meshes and their ground textures, 0.9 s for a synthetic island of that size
/// (audit F323). The backdrop's meshes live in its own <see cref="GpuMeshCache"/>, so a backdrop that only adds far models
/// sends just those, and the detail levels' cache never trims what the backdrop draws.
/// </summary>
public sealed class BackdropStager
{
    private readonly GpuMeshCache _cache = new();

    /// <summary>The backdrop drawn, or null.</summary>
    public LevelScene? Scene { get; private set; }

    /// <summary>The prepared backdrop <see cref="Scene"/> shows, or null.</summary>
    public PreparedLevelScene? Shown { get; private set; }

    /// <summary>The meshes and textures the backdrop draws with (diagnostics).</summary>
    public GpuMeshCache Cache => _cache;

    /// <summary>
    /// One frame's step towards showing <paramref name="wanted"/> (null: none): sends what it still needs for at most
    /// <paramref name="budgetMs"/> (plus the item under way), and once all is sent swaps it in and frees what nothing draws
    /// any more. True once <paramref name="wanted"/> is the backdrop drawn; call it again next frame until then.
    /// </summary>
    public bool Step(SceneRenderer renderer, PreparedLevelScene? wanted, double budgetMs)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (ReferenceEquals(wanted, Shown))
        {
            return true;
        }

        if (wanted is not null && !_cache.Stage(renderer, wanted, budgetMs))
        {
            return false;
        }

        var old = Scene;
        Scene = wanted is null ? null : LevelSceneUploader.Upload(renderer, wanted, cache: _cache);
        Shown = wanted;
        old?.Dispose(); // its nodes; the meshes are the cache's
        for (var i = wanted is null ? 0 : GpuMeshCache.KeepUploads; i < GpuMeshCache.KeepUploads; i++)
        {
            _cache.Begin(); // no backdrop any more: nothing is kept for a next one
        }

        _cache.Trim(renderer);
        return true;
    }

    /// <summary>Frees the backdrop and everything it drew with, before the GL context goes (the viewport's GL de-init).</summary>
    public void Clear()
    {
        Scene?.Dispose();
        Scene = null;
        Shown = null;
        _cache.Clear();
    }
}
