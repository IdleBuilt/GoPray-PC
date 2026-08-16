using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GoPray.Models
{
    /// <summary>
    /// What a single prayer is allowed to do when its time arrives. The global switches in settings
    /// decide <i>how</i> an alert looks (overlay vs Windows notification); these decide whether this
    /// particular prayer alerts at all, and whether it is allowed to make noise.
    /// </summary>
    public sealed class PrayerAlertSetting
    {
        public bool Enabled { get; set; } = true;
        /// <summary>Play the adhan. Off means the alert is shown silently.</summary>
        public bool Adhan { get; set; } = true;

        public PrayerAlertSetting Copy() => new() { Enabled = Enabled, Adhan = Adhan };
    }

    public class AppSettings
    {
        /// <summary>Every prayer that can raise an alert. Sunrise is displayed but never alerts.</summary>
        public static readonly string[] AlertablePrayers =
            { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha", "Jumaa" };

        // Location / source
        public string City { get; set; } = "Sousse";
        public string Country { get; set; } = "Tunisia";
        public CalculationMethod Method { get; set; } = CalculationMethod.MuslimWorldLeague;
        public ApiProvider Provider { get; set; } = ApiProvider.Aladhan;
        public string MawaqitMosqueUuid { get; set; } = "";
        public string MawaqitMosqueName { get; set; } = "";
        public string MawaqitMosqueSlug { get; set; } = "";

        /// <summary>
        /// Coordinates for the chosen place, when it came from somewhere that knows them (a geocoded
        /// result, or the built-in city list). Aladhan is asked by latitude/longitude when these are
        /// set, which is both exact and immune to a city name it cannot resolve.
        /// </summary>
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        [JsonIgnore]
        public bool HasCoordinates => Latitude is { } lat && Longitude is { } lon
                                      && double.IsFinite(lat) && double.IsFinite(lon);

        // Display. There is nothing here about colour: GoPray follows the Windows theme and
        // accent, with no settings of its own.
        public bool Is24Hour { get; set; } = true;
        public bool AnimationsEnabled { get; set; } = true;

        /// <summary>"auto" follows Windows; otherwise a culture code this build ships, e.g. "en"/"ar".</summary>
        public string Language { get; set; } = "auto";

        /// <summary>
        /// Days to shift the displayed Hijri date, -2..+2. The Umm al-Qura calendar is arithmetic;
        /// local moon sighting routinely lands a day either side of it, and a date the user can see
        /// is wrong undermines the times printed next to it.
        /// </summary>
        public int HijriOffset { get; set; }

        // Prayer alerts
        public bool NotificationsEnabled { get; set; } = true;
        public bool AdhanEnabled { get; set; } = true;
        public double AdhanVolume { get; set; } = 0.8;
        public bool AdhanOverlayEnabled { get; set; } = true;

        /// <summary>Per-prayer overrides, keyed by the names in <see cref="AlertablePrayers"/>.</summary>
        public Dictionary<string, PrayerAlertSetting> PrayerAlerts { get; set; } = new();

        /// <summary>Never null: an unknown prayer is treated as fully enabled.</summary>
        public PrayerAlertSetting AlertFor(string prayer)
            => PrayerAlerts.TryGetValue(prayer, out var alert) ? alert : new PrayerAlertSetting();

        /// <summary>Whether this prayer should alert at all, honouring both the per-prayer switch
        /// and the fact that an alert with no overlay and no notification is not an alert.</summary>
        public bool ShouldAlert(string prayer)
            => AlertFor(prayer).Enabled && (AdhanOverlayEnabled || NotificationsEnabled || ShouldPlayAdhan(prayer));

        /// <summary>Whether this prayer is allowed to make noise.</summary>
        public bool ShouldPlayAdhan(string prayer)
            => AdhanEnabled && AlertFor(prayer).Enabled && AlertFor(prayer).Adhan;

        /// <summary>Show the mosque's iqamah time beside each prayer, where it publishes one.</summary>
        public bool ShowIqamah { get; set; } = true;

        // Widget. Null position means "never placed yet" — NaN is not valid JSON.
        public double? WidgetLeft { get; set; }
        public double? WidgetTop { get; set; }
        /// <summary>Off by default: the widget only floats above other windows once asked to.</summary>
        public bool WidgetPinned { get; set; } = false;
        public bool WidgetVisible { get; set; } = true;

        /// <summary>
        /// Global show/hide shortcut, as a WPF gesture string such as "Ctrl+Alt+P". Empty by
        /// default — GoPray claims no shortcut until the user picks one, because a hotkey
        /// registered system-wide takes it away from every other app.
        /// </summary>
        public string ToggleWidgetHotkey { get; set; } = "";

        // What the widget shows. "Full" shows prayer, time and countdown; "Compact" drops the time
        // line; "Countdown" is the countdown alone. Nothing here changes how it is coloured.
        public string WidgetLayout { get; set; } = "Full";
        public bool WidgetShowProgress { get; set; } = true;
        public bool WidgetShowActions { get; set; } = true;
        /// <summary>"Small", "Default" or "Large".</summary>
        public string WidgetTextSize { get; set; } = "Default";

        public double WidgetScale() => WidgetTextSize switch
        {
            "Small" => 0.85,
            "Large" => 1.2,
            _ => 1.0
        };

        // Full view. Anchored above the widget by default; otherwise it reopens where it was left.
        public bool MainFollowsWidget { get; set; } = true;
        public double? MainLeft { get; set; }
        public double? MainTop { get; set; }

        // App
        public bool StartWithWindows { get; set; } = false;
        public bool DiscordRpcEnabled { get; set; } = false;
        public bool DiscordRpcShowCountdown { get; set; } = true;
        /// <summary>Whether the one-click Discord toggle appears in the full view's footer.</summary>
        public bool ShowDiscordShortcut { get; set; } = true;
        public bool OnboardingComplete { get; set; } = false;
        /// <summary>Check GitHub for a newer release on start-up.</summary>
        public bool CheckForUpdates { get; set; } = true;

        // Support. Everything in GoPray is free; these only decide when the quiet support icon
        // is allowed to hint once, and whether to show a permanent thank-you.
        public DateTime? FirstRunUtc { get; set; }
        public int RemindersDelivered { get; set; }
        /// <summary>"never", "hinted" or "dismissed".</summary>
        public string SupportPrompt { get; set; } = "never";
        public DateTime? SupporterSince { get; set; }

        /// <summary>Identity of the configured source; a change means the cached times no longer apply.</summary>
        public string LocationKey() => Provider == ApiProvider.Mawaqit && !string.IsNullOrEmpty(MawaqitMosqueUuid)
            ? $"mawaqit:{MawaqitMosqueUuid}"
            : HasCoordinates
                ? $"{Provider}:{Latitude:F4},{Longitude:F4}:{(int)Method}"
                : $"{Provider}:{City}:{Country}:{(int)Method}";

        public string LocationLabel() => Provider == ApiProvider.Mawaqit && !string.IsNullOrEmpty(MawaqitMosqueName)
            ? MawaqitMosqueName
            : string.IsNullOrWhiteSpace(City) ? "Unknown" : City;

        /// <summary>Derived from <see cref="SupporterSince"/>, so it must not be written to the
        /// settings file: System.Text.Json happily serializes a get-only property but silently
        /// ignores it on the way back in, leaving a field in the file that looks authoritative
        /// and is not.</summary>
        [JsonIgnore]
        public bool IsSupporter => SupporterSince.HasValue;

        /// <summary>
        /// The support icon may draw attention exactly once, and only after GoPray has clearly
        /// earned it: three weeks of use and fifty prayers actually reminded.
        /// </summary>
        public bool ShouldHintSupport()
        {
            if (IsSupporter || SupportPrompt != "never") return false;
            if (RemindersDelivered < 50) return false;
            return FirstRunUtc is { } first && DateTime.UtcNow - first >= TimeSpan.FromDays(21);
        }

        /// <summary>
        /// A copy that shares nothing mutable with the original. MemberwiseClone alone would hand
        /// back the <i>same</i> <see cref="PrayerAlerts"/> dictionary, so editing the clone — which
        /// is exactly what the settings page does before calling ApplySettings — would reach back
        /// and mutate the live settings, defeating the whole point of cloning.
        /// </summary>
        public AppSettings Clone()
        {
            var copy = (AppSettings)MemberwiseClone();
            copy.PrayerAlerts = new Dictionary<string, PrayerAlertSetting>(PrayerAlerts.Count);
            foreach (var (name, alert) in PrayerAlerts) copy.PrayerAlerts[name] = alert.Copy();
            return copy;
        }
    }
}
