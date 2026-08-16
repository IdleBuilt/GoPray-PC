using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using GoPray.Models;

namespace GoPray.Services
{
    public static class SettingsService
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoPray");

        private static readonly string SettingsPath = Path.Combine(Dir, "settings.json");
        private static readonly string CachePath = Path.Combine(Dir, "cache.json");

        // Settings written before the GoPray rename still live here; migrated on first load.
        private static readonly string LegacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrayerTimes");

        // AllowNamedFloatingPointLiterals keeps a stray non-finite double from making the whole
        // file unsavable; the same options are used to read it back.
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        private static readonly object Gate = new();

        public static AppSettings Load()
        {
            lock (Gate)
            {
                // No file yet means a first run, and the installer's "start with Windows" checkbox
                // has to be honoured exactly once. Without this, StartupService.Sync would see a
                // preference that was never set, decide the Run entry disagrees with it, and delete
                // the very thing the user had just ticked. After this, settings always win.
                var settings = ReadSettings()
                               ?? new AppSettings
                               {
                                   StartWithWindows = StartupService.InstallPreference()
                                                      ?? StartupService.IsEnabled()
                               };

                Normalize(settings);
                return settings;
            }
        }

        private static AppSettings? ReadSettings()
        {
            foreach (var path in new[] { SettingsPath, Path.Combine(LegacyDir, "settings.json") })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
                    if (parsed != null) return parsed;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Repairs values that older builds (or a hand-edited file) could leave out of range.</summary>
        private static void Normalize(AppSettings s)
        {
            // Also catches the retired providers — 2 (PrayZone) and 3 (on-device calculation) —
            // whose saved settings files are still out there.
            if (!Enum.IsDefined(typeof(ApiProvider), s.Provider)) s.Provider = ApiProvider.Aladhan;
            if (!Enum.IsDefined(typeof(CalculationMethod), s.Method)) s.Method = CalculationMethod.MuslimWorldLeague;
            s.AdhanVolume = Math.Clamp(s.AdhanVolume, 0, 1);
            if (string.IsNullOrWhiteSpace(s.City)) s.City = "Sousse";
            if (string.IsNullOrWhiteSpace(s.Country)) s.Country = "Tunisia";
            if (s.Provider == ApiProvider.Mawaqit && string.IsNullOrEmpty(s.MawaqitMosqueUuid))
                s.Provider = ApiProvider.Aladhan;
            if (!new[] { "Full", "Compact", "Countdown" }.Contains(s.WidgetLayout)) s.WidgetLayout = "Full";
            if (!new[] { "Small", "Default", "Large" }.Contains(s.WidgetTextSize)) s.WidgetTextSize = "Default";
            if (s.WidgetLayout == "Countdown") s.WidgetShowProgress = false;
            if (!new[] { "never", "hinted", "dismissed" }.Contains(s.SupportPrompt)) s.SupportPrompt = "never";

            // AllowNamedFloatingPointLiterals lets NaN/Infinity round-trip through the file. A
            // non-finite coordinate silently defeats every later clamp (NaN fails all comparisons)
            // and WPF reads it as "unset", which is how a window ends up dead-centre. Treat those
            // exactly like "never placed yet".
            s.WidgetLeft = Finite(s.WidgetLeft);
            s.WidgetTop = Finite(s.WidgetTop);
            s.MainLeft = Finite(s.MainLeft);
            s.MainTop = Finite(s.MainTop);

            // Same reasoning, and a non-finite coordinate would be sent to the provider verbatim.
            s.Latitude = InRange(Finite(s.Latitude), -90, 90);
            s.Longitude = InRange(Finite(s.Longitude), -180, 180);
            if (s.Latitude == null || s.Longitude == null) { s.Latitude = null; s.Longitude = null; }

            if (!Localization.Supported.Contains(s.Language)) s.Language = "auto";
            s.HijriOffset = Math.Clamp(s.HijriOffset, -PrayerService.MaxHijriOffset, PrayerService.MaxHijriOffset);
            if (!HotkeyService.TryParse(s.ToggleWidgetHotkey, out _, out _)) s.ToggleWidgetHotkey = "";

            // Every alertable prayer always has an entry, so nothing downstream has to reason
            // about a missing one — and a prayer added in a later version defaults to on.
            foreach (var prayer in AppSettings.AlertablePrayers)
                if (!s.PrayerAlerts.ContainsKey(prayer)) s.PrayerAlerts[prayer] = new PrayerAlertSetting();

            static double? Finite(double? value) =>
                value is { } v && double.IsFinite(v) ? v : null;

            static double? InRange(double? value, double min, double max) =>
                value is { } v && v >= min && v <= max ? v : null;
        }

        /// <summary>
        /// Set once the data folder has been wiped and the app is on its way out, so the ordinary
        /// save-on-close does not immediately write everything back.
        /// </summary>
        public static bool Suspended { get; set; }

        public static void Save(AppSettings settings)
        {
            if (Suspended) return;
            lock (Gate) WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, Json));
        }

        /// <summary>
        /// Deletes everything GoPray has ever written: settings, the cached timetable and the error
        /// log, in both the current folder and the pre-rename one. The caller is expected to quit
        /// immediately afterwards, so nothing in memory writes itself back out.
        /// </summary>
        public static void ClearAll()
        {
            lock (Gate)
            {
                foreach (var directory in new[] { Dir, LegacyDir })
                {
                    try
                    {
                        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                    }
                    catch (Exception ex) { App.LogError(ex); }
                }
            }
        }

        public static void CacheTimes(GoPrayTimetable timetable)
        {
            if (Suspended) return;
            lock (Gate) WriteAtomic(CachePath, JsonSerializer.Serialize(timetable, Json));
        }

        /// <summary>
        /// The stored timetable, or null when there is nothing usable. A cache written by a build
        /// that stored a single day deserializes into a timetable with no <c>Days</c> at all, which
        /// reports no coverage and is simply replaced by the first fetch — no migration needed.
        /// </summary>
        public static GoPrayTimetable? LoadCachedTimes()
        {
            lock (Gate)
            {
                foreach (var path in new[] { CachePath, Path.Combine(LegacyDir, "cache.json") })
                {
                    try
                    {
                        if (!File.Exists(path)) continue;
                        var timetable = JsonSerializer.Deserialize<GoPrayTimetable>(File.ReadAllText(path), Json);
                        if (timetable == null) continue;

                        timetable.Trim();
                        if (timetable.Days.Count > 0) return timetable;
                    }
                    catch { }
                }
                return null;
            }
        }

        /// <summary>Write via a temp file so a crash mid-write cannot leave a truncated config behind.</summary>
        private static void WriteAtomic(string path, string contents)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, contents);
                File.Move(tmp, path, overwrite: true);
            }
            catch { }
        }
    }
}
