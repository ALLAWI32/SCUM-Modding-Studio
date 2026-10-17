using System.Runtime.InteropServices;

namespace ScumStudio.App.Services;

/// <summary>
/// Even frame pacing while a 3D view flies (owner: "camera motion is not smooth like the game"). Avalonia's compositor
/// is paced by a timer of 60 ticks a second; on Windows a timer or sleep of a process that has not asked for a finer
/// clock wakes on the system's 15.6 ms tick, so the 16.7 ms frames came every 15.6 or 31.2 ms (the measured intervals
/// are in <c>FramePacingTests</c>): uneven frames, or half of them dropped, however fast each frame is drawn. While one
/// is held the process asks Windows for a 1 ms clock (<c>timeBeginPeriod</c>, process-wide since Windows 10 2004, as
/// games and browsers do while they animate) and gives it back when the last hold ends, so a view at rest costs nothing.
/// Elsewhere it does nothing.
/// </summary>
internal static class FramePacing
{
    private static readonly object Gate = new();
    private static int _holds;

    /// <summary>True while at least one hold is active (and the 1 ms clock was granted on Windows).</summary>
    public static bool Held { get; private set; }

    /// <summary>Asks for the fine clock; pair with <see cref="Release"/>.</summary>
    public static void Hold()
    {
        lock (Gate)
        {
            if (++_holds == 1 && OperatingSystem.IsWindows())
            {
                Held = TimeBeginPeriod(1) == 0;
            }
        }
    }

    /// <summary>Ends one hold; the last one gives the clock back.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            if (_holds == 0)
            {
                return;
            }

            if (--_holds == 0 && Held)
            {
                _ = TimeEndPeriod(1);
                Held = false;
            }
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
