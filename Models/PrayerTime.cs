using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace GoPray.Models
{
    /// <summary>One day of prayer times, exactly as a provider published them.</summary>
    public class GoPrayData
    {
        /// <summary>The calendar day these times belong to. Date component only.</summary>
        public DateTime Day { get; set; }

        public string Fajr { get; set; } = "";
        public string Sunrise { get; set; } = "";
        public string Dhuhr { get; set; } = "";
        public string Asr { get; set; } = "";
        public string Maghrib { get; set; } = "";
        public string Isha { get; set; } = "";
        public string Jumaa { get; set; } = "";

        /// <summary>
        /// When the congregation actually starts, per prayer name, where the mosque publishes it.
        /// Absent for calculated timetables — iqamah is a decision a mosque makes, not something a
        /// formula can derive.
        /// </summary>
        public Dictionary<string, string> Iqamah { get; set; } = new();

        /// <summary>The iqamah for a prayer, or "" when this source does not publish one.</summary>
        public string IqamahFor(string prayer)
            => Iqamah.TryGetValue(prayer, out var t) && !string.IsNullOrWhiteSpace(t) ? t : "";

        /// <summary>Human-readable date for display, e.g. "16 Aug 2026".</summary>
        public string Date { get; set; } = "";

        /// <summary>The five prayers plus Sunrise, in daily order.</summary>
        public List<(string Name, string Time)> GetAll() => new()
        {
            ("Fajr", Fajr),
            ("Sunrise", Sunrise),
            ("Dhuhr", Dhuhr),
            ("Asr", Asr),
            ("Maghrib", Maghrib),
            ("Isha", Isha)
        };

        /// <summary>
        /// Same list, with Jumaa slotted in next to Dhuhr when this day is a Friday and the mosque
        /// publishes one. It goes after Dhuhr whenever the mosque holds it later — which is the
        /// usual case — so the timetable stays in chronological order instead of listing 13:00
        /// above 12:30.
        /// </summary>
        public List<(string Name, string Time)> GetAllForDay()
        {
            var list = GetAll();
            if (Day.DayOfWeek != DayOfWeek.Friday) return list;
            if (string.IsNullOrEmpty(Jumaa) || Jumaa == "--:--") return list;

            int i = list.FindIndex(p => p.Name == "Dhuhr");
            if (i < 0) return list;

            bool afterDhuhr =
                TimeSpan.TryParse(Jumaa.Trim(), CultureInfo.InvariantCulture, out var jumaa) &&
                TimeSpan.TryParse(list[i].Time.Trim(), CultureInfo.InvariantCulture, out var dhuhr) &&
                jumaa >= dhuhr;

            list.Insert(afterDhuhr ? i + 1 : i, ("Jumaa", Jumaa));
            return list;
        }

        public bool IsUsable() => !string.IsNullOrEmpty(Fajr) && Fajr != "--:--";
    }

    /// <summary>
    /// Everything one fetch produced: today, plus however many days ahead the provider was willing
    /// to publish. Aladhan serves a whole month at a time, so a single successful fetch keeps the
    /// app correct through a month with no network at all — including across midnight, which a
    /// one-day payload could never survive. Mawaqit's public API only ever describes today, so a
    /// mosque timetable is a one-entry timetable; nothing here fabricates the days it did not send.
    /// </summary>
    public class GoPrayTimetable
    {
        /// <summary>Ascending by <see cref="GoPrayData.Day"/>. Past days are trimmed on save.</summary>
        public List<GoPrayData> Days { get; set; } = new();

        /// <summary>When this timetable was retrieved. Only used to decide when to refetch.</summary>
        public DateTime FetchedAt { get; set; }

        /// <summary>Location/provider fingerprint. A mismatch invalidates the cache outright.</summary>
        public string LocationKey { get; set; } = "";

        /// <summary>e.g. "Aladhan" or "Mawaqit · Masjid an-Nour".</summary>
        public string Source { get; set; } = "";

        /// <summary>The day's times, or null if this timetable does not reach that far.</summary>
        public GoPrayData? For(DateTime date)
        {
            var day = date.Date;
            foreach (var entry in Days)
                if (entry.Day.Date == day && entry.IsUsable()) return entry;
            return null;
        }

        [JsonIgnore] public GoPrayData? Today => For(DateTime.Today);

        /// <summary>Whether this timetable can actually answer "what are today's times?".</summary>
        [JsonIgnore] public bool CoversToday => Today != null;

        /// <summary>
        /// Just the provider ("Mawaqit", "Aladhan"). <see cref="Source"/> also carries the mosque
        /// name, which the UI already shows as the heading — printing it twice reads like a bug.
        /// Derived, so it is kept out of the cache file: System.Text.Json serializes a get-only
        /// property and then silently ignores it on the way back in, leaving a value in cache.json
        /// that looks authoritative and is not.
        /// </summary>
        [JsonIgnore]
        public string ProviderName
        {
            get
            {
                int separator = Source.IndexOf('·');
                return separator > 0 ? Source[..separator].Trim() : Source;
            }
        }

        /// <summary>
        /// Drops days that have already passed and sorts what is left. Keeps the cache from growing
        /// without bound as months are fetched over it, and lets <see cref="For"/> stay a linear scan.
        /// </summary>
        public void Trim()
        {
            var today = DateTime.Today;
            Days.RemoveAll(d => d.Day.Date < today || !d.IsUsable());
            Days.Sort((a, b) => a.Day.Date.CompareTo(b.Day.Date));
        }
    }

    // Explicit values: settings are serialized numerically, so these must stay stable.
    // 2 was the retired PrayZone provider and 3 the retired on-device calculation; both are
    // rejected by AppSettings normalization on load and fall back to Aladhan.
    public enum ApiProvider
    {
        Mawaqit = 0,
        Aladhan = 1
    }

    public enum CalculationMethod
    {
        MuslimWorldLeague = 1,
        ISNA = 2,
        Karachi = 3,
        UmmAlQura = 4,
        EgyptianAuthority = 5,
        Tehran = 7,
        Gulf = 8,
        Kuwait = 9,
        France = 12,
        Turkey = 13
    }
}
