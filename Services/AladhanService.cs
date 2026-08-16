using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GoPray.Models;

namespace GoPray.Services
{
    /// <summary>Generic city-based timetable calculation from api.aladhan.com. No account needed.</summary>
    public static class AladhanService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>Fetch a second month once today is within this many days of the month's end.</summary>
        private const int MinimumDaysAhead = 7;

        /// <summary>
        /// A month of times in one request, via Aladhan's calendar endpoint. Fetching the whole
        /// month costs one request instead of one per day and is what lets the app stay correct
        /// across midnight — and for weeks — with no network. A second month is pulled in only
        /// near the end of the current one, so there are always at least a week of days ahead.
        /// </summary>
        /// <param name="latitude">Exact coordinates when the chosen place carries them. Preferred
        /// over the city name: it is precise, and immune to a place name Aladhan cannot geocode —
        /// which is most of them once the user picks something smaller than a capital.</param>
        public static async Task<GoPrayTimetable> FetchTimetableAsync(
            string city, string country, CalculationMethod method,
            double? latitude = null, double? longitude = null)
        {
            var today = DateTime.Today;
            var timetable = new GoPrayTimetable { Source = "Aladhan" };

            await AppendMonthAsync(timetable, city, country, method, today, latitude, longitude);

            if (DateTime.DaysInMonth(today.Year, today.Month) - today.Day < MinimumDaysAhead)
            {
                // Best-effort: a failure here still leaves the current month usable.
                try
                {
                    await AppendMonthAsync(timetable, city, country, method,
                        today.AddMonths(1), latitude, longitude);
                }
                catch (Exception ex) { App.LogError(ex); }
            }

            timetable.Trim();
            return timetable;
        }

        private static async Task AppendMonthAsync(
            GoPrayTimetable timetable, string city, string country, CalculationMethod method,
            DateTime month, double? latitude, double? longitude)
        {
            bool byCoordinates = latitude is { } la && longitude is { } lo
                                 && double.IsFinite(la) && double.IsFinite(lo);

            var url = byCoordinates
                ? "https://api.aladhan.com/v1/calendar"
                  + $"?latitude={latitude!.Value.ToString(CultureInfo.InvariantCulture)}"
                  + $"&longitude={longitude!.Value.ToString(CultureInfo.InvariantCulture)}"
                  + $"&method={(int)method}"
                  + $"&month={month.Month}&year={month.Year}"
                : "https://api.aladhan.com/v1/calendarByCity"
                  + $"?city={Uri.EscapeDataString(city)}"
                  + $"&country={Uri.EscapeDataString(country)}"
                  + $"&method={(int)method}"
                  + $"&month={month.Month}&year={month.Year}";

            using var response = await Http.GetAsync(url);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("data", out var days) || days.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Aladhan returned no calendar data");

            foreach (var entry in days.EnumerateArray())
            {
                var day = ParseDay(entry);
                if (day != null) timetable.Days.Add(day);
            }
        }

        private static GoPrayData? ParseDay(JsonElement entry)
        {
            if (!entry.TryGetProperty("timings", out var timings)) return null;
            if (!entry.TryGetProperty("date", out var date)) return null;

            // "16-08-2026". Parsed with the invariant culture and an explicit format: the day comes
            // first regardless of what the machine's own date format happens to be.
            var gregorian = date.TryGetProperty("gregorian", out var g) ? Read(g, "date") : "";
            if (!DateTime.TryParseExact(gregorian, "dd-MM-yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return null;

            var result = new GoPrayData
            {
                Day = parsed.Date,
                Fajr = TimeCleaning.Clean(Read(timings, "Fajr")),
                Sunrise = TimeCleaning.Clean(Read(timings, "Sunrise")),
                Dhuhr = TimeCleaning.Clean(Read(timings, "Dhuhr")),
                Asr = TimeCleaning.Clean(Read(timings, "Asr")),
                Maghrib = TimeCleaning.Clean(Read(timings, "Maghrib")),
                Isha = TimeCleaning.Clean(Read(timings, "Isha")),
                Date = Read(date, "readable")
            };

            return result.IsUsable() ? result : null;
        }

        private static string Read(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
    }
}
