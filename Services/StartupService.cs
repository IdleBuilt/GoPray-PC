using System;
using Microsoft.Win32;

namespace GoPray.Services
{
    public static class StartupService
    {
        private const string AppName = "GoPray";
        private static readonly string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>Where Setup records the state of its "start with Windows" checkbox.</summary>
        private const string InstallKeyPath = @"Software\KiraiEEE\GoPray";
        private const string InstallValueName = "StartWithWindows";

        /// <summary>
        /// What the installer's checkbox was set to, or null when GoPray was not installed by Setup
        /// (a portable copy, or a build run straight from bin).
        ///
        /// <para>Setup cannot write the Run entry itself: an elevated install runs as the
        /// administrator, so HKCU there is the <i>administrator's</i> profile, not the profile of
        /// the person who will actually be signing in. It records the intent under HKLM instead and
        /// the app applies it per user on first run, which is the only point at which the right
        /// HKCU is in scope.</para>
        /// </summary>
        public static bool? InstallPreference()
        {
            // Per-user installs write HKCU, per-machine installs write HKLM; check the narrower
            // scope first so a user's own install beats a machine-wide default.
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = root.OpenSubKey(InstallKeyPath, false);
                    if (key?.GetValue(InstallValueName) is int value) return value != 0;
                }
                catch { }
            }

            return null;
        }

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(AppName) != null;
            }
            catch { }
            return false;
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
                if (key == null) return;

                if (enabled)
                {
                    var exePath = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exePath)) return;
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
            catch { }
        }

        /// <summary>
        /// Makes the registry agree with the saved preference on every launch. Without this the
        /// two drift apart for good: reinstalling to a different folder, or moving the portable
        /// exe, leaves the Run entry pointing at a path that no longer exists, and the settings
        /// toggle still reads "on" while nothing actually starts with Windows.
        /// </summary>
        public static void Sync(bool enabled)
        {
            if (enabled != IsEnabled()) { SetEnabled(enabled); return; }

            // Enabled and present, but possibly recorded against a stale path.
            if (!enabled) return;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                var current = key?.GetValue(AppName) as string;
                var expected = $"\"{Environment.ProcessPath}\"";

                if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                    SetEnabled(true);
            }
            catch { }
        }
    }
}
