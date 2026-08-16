using System;
using System.Collections.Generic;
using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace GoPray.Services
{
    /// <summary>
    /// GoPray has no theming of its own. It follows Windows: the system light/dark, the system
    /// accent and Mica on every window. WPF-UI does the painting, so this is the front door to it
    /// rather than a palette of its own — there is nothing here for a user to configure.
    /// </summary>
    public static class AppearanceService
    {
        private static readonly List<Window> Registered = new();
        private static bool _watching;

        /// <summary>Applies the system theme, and keeps applying it when Windows changes.</summary>
        public static void Initialize()
        {
            Apply();

            if (_watching) return;
            _watching = true;

            // Watched here rather than through SystemThemeWatcher: that infers the theme from the
            // *personalisation* theme file, which reports Unknown for any custom theme — and on
            // Unknown WPF-UI keeps its light dictionary, which is how a dark widget ends up with
            // black text on it. AppsUseLightTheme is the value Windows itself switches.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != UserPreferenceCategory.General) return;

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Apply();
                    foreach (var window in Registered.ToArray()) Style(window);
                }));
            };
        }

        private static void Apply()
        {
            try
            {
                ApplicationThemeManager.Apply(SystemTheme(), WindowBackdropType.Mica);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        /// <summary>Dark when Windows is set to dark apps; light otherwise.</summary>
        private static ApplicationTheme SystemTheme()
        {
            try
            {
                var value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);

                return value is int light && light == 0 ? ApplicationTheme.Dark : ApplicationTheme.Light;
            }
            catch { return ApplicationTheme.Light; }
        }

        /// <summary>Gives a window Mica and keeps it in step with the system theme.</summary>
        public static void Register(Window window)
        {
            if (!Registered.Contains(window))
            {
                Registered.Add(window);
                window.Closed += (_, _) => Registered.Remove(window);
            }

            Style(window);
        }

        private static void Style(Window window)
        {
            // A layered window draws its own pixels and cannot host a DWM material; the adhan
            // overlay is deliberately one of those.
            if (window.AllowsTransparency) return;

            try
            {
                var theme = ApplicationThemeManager.GetAppTheme();

                WindowInterop.SetImmersiveDarkMode(window, theme == ApplicationTheme.Dark);
                WindowBackgroundManager.UpdateBackground(window, theme, WindowBackdropType.Mica);
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
