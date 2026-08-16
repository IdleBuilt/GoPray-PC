using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace GoPray.Services
{
    /// <summary>
    /// One system-wide shortcut for showing and hiding the widget.
    ///
    /// <para>Registered against a message-only window rather than a real one, because the shortcut
    /// has to work while GoPray has no window on screen at all — which is most of the time.</para>
    ///
    /// <para>Nothing is claimed unless the user picks a combination: a global hotkey takes that
    /// key away from every other application on the machine, which is not a thing to do by
    /// default.</para>
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        private const int WmHotkey = 0x0312;
        /// <summary>Arbitrary, but must be stable: it is how WM_HOTKEY identifies which one fired.</summary>
        private const int HotkeyId = 0x60A1;
        private static readonly IntPtr HwndMessage = new(-3);

        /// <summary>Suppresses the auto-repeat storm from a held-down shortcut.</summary>
        private const uint ModNoRepeat = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private HwndSource? _source;
        private bool _registered;

        /// <summary>The shortcut was pressed.</summary>
        public event Action? Pressed;

        /// <summary>
        /// Claims <paramref name="gesture"/>, releasing whatever was held before. An empty or
        /// unparseable gesture simply clears the shortcut. Returns false when Windows refused it,
        /// which almost always means another application already owns that combination.
        /// </summary>
        public bool Register(string? gesture)
        {
            Unregister();
            if (!TryParse(gesture, out var modifiers, out var key)) return true;

            _source ??= CreateMessageWindow();
            if (_source?.Handle is not { } handle || handle == IntPtr.Zero) return false;

            _registered = RegisterHotKey(handle, HotkeyId,
                ToWin32(modifiers) | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(key));

            return _registered;
        }

        public void Unregister()
        {
            if (!_registered || _source == null) return;
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        private HwndSource? CreateMessageWindow()
        {
            try
            {
                var source = new HwndSource(new HwndSourceParameters("GoPray.Hotkey")
                {
                    ParentWindow = HwndMessage,
                    WindowStyle = 0
                });

                source.AddHook(WndProc);
                return source;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return null;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmHotkey || wParam.ToInt32() != HotkeyId) return IntPtr.Zero;

            handled = true;
            Pressed?.Invoke();
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
            _source?.RemoveHook(WndProc);
            _source?.Dispose();
            _source = null;
        }

        // ── Gesture text ───────────────────────────────────────────────────────

        /// <summary>Modifiers in a fixed order, so a stored gesture always round-trips identically.</summary>
        private static readonly (ModifierKeys Flag, string Name)[] ModifierNames =
        {
            (ModifierKeys.Control, "Ctrl"),
            (ModifierKeys.Alt, "Alt"),
            (ModifierKeys.Shift, "Shift"),
            (ModifierKeys.Windows, "Win")
        };

        /// <summary>"Ctrl+Alt+P" from its parts, or "" when the combination is not usable.</summary>
        public static string Format(ModifierKeys modifiers, Key key)
        {
            if (!IsUsable(modifiers, key)) return "";

            var parts = new List<string>();
            foreach (var (flag, name) in ModifierNames)
                if (modifiers.HasFlag(flag)) parts.Add(name);

            parts.Add(key.ToString());
            return string.Join('+', parts);
        }

        /// <summary>
        /// A shortcut has to carry at least one modifier — registering a bare letter globally would
        /// swallow that key everywhere on the machine — and the modifier keys themselves are not
        /// shortcuts, which is what filters out the transitional state while the user is still
        /// holding Ctrl and has not pressed anything else yet.
        /// </summary>
        public static bool IsUsable(ModifierKeys modifiers, Key key)
        {
            if (modifiers == ModifierKeys.None) return false;

            return key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
                or Key.System or Key.None or Key.Escape or Key.Tab);
        }

        public static bool TryParse(string? gesture, out ModifierKeys modifiers, out Key key)
        {
            modifiers = ModifierKeys.None;
            key = Key.None;
            if (string.IsNullOrWhiteSpace(gesture)) return false;

            foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                    case "alt": modifiers |= ModifierKeys.Alt; break;
                    case "shift": modifiers |= ModifierKeys.Shift; break;
                    case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
                    default:
                        if (!Enum.TryParse(raw, ignoreCase: true, out key)) return false;
                        break;
                }
            }

            return IsUsable(modifiers, key);
        }

        private static uint ToWin32(ModifierKeys modifiers)
        {
            uint value = 0;
            if (modifiers.HasFlag(ModifierKeys.Alt)) value |= 0x0001;
            if (modifiers.HasFlag(ModifierKeys.Control)) value |= 0x0002;
            if (modifiers.HasFlag(ModifierKeys.Shift)) value |= 0x0004;
            if (modifiers.HasFlag(ModifierKeys.Windows)) value |= 0x0008;
            return value;
        }
    }
}
