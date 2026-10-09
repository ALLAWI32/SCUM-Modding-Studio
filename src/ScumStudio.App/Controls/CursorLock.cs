using System.Runtime.InteropServices;
using Avalonia;

namespace ScumStudio.App.Controls;

/// <summary>
/// Confines the mouse to a screen rectangle and moves it (Windows), so mouse-look in the viewport never runs off the
/// screen. Every call is a no-op on other systems; check <see cref="IsSupported"/> before relying on it.
/// </summary>
internal static class CursorLock
{
    /// <summary>True where the cursor can be confined and moved.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Keeps the cursor inside <paramref name="area"/> (screen pixels).</summary>
    public static void Clip(PixelRect area)
    {
        if (IsSupported)
        {
            var rect = new NativeRect { Left = area.X, Top = area.Y, Right = area.Right, Bottom = area.Bottom };
            ClipCursor(ref rect);
        }
    }

    /// <summary>Lets the cursor move over the whole desktop again.</summary>
    public static void Release()
    {
        if (IsSupported)
        {
            ClipCursor(IntPtr.Zero);
        }
    }

    /// <summary>Moves the cursor to <paramref name="point"/> (screen pixels).</summary>
    public static void MoveTo(PixelPoint point)
    {
        if (IsSupported)
        {
            SetCursorPos(point.X, point.Y);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(IntPtr rect);

    /// <summary>
    /// True when the key or mouse button with Windows virtual-key code <paramref name="virtualKey"/> is physically down now;
    /// null where that cannot be asked (other systems).
    /// </summary>
    public static bool? IsDown(int virtualKey) => IsSupported ? (GetAsyncKeyState(virtualKey) & 0x8000) != 0 : null;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    /// <summary>
    /// True when the window in front belongs to this app (a menu or popup of it counts); null where that cannot be asked.
    /// <see cref="IsDown"/> reads the whole keyboard: W held in a game or a chat in front also reads as down.
    /// </summary>
    public static bool? AppIsInFront()
    {
        if (!IsSupported)
        {
            return null;
        }

        var window = GetForegroundWindow();
        return window != IntPtr.Zero && GetWindowThreadProcessId(window, out var process) != 0 && process == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
