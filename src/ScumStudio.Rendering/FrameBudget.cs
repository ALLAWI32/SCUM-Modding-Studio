namespace ScumStudio.Rendering;

/// <summary>
/// The Map's Smooth switch: keeps the frame time under <see cref="TargetMs"/> (owner: "high FPS AND far render distance at
/// the same time"). Each drawn frame's cost (the larger of the CPU time of its draw calls and the GPU time) goes into a
/// moving average; while it stays above the target for half a second the view is lowered one step, while it stays well
/// under it for two seconds it is raised one step. The steps, in order: foliage distance ×0.75, coarser LODs, foliage
/// distance ×0.5, half the shadow distance, no shadows, twice the smallest object drawn, one sector less of far view.
/// Texture detail is never touched (a new size means preparing the levels again, a streaming hitch).
/// Allocation-free once the settings of a level are made (<see cref="Apply"/> hands the same object back).
/// </summary>
public sealed class FrameBudget
{
    /// <summary>The frame time kept: 60 frames a second.</summary>
    public const double TargetMs = 1000.0 / 60.0;

    /// <summary>The last step of the ladder (one sector less of far view).</summary>
    public const int MaxLevel = 7;

    private const double Over = 1.05; // above this share of the target the view is too slow
    private const double Under = 0.7; // below it there is room to draw more
    private const double LowerAfterSeconds = 0.5;
    private const double RaiseAfterSeconds = 2.0;
    private const double Smoothing = 0.2; // weight of the newest frame in the average

    private double _overSince = -1;
    private double _underSince = -1;
    private RenderSettings? _baseline;
    private RenderSettings? _applied;
    private int _appliedLevel = -1;
    private bool _enabled = true;

    /// <summary>Off: the view is drawn as the quality asks (level 0).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                (Level, _overSince, _underSince) = (0, -1, -1);
            }
        }
    }

    /// <summary>How many steps the view is lowered (0 = as the quality asks).</summary>
    public int Level { get; private set; }

    /// <summary>The moving average of a frame's cost in ms (0 before the first frame).</summary>
    public double CostMs { get; private set; }

    /// <summary>
    /// Feeds one drawn frame: <paramref name="cpuMs"/> spent in its draw calls, <paramref name="gpuMs"/> on the card (NaN
    /// when not known yet), at <paramref name="nowSeconds"/>. True when <see cref="Level"/> changed.
    /// </summary>
    public bool Add(double cpuMs, double gpuMs, double nowSeconds)
    {
        var cost = double.IsNaN(gpuMs) ? cpuMs : Math.Max(cpuMs, gpuMs);
        CostMs = CostMs <= 0 ? cost : CostMs + ((cost - CostMs) * Smoothing);
        if (!_enabled)
        {
            return false;
        }

        if (CostMs > TargetMs * Over)
        {
            _underSince = -1;
            if (_overSince < 0)
            {
                _overSince = nowSeconds;
            }
            else if (nowSeconds - _overSince >= LowerAfterSeconds && Level < MaxLevel)
            {
                Level++;
                _overSince = nowSeconds; // the next step waits for the new level to be measured
                return true;
            }
        }
        else if (CostMs < TargetMs * Under)
        {
            _overSince = -1;
            if (_underSince < 0)
            {
                _underSince = nowSeconds;
            }
            else if (nowSeconds - _underSince >= RaiseAfterSeconds && Level > 0)
            {
                Level--;
                _underSince = nowSeconds;
                return true;
            }
        }
        else
        {
            (_overSince, _underSince) = (-1, -1); // around the target: hold
        }

        return false;
    }

    /// <summary>
    /// <paramref name="baseline"/> lowered by <see cref="Level"/>; the same object while neither changes (the baseline
    /// itself at level 0).
    /// </summary>
    public RenderSettings Apply(RenderSettings baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (!ReferenceEquals(baseline, _baseline) || _appliedLevel != Level)
        {
            (_baseline, _appliedLevel) = (baseline, Level);
            _applied = Lower(baseline, Level);
        }

        return _applied!;
    }

    /// <summary>The sectors of far view to draw for <paramref name="sectors"/> chosen (1-4): one less at the last step.</summary>
    public int RenderDistance(int sectors) => Math.Max(1, Math.Clamp(sectors, 1, 4) - (Level >= MaxLevel ? 1 : 0));

    /// <summary><paramref name="settings"/> lowered <paramref name="level"/> steps down the ladder.</summary>
    public static RenderSettings Lower(RenderSettings settings, int level)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return level <= 0 ? settings : settings with
        {
            ViewDistanceScale = settings.ViewDistanceScale * (level >= 3 ? 0.5f : 0.75f),
            LodBias = level >= 2 ? settings.LodBias * 0.8f : settings.LodBias,
            ShadowDistance = level >= 4 ? settings.ShadowDistance * 0.5f : settings.ShadowDistance,
            Shadows = settings.Shadows && level < 5,
            CullPixelSize = level >= 6 ? MathF.Max(1f, settings.CullPixelSize) * 2f : settings.CullPixelSize,
        };
    }

    /// <summary>
    /// The island backdrop's settings: <paramref name="detail"/> with the game's far models drawn out to
    /// <paramref name="sectors"/> (clamped to 1-4) sectors of <paramref name="sectorSizeCm"/> around the camera. Only nodes
    /// with a draw distance are cut (far models have +∞); terrain and sea (0) are always drawn.
    /// </summary>
    public static RenderSettings Backdrop(RenderSettings detail, int sectors, float sectorSizeCm)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return detail with { ObjectDrawDistance = Math.Clamp(sectors, 1, 4) * sectorSizeCm };
    }
}
