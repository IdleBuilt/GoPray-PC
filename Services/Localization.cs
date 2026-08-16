using System;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace GoPray.Services
{
    /// <summary>
    /// Language and reading direction for the whole app.
    ///
    /// <para>Every visible string lives in a <see cref="ResourceDictionary"/> keyed <c>S_Something</c>,
    /// and XAML reaches it with <c>{DynamicResource S_Something}</c>. Swapping the merged dictionary
    /// therefore retranslates every label already on screen with no per-element code and no window
    /// rebuild — which is the whole reason for doing it this way rather than assigning strings from
    /// code-behind. Reading direction and the UI font ride along as resources for the same reason.</para>
    /// </summary>
    public static class Localization
    {
        /// <summary>Language codes this build ships, in the order the settings list shows them.</summary>
        public static readonly string[] Supported = { "en", "ar" };

        private static ResourceDictionary? _active;

        /// <summary>The resolved two-letter code actually in use — never "auto".</summary>
        public static string Current { get; private set; } = "en";

        /// <summary>Raised after a swap, for the few strings that cannot be a resource reference.</summary>
        public static event Action? Changed;

        /// <summary>
        /// Applies a language. "auto" (or anything unrecognised) follows Windows' UI language and
        /// falls back to English, so a fresh install is already in the user's language where GoPray
        /// has one.
        /// </summary>
        public static void Apply(string language)
        {
            var resolved = Resolve(language);
            if (_active != null && resolved == Current) return;

            var dictionary = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Strings/{resolved}.xaml", UriKind.Absolute)
            };

            var merged = Application.Current.Resources.MergedDictionaries;
            if (_active != null) merged.Remove(_active);

            // Appended last so these win over anything WPF-UI happens to key the same way.
            merged.Add(dictionary);
            _active = dictionary;
            Current = resolved;

            Changed?.Invoke();
        }

        private static string Resolve(string language)
        {
            if (!string.IsNullOrWhiteSpace(language) && Supported.Contains(language)) return language;

            var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return Supported.Contains(system) ? system : "en";
        }

        /// <summary>
        /// A string by key, for the handful of places a resource reference cannot reach — text built
        /// in code, and format strings. Returns the key itself when it is missing, which makes an
        /// untranslated string obvious on screen instead of silently blank.
        /// </summary>
        public static string T(string key)
            => Application.Current?.TryFindResource(key) as string ?? key;

        /// <summary>Same, with <see cref="string.Format(string,object[])"/> arguments applied.</summary>
        public static string T(string key, params object?[] args)
        {
            var format = T(key);
            try { return string.Format(CultureInfo.CurrentCulture, format, args); }
            catch (FormatException) { return format; }
        }

        /// <summary>
        /// Formats a Hijri date from its parts. The month names are resources (<c>S_Hijri1</c>..
        /// <c>S_Hijri12</c>) so Arabic gets its own names rather than transliterations, and the
        /// whole line is a format string so the era marker can sit on whichever side the language
        /// puts it.
        /// </summary>
        public static string HijriDate(int day, int month, int year)
        {
            if (month is < 1 or > 12) return "";
            return T("S_HijriFormat", day, T($"S_Hijri{month}"), year);
        }

        /// <summary>Localised prayer name for display; falls back to the canonical English key.</summary>
        public static string Prayer(string name) =>
            Application.Current?.TryFindResource($"S_Prayer{name}") as string ?? name;
    }
}
