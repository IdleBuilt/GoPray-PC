using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GoPray.Models;

namespace GoPray.Services
{
    /// <summary>Mosque lookup and timetable retrieval from mawaqit.net.</summary>
    public static class MawaqitService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

        private static readonly Regex GenericWords =
            new(@"\b(mosque|masjid|masjed|mesjid|jamii|jami|mescit)\b", RegexOptions.Compiled);

        public sealed class MawaqitMosque
        {
            public string Uuid { get; set; } = "";
            public string Name { get; set; } = "";
            public string Slug { get; set; } = "";
            public List<string> Times { get; set; } = new();
            /// <summary>Jumu'ah, which the API reports on its own rather than inside <see cref="Times"/>.</summary>
            public string Jumua { get; set; } = "";
            /// <summary>
            /// Five entries — Fajr, Dhuhr, Asr, Maghrib, Isha, with no Sunrise, so this does
            /// <i>not</i> line up index-for-index with <see cref="Times"/>. Each is either a
            /// relative offset ("+20") or an absolute time ("12:45"), and the two forms are mixed
            /// freely inside one array.
            /// </summary>
            public List<string> Iqama { get; set; } = new();
            /// <summary>The mosque publishes iqamah at all; when false the offsets are meaningless.</summary>
            public bool IqamaEnabled { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public string Localisation { get; set; } = "";

            public override string ToString() =>
                string.IsNullOrWhiteSpace(Localisation) ? Name : $"{Name} — {Localisation}";
        }

