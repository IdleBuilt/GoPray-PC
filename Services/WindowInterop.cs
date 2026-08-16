using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GoPray.Services
{
    /// <summary>Win32 bits GoPray needs that WPF does not expose.</summary>
    public static class WindowInterop
    {
        private const int GwlExStyle = -20;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeLegacy = 19;   // Windows 10 1809 – 1903

        /// <summary>
        /// Tells the DWM this is a dark window. Mica is rendered light or dark from this flag — and
        /// so is the colour it falls back to when the window loses focus — so a dark app that skips
        /// it washes out to near-white the moment you click away.
        /// </summary>
        public static void SetImmersiveDarkMode(Window window, bool dark)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                int value = dark ? 1 : 0;
                if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        /// <summary>
        /// Toggles whether a window can ever take keyboard focus. WPF's ShowActivated="False" only
        /// covers the initial Show; this covers clicks too, so the compact widget card can never
        /// pull the caret out of whatever the user is typing in. The expanded view is the opposite:
        /// it is a real, typeable window, so this is turned back off while it is showing.
        /// </summary>
        public static void SetActivatable(Window window, bool activatable)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                long style = (long)GetWindowLongPtr(handle, GwlExStyle);
                style = activatable
                    ? style & ~(WsExNoActivate | WsExToolWindow)
                    : style | WsExNoActivate | WsExToolWindow;

                SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
            int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HwndTop = IntPtr.Zero;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;

        /// <summary>
        /// Lifts a window to the top of the normal z-order without giving it focus. A widget that
        /// is neither topmost nor activatable cannot raise itself, so unpinning it would otherwise
        /// look like it vanished behind whatever was in front.
        /// </summary>
        public static void RaiseWithoutActivating(Window window)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                SetWindowPos(handle, HwndTop, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        private static readonly IntPtr HwndTopmost = new(-1);
        private static readonly IntPtr HwndNoTopmost = new(-2);

        /// <summary>
        /// Sets or clears true always-on-top (HWND_TOPMOST), as opposed to the one-shot
        /// <see cref="RaiseWithoutActivating"/> bump. Pinning re-calls this every couple of
        /// seconds on a timer: a single call can still lose the top slot to another app that
        /// also asks for topmost (a game's overlay, another always-on-top utility), and only
        /// reasserting it periodically keeps the widget reliably above those. It cannot win
        /// against a DirectX exclusive-fullscreen game, which bypasses the desktop compositor
        /// entirely — no window can draw over that.
        /// </summary>
        public static void SetTopmost(Window window, bool topmost)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                SetWindowPos(handle, topmost ? HwndTopmost : HwndNoTopmost,
                    0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        private const uint MonitorDefaultToNearest = 0x00000002;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int Size;
            public RECT Monitor;
            public RECT Work;
            public uint Flags;
        }

        /// <summary>
        /// The usable area (screen minus taskbar) of the monitor containing <paramref name="target"/>,
        /// in the same device-independent units the caller's window coordinates use.
        ///
        /// <para><see cref="SystemParameters.WorkArea"/> describes the <i>primary</i> monitor and
        /// nothing else, so clamping against it dragged the expanded window and the adhan overlay
        /// back to the main display whenever the widget lived on a second one. The round trip
        /// through device pixels is what makes this correct under per-monitor DPI: the window's own
        /// composition transform is the only thing that knows its current scaling, and this app
        /// declares PerMonitorV2 in its manifest.</para>
        /// </summary>
        /// <param name="reference">Window whose DPI the coordinates are expressed in.</param>
        /// <param name="target">Rectangle of interest, in <paramref name="reference"/>'s units.</param>
        public static Rect WorkAreaAround(Window reference, Rect target)
        {
            try
            {
                var source = PresentationSource.FromVisual(reference);
                var toDevice = source?.CompositionTarget?.TransformToDevice;
                var fromDevice = source?.CompositionTarget?.TransformFromDevice;

                // Before the window has a presentation source there is no transform to work with,
                // and no widget has been placed yet either — the primary work area is the right
                // answer for a first-run, centre-of-screen placement.
                if (toDevice is not { } toDev || fromDevice is not { } fromDev)
                    return SystemParameters.WorkArea;

                var centre = toDev.Transform(new Point(
                    target.Left + target.Width / 2,
                    target.Top + target.Height / 2));

                var monitor = MonitorFromPoint(
                    new POINT { X = (int)centre.X, Y = (int)centre.Y }, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return SystemParameters.WorkArea;

                var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfoW(monitor, ref info)) return SystemParameters.WorkArea;

                var topLeft = fromDev.Transform(new Point(info.Work.Left, info.Work.Top));
                var bottomRight = fromDev.Transform(new Point(info.Work.Right, info.Work.Bottom));

                var area = new Rect(topLeft, bottomRight);
                return area.Width > 0 && area.Height > 0 ? area : SystemParameters.WorkArea;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return SystemParameters.WorkArea;
            }
        }

        public static void Foreground(Window window)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero) SetForegroundWindow(handle);
            }
            catch { }
        }

        /// <summary>Cursor position in device-independent units, for placing popups.</summary>
        public static Point CursorPosition(Visual reference)
        {
            if (!GetCursorPos(out var point)) return new Point(0, 0);

            var source = PresentationSource.FromVisual(reference);
            var transform = source?.CompositionTarget?.TransformFromDevice;

            return transform.HasValue
                ? transform.Value.Transform(new Point(point.X, point.Y))
                : new Point(point.X, point.Y);
        }
    }
}
