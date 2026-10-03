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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