        /// <summary>
        /// Searches mosques, retrying with progressively looser variants of the query
        /// because the upstream search only matches whole words.
        /// </summary>
        public static async Task<List<MawaqitMosque>> SearchMosquesAsync(
            string query, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<MawaqitMosque>();

            foreach (var variant in SearchVariations(query))
            {
                // Each variant is another round trip, up to eight of them at an eight-second
                // timeout. Abandoning the whole chain the moment the user types again is the
                // difference between a responsive search box and a minute-long queue of requests
                // for words that are no longer on screen.
                token.ThrowIfCancellationRequested();

                try
                {
                    var results = await FetchAsync(variant, token);
                    if (results.Count > 0) return results;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { }
            }
            return new List<MawaqitMosque>();
        }

        /// <summary>
        /// The mosque's timetable — which is today and only today. Mawaqit's public search endpoint
        /// publishes a single <c>times</c> array for the current day and offers no calendar, so
        /// this returns a one-day timetable rather than inventing days it was never given. Days
        /// beyond today simply do not exist for a Mawaqit source until the next successful fetch.
        /// </summary>
        public static async Task<GoPrayTimetable> FetchTimetableAsync(string uuid, string name, string slug)
        {
            var mosque = await FindAsync(uuid, slug, name)
                ?? throw new InvalidOperationException($"Mawaqit mosque not found: {name}");

            if (mosque.Times.Count < 6)
                throw new InvalidOperationException($"Mawaqit returned an incomplete timetable for {mosque.Name}");

            var today = new GoPrayData
            {
                Day = DateTime.Today,
                Fajr = TimeCleaning.Clean(mosque.Times[0]),
                Sunrise = TimeCleaning.Clean(mosque.Times[1]),
                Dhuhr = TimeCleaning.Clean(mosque.Times[2]),
                Asr = TimeCleaning.Clean(mosque.Times[3]),
                Maghrib = TimeCleaning.Clean(mosque.Times[4]),
                Isha = TimeCleaning.Clean(mosque.Times[5]),
                // From the mosque's own "jumua" field, never Times[6]. Times is the six daily
                // entries (Fajr, Shuruq, Dhuhr, Asr, Maghrib, Isha) for effectively every mosque,
                // so reading index 6 left Jumaa empty everywhere — and on the rare mosque that
                // does return a seventh entry, that entry is not Jumu'ah.
                Jumaa = string.IsNullOrWhiteSpace(mosque.Jumua) ? "" : TimeCleaning.Clean(mosque.Jumua),
                Date = DateTime.Today.ToString("dd MMM yyyy")
            };

            if (!today.IsUsable())
                throw new InvalidOperationException($"Mawaqit returned no usable times for {mosque.Name}");

            ApplyIqamah(today, mosque);

            return new GoPrayTimetable
            {
                Days = { today },
                Source = $"Mawaqit · {mosque.Name}"
            };
        }

        /// <summary>
        /// Resolves the mosque's iqamah column onto the day. The array skips Sunrise, so it is
        /// walked against the five prayers rather than against <see cref="MawaqitMosque.Times"/>,
        /// and each entry is either an offset from the adhan ("+20") or an absolute time ("12:45").
        /// Jumu'ah takes Dhuhr's offset, which is what a mosque publishing one Friday time means.
        /// </summary>
        private static void ApplyIqamah(GoPrayData day, MawaqitMosque mosque)
        {
            if (!mosque.IqamaEnabled || mosque.Iqama.Count < 5) return;

            var prayers = new[] { "Fajr", "Dhuhr", "Asr", "Maghrib", "Isha" };
            var adhan = new[] { day.Fajr, day.Dhuhr, day.Asr, day.Maghrib, day.Isha };

            for (int i = 0; i < prayers.Length; i++)
            {
                var resolved = ResolveIqamah(mosque.Iqama[i], adhan[i]);
                if (resolved.Length > 0) day.Iqamah[prayers[i]] = resolved;
            }

            if (day.Jumaa.Length > 0 && day.Jumaa != "--:--")
            {
                var resolved = ResolveIqamah(mosque.Iqama[1], day.Jumaa);
                if (resolved.Length > 0) day.Iqamah["Jumaa"] = resolved;
            }
        }

        /// <summary>
        /// Longest gap between adhan and iqamah that is believable. Mosques hand-enter this column
        /// and sometimes get it wrong — one in the test set publishes "15:45" for a Dhuhr whose
        /// adhan is 12:29. A wrong congregation time printed beside a correct adhan is worse than
        /// no congregation time, so anything outside the window is dropped rather than shown.
        /// </summary>
        private static readonly TimeSpan MaxIqamahDelay = TimeSpan.FromHours(2);

        /// <summary>"+20" against "12:29" becomes "12:49"; "12:45" is already the answer.</summary>
        private static string ResolveIqamah(string entry, string adhanTime)
        {
            entry = (entry ?? "").Trim();
            if (entry.Length == 0) return "";
            if (!TimeSpan.TryParse(adhanTime.Trim(), CultureInfo.InvariantCulture, out var adhan)) return "";

            TimeSpan at;

            if (entry.StartsWith('+') || entry.StartsWith('-'))
            {
                if (!int.TryParse(entry, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minutes))
                    return "";
                at = adhan + TimeSpan.FromMinutes(minutes);
            }
            else if (!TimeSpan.TryParse(entry, CultureInfo.InvariantCulture, out at))
            {
                return "";
            }

            // Wrapped, so an Isha iqamah that crosses midnight reads as a time rather than a
            // negative — and so the delay below is measured the short way round.
            at = TimeSpan.FromTicks((at + TimeSpan.FromDays(1)).Ticks % TimeSpan.TicksPerDay);

            var delay = TimeSpan.FromTicks((at - adhan + TimeSpan.FromDays(1)).Ticks % TimeSpan.TicksPerDay);
            if (delay > MaxIqamahDelay) return "";

            return $"{at.Hours:D2}:{at.Minutes:D2}";
        }

        /// <summary>
        /// Locates a known mosque by uuid, searching its name before falling back to its slug.
        /// Name first because the upstream search matches whole words against the mosque's real
        /// name: a slug is a hyphen-joined transliteration ("jm-ltwb-sws-4081-tunisia") that
        /// usually matches nothing, and <see cref="SearchVariations"/> then splits it on the
        /// hyphens and tries each fragment — eight round trips at an eight-second timeout before
        /// the name it should have started with.
        /// </summary>
        private static async Task<MawaqitMosque?> FindAsync(string uuid, string slug, string name)
        {
            foreach (var term in new[] { name, slug }.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                var match = (await SearchMosquesAsync(term)).FirstOrDefault(m => m.Uuid == uuid);
                if (match != null) return match;
            }
            return null;
        }

        /// <summary>
        /// Progressively looser forms of the query. The upstream search only matches whole words,
        /// so the useful variants are: what the user typed, the same folded through
        /// <see cref="TextMatching"/> (which drops accents and the Arabic article — "جامع الرحمة"
        /// also tried as "رحمه"), the distinctive words on their own once the generic
        /// mosque/masjid noise is gone, and finally that noise added back for people who typed only
        /// the distinguishing part.
        /// </summary>
        private static IEnumerable<string> SearchVariations(string query)
        {
            var typed = query.Trim();
            var variations = new List<string> { typed };

            var folded = TextMatching.Normalize(typed);
            if (folded.Length >= 2) variations.Add(folded);

            var stripped = GenericWords.Replace(folded, " ").Trim();
            if (stripped.Length >= 2) variations.Add(stripped);

            // Longest first: the distinctive word narrows the results far better than a short one.
            variations.AddRange(TextMatching.Tokenize(stripped)
                .Where(t => t.Length >= 3)
                .OrderByDescending(t => t.Length));

            variations.Add($"{typed} mosque");
            variations.Add($"{typed} masjid");

            return variations
                .Select(v => v.Trim())
                .Where(v => v.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static async Task<List<MawaqitMosque>> FetchAsync(string query, CancellationToken token)
        {
            var url = $"https://mawaqit.net/api/2.0/mosque/search?word={Uri.EscapeDataString(query)}";
            using var response = await Http.GetAsync(url, token);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var results = new List<MawaqitMosque>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var uuid = ReadString(element, "uuid");
                var name = ReadString(element, "name");
                if (uuid.Length == 0 || name.Length == 0) continue;

                var times = new List<string>();
                if (element.TryGetProperty("times", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var t in arr.EnumerateArray())
                        times.Add(t.GetString() ?? "");

                // Entries without a full timetable are useless to us.
                if (times.Count < 6) continue;

                var iqama = new List<string>();
                if (element.TryGetProperty("iqama", out var iq) && iq.ValueKind == JsonValueKind.Array)
                    foreach (var v in iq.EnumerateArray())
                        iqama.Add(v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "");

                results.Add(new MawaqitMosque
                {
                    Uuid = uuid,
                    Name = name,
                    Slug = ReadString(element, "slug"),
                    Times = times,
                    Jumua = ReadString(element, "jumua"),
                    Iqama = iqama,
                    IqamaEnabled = element.TryGetProperty("iqamaEnabled", out var ie)
                                   && ie.ValueKind == JsonValueKind.True,
                    Latitude = ReadNumber(element, "latitude"),
                    Longitude = ReadNumber(element, "longitude"),
                    Localisation = ReadString(element, "localisation")
                });
            }
            return results;
        }

        private static string ReadString(JsonElement element, string property)
            => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        /// <summary>Mawaqit sends coordinates as JSON numbers on some records and strings on others.</summary>
        private static double? ReadNumber(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
                JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var s) ? s : null,
                _ => null
            };
        }
    }
}
