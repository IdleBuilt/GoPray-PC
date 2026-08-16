using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GoPray.Services
{
    /// <summary>A place on the map, with the coordinates that make its prayer times exact.</summary>
    public sealed record GeocodedPlace(string Name, string Context, double Latitude, double Longitude,
                                       string City, string Country, bool IsMosque);

    /// <summary>
    /// Place lookup for everywhere Mawaqit does not reach, via OpenStreetMap's Nominatim.
    ///
    /// <para>Google Places would be the obvious alternative and is deliberately not used: it
    /// requires a billed API key, and a key shipped inside a desktop binary is a key anyone can
    /// extract and spend. Nominatim needs no key, covers the whole planet, and returns coordinates
    /// — which matter more than the name here, since Aladhan is asked by latitude/longitude.</para>
    ///
    /// <para>Nominatim's usage policy requires a real User-Agent identifying the application and
    /// asks for at most one request a second; the search box is debounced well past that.</para>
    /// </summary>
    public static class GeocodingService
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("GoPray", AppInfo.Version));
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue($"(+{AppInfo.ProjectUrl})"));
            return client;
        }

        /// <summary>Any place: cities, towns, neighbourhoods, landmarks.</summary>
        public static Task<List<GeocodedPlace>> SearchPlacesAsync(string query, CancellationToken token = default)
            => SearchAsync(query, mosquesOnly: false, token);

        /// <summary>
        /// Mosques as the map knows them. These have no published timetable — picking one sets the
        /// location to its coordinates and the times are calculated, which is the honest fallback
        /// when a mosque is simply not registered with Mawaqit.
        /// </summary>
        public static Task<List<GeocodedPlace>> SearchMosquesAsync(string query, CancellationToken token = default)
            => SearchAsync(query, mosquesOnly: true, token);

        private static async Task<List<GeocodedPlace>> SearchAsync(
            string query, bool mosquesOnly, CancellationToken token)
        {
            var results = new List<GeocodedPlace>();
            query = query.Trim();
            if (query.Length < 2) return results;

            var url = "https://nominatim.openstreetmap.org/search"
                    + $"?q={Uri.EscapeDataString(mosquesOnly ? $"mosque {query}" : query)}"
                    + "&format=jsonv2&limit=10&addressdetails=1";

            using var response = await Http.GetAsync(url, token);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var place = Parse(element);
                if (place == null) continue;
                if (mosquesOnly && !place.IsMosque) continue;

                results.Add(place);
            }

            return results;
        }

        private static GeocodedPlace? Parse(JsonElement element)
        {
            if (!TryCoordinate(element, "lat", out var lat)) return null;
            if (!TryCoordinate(element, "lon", out var lon)) return null;

            var display = Read(element, "display_name");
            if (display.Length == 0) return null;

            // "Masjid an-Nour, Coventry Road, Birmingham, England, B10 0UG, United Kingdom"
            // — the head is the place, the tail is the context worth showing under it.
            var parts = display.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var name = Read(element, "name");
            if (name.Length == 0) name = parts.Length > 0 ? parts[0] : display;

            // OSM tags a mosque as amenity=place_of_worship (+ religion=muslim, which the search
            // response does not carry), so the type is the only signal available here.
            bool isMosque = Read(element, "type") is "place_of_worship" or "mosque";

            var (city, country) = Locality(element, parts);

            return new GeocodedPlace(
                Name: name,
                Context: string.Join(", ", Tail(parts)),
                Latitude: lat,
                Longitude: lon,
                City: city,
                Country: country,
                IsMosque: isMosque);
        }

        /// <summary>At most three trailing components — the whole display name is a postal address
        /// and putting all of it on one line makes every result look identical.</summary>
        private static IEnumerable<string> Tail(string[] parts)
        {
            int skip = Math.Max(1, parts.Length - 3);
            for (int i = skip; i < parts.Length; i++) yield return parts[i];
        }

        /// <summary>
        /// City and country from the structured address, which is far more reliable than slicing
        /// the display string. Only used as a label and as the Aladhan fallback — the coordinates
        /// are what actually drive the timetable.
        /// </summary>
        private static (string City, string Country) Locality(JsonElement element, string[] parts)
        {
            if (!element.TryGetProperty("address", out var address))
                return (parts.Length > 1 ? parts[0] : "", parts.Length > 0 ? parts[^1] : "");

            foreach (var key in new[] { "city", "town", "village", "municipality", "county", "state" })
            {
                var value = Read(address, key);
                if (value.Length > 0) return (value, Read(address, "country"));
            }

            return ("", Read(address, "country"));
        }

        private static bool TryCoordinate(JsonElement element, string property, out double value)
        {
            value = 0;
            if (!element.TryGetProperty(property, out var v)) return false;

            return v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetDouble(out value),
                JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value),
                _ => false
            };
        }

        private static string Read(JsonElement element, string property)
            => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
    }
}
