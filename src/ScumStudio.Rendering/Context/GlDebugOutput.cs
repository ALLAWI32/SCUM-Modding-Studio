using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace ScumStudio.Rendering.Context;

/// <summary>
/// The driver's own messages (KHR_debug, core in GL 4.3) to a log: errors and other high- and medium-severity messages,
/// reported synchronously in the call that caused them, so the last lines of a log written before a driver crash name what
/// the driver objected to (D03: AMD cards crashed with no error in the log). Notifications and low-severity chatter are off,
/// but undefined behaviour, portability problems and errors are logged at every severity.
/// </summary>
public static class GlDebugOutput
{
    /// <summary>Messages logged per context at most (a driver repeating one warning every frame must not fill the log).</summary>
    public const int MaxMessages = 40;

    // The driver calls these delegates: they must live as long as their contexts, which outlive no process.
    private static readonly List<DebugProc> Callbacks = [];

    // The message types that point at a bug of the caller whatever their severity: what one driver lets through and
    // another may crash on (D03).
    private static readonly GLEnum[] ProblemTypes = [GLEnum.DebugTypeError, GLEnum.DebugTypeUndefinedBehavior, GLEnum.DebugTypeDeprecatedBehavior, GLEnum.DebugTypePortability];

    /// <summary>
    /// Sends the current context's debug messages to <paramref name="sink"/>; false when the driver offers no debug output.
    /// Never throws: diagnostics must not stop a viewport from starting.
    /// </summary>
    public static unsafe bool Enable(GL gl, Action<string> sink)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(sink);
        try
        {
            var logged = 0;
            DebugProc callback = (source, type, id, severity, length, message, _) =>
            {
                if (Interlocked.Increment(ref logged) > MaxMessages)
                {
                    return;
                }

                sink(new GlDebugMessage((GLEnum)source, (GLEnum)type, (uint)id, (GLEnum)severity, Text(message, length))
                     + (logged == MaxMessages ? $" [further driver messages of this context are not logged]" : string.Empty));
            };
            lock (Callbacks)
            {
                Callbacks.Add(callback);
            }

            gl.Enable(EnableCap.DebugOutput);
            gl.Enable(EnableCap.DebugOutputSynchronous);
            gl.DebugMessageControl(GLEnum.DontCare, GLEnum.DontCare, GLEnum.DontCare, 0, (uint*)null, false);
            gl.DebugMessageControl(GLEnum.DontCare, GLEnum.DontCare, GLEnum.DebugSeverityHigh, 0, (uint*)null, true);
            gl.DebugMessageControl(GLEnum.DontCare, GLEnum.DontCare, GLEnum.DebugSeverityMedium, 0, (uint*)null, true);
            foreach (var type in ProblemTypes)
            {
                gl.DebugMessageControl(GLEnum.DontCare, type, GLEnum.DontCare, 0, (uint*)null, true);
            }

            gl.DebugMessageCallback(callback, null);
            return gl.GetError() == GLEnum.NoError;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            sink($"debug output unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Every message the current context's driver sends until the capture is disposed, at every severity: the strict
    /// validation the tests run the renderer under. A debug context (<see cref="OffscreenGlContext.Create"/> with
    /// <c>debug</c>) also reports undefined behaviour and performance problems; a plain one may report errors only.
    /// </summary>
    public static unsafe GlDebugCapture Capture(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        var capture = new GlDebugCapture(gl);
        gl.Enable(EnableCap.DebugOutput);
        gl.Enable(EnableCap.DebugOutputSynchronous);
        gl.DebugMessageControl(GLEnum.DontCare, GLEnum.DontCare, GLEnum.DontCare, 0, (uint*)null, true);
        gl.DebugMessageCallback(capture.Callback, null);
        return capture;
    }

    internal static string Text(nint message, int length) =>
        (message == 0 ? string.Empty : length >= 0 ? Marshal.PtrToStringAnsi(message, length) : Marshal.PtrToStringAnsi(message))?.TrimEnd() ?? string.Empty;

    internal static bool IsProblemType(GLEnum type) => Array.IndexOf(ProblemTypes, type) >= 0;
}

/// <summary>One message of the driver's debug output.</summary>
/// <param name="Source">Who sent it (API, shader compiler, window system, …).</param>
/// <param name="Type">What it is (error, undefined behaviour, portability, performance, other).</param>
/// <param name="Id">The driver's own number for the message.</param>
/// <param name="Severity">High, medium, low or notification.</param>
/// <param name="Text">The driver's text.</param>
public readonly record struct GlDebugMessage(GLEnum Source, GLEnum Type, uint Id, GLEnum Severity, string Text)
{
    /// <summary>
    /// True for a message that points at a bug of the caller: an error, undefined behaviour, a portability or deprecation
    /// problem at any severity, or any high-severity message. Performance hints and notifications are not.
    /// </summary>
    public bool IsProblem => GlDebugOutput.IsProblemType(Type) || Severity == GLEnum.DebugSeverityHigh;

    /// <inheritdoc />
    public override string ToString() => $"{Name(Type)} ({Name(Severity)}, {Name(Source)}, id {Id}): {Text}";

    private static string Name(GLEnum value) => value.ToString().Replace("Debug", string.Empty, StringComparison.Ordinal);
}

/// <summary>The messages <see cref="GlDebugOutput.Capture"/> collects; disposing it turns the debug output off again.</summary>
public sealed class GlDebugCapture : IDisposable
{
    private readonly GL _gl;
    private readonly List<GlDebugMessage> _messages = [];
    private bool _disposed;

    internal GlDebugCapture(GL gl)
    {
        _gl = gl;
        Callback = (source, type, id, severity, length, message, _) =>
        {
            lock (_messages)
            {
                _messages.Add(new GlDebugMessage((GLEnum)source, (GLEnum)type, (uint)id, (GLEnum)severity, GlDebugOutput.Text(message, length)));
            }
        };
    }

    /// <summary>The delegate the driver calls (kept alive by this object).</summary>
    internal DebugProc Callback { get; }

    /// <summary>Every message so far, in order.</summary>
    public IReadOnlyList<GlDebugMessage> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    /// <summary>The messages that point at a bug of the caller (<see cref="GlDebugMessage.IsProblem"/>).</summary>
    public IReadOnlyList<GlDebugMessage> Problems => Messages.Where(m => m.IsProblem).ToList();

    /// <summary>Forgets the messages so far (a set-up step whose messages do not count).</summary>
    public void Clear()
    {
        lock (_messages)
        {
            _messages.Clear();
        }
    }

    /// <summary>Stops the capture (the context must still be current).</summary>
    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DebugMessageCallback(null, null);
        _gl.Disable(EnableCap.DebugOutput);
        GC.KeepAlive(Callback);
    }
}
