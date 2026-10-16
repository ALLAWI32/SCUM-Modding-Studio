using Silk.NET.OpenGL;

namespace ScumStudio.Rendering;

/// <summary>
/// The GPU time of a frame's draw calls (<c>GL_TIME_ELAPSED</c> queries in a ring of three around them): a result is read
/// only once the driver says it is available, a frame or two later, so the CPU never waits for the card. A frame whose
/// query slot is still busy is not timed. Render thread, GL context current.
/// </summary>
public sealed class GpuFrameTimer : IDisposable
{
    private const int Ring = 3;
    private readonly GL _gl;
    private readonly uint[] _queries = new uint[Ring];
    private readonly bool[] _pending = new bool[Ring];
    private int _next;
    private int _active = -1;
    private bool _disposed;

    /// <summary>Creates the queries.</summary>
    public GpuFrameTimer(GL gl)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        for (var i = 0; i < Ring; i++)
        {
            _queries[i] = _gl.GenQuery();
        }
    }

    /// <summary>Milliseconds the GPU spent on the latest frame read back; NaN until one is.</summary>
    public double LastMs { get; private set; } = double.NaN;

    /// <summary>Reads what finished and starts timing a frame (unless its slot is still busy).</summary>
    public void Begin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Collect();
        if (_active >= 0 || _pending[_next])
        {
            return;
        }

        _gl.BeginQuery(QueryTarget.TimeElapsed, _queries[_next]);
        _active = _next;
    }

    /// <summary>Stops timing the frame <see cref="Begin"/> started.</summary>
    public void End()
    {
        if (_active < 0)
        {
            return;
        }

        _gl.EndQuery(QueryTarget.TimeElapsed);
        _pending[_active] = true;
        _active = -1;
        _next = (_next + 1) % Ring;
    }

    private void Collect()
    {
        // Oldest first, so LastMs ends on the newest result.
        for (var k = 0; k < Ring; k++)
        {
            var i = (_next + k) % Ring;
            if (!_pending[i])
            {
                continue;
            }

            _gl.GetQueryObject(_queries[i], QueryObjectParameterName.ResultAvailable, out int available);
            if (available == 0)
            {
                continue;
            }

            _gl.GetQueryObject(_queries[i], QueryObjectParameterName.Result, out ulong nanoseconds);
            _pending[i] = false;
            LastMs = nanoseconds / 1_000_000.0;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_active >= 0)
        {
            _gl.EndQuery(QueryTarget.TimeElapsed);
        }

        foreach (var query in _queries)
        {
            _gl.DeleteQuery(query);
        }
    }
}
