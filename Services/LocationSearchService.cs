using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GoPray.Models;

namespace GoPray.Services
{
    /// <summary>Which pool the picker is searching.</summary>
    public enum SearchMode
    {
        /// <summary>Mosques registered with Mawaqit, plus the built-in city list. The default.</summary>
        Mosques,
        /// <summary>Everywhere on the map, via geocoding. The "I can't find my mosque" escape hatch.</summary>
        Places
    }

    /// <summary>A mosque or place the user can pick as their prayer-times source.</summary>
    public sealed class LocationResult
    {
        /// <summary>True only for a Mawaqit mosque, which is the one kind that has a real timetable.</summary>
        public bool IsMosque { get; init; }
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";

        public string MosqueUuid { get; init; } = "";
        public string MosqueSlug { get; init; } = "";
        public string City { get; init; } = "";
        public string Country { get; init; } = "";
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }

        /// <summary>Writes this choice into the given settings, including the matching provider.</summary>
        public void ApplyTo(AppSettings settings)
        {
            settings.Latitude = Latitude;
            settings.Longitude = Longitude;

            if (IsMosque)
            {
                settings.Provider = ApiProvider.Mawaqit;
                settings.MawaqitMosqueUuid = MosqueUuid;
                settings.MawaqitMosqueName = Title;
                settings.MawaqitMosqueSlug = MosqueSlug;

                // City/Country are not how a mosque is identified — LocationLabel and the settings
                // card both read MawaqitMosqueName. They exist purely as the fallback the Aladhan
                // path uses when Mawaqit is unreachable. Storing the mosque's *name* in City, as
                // this used to, guaranteed that fallback failed: "Masjid an-Nour" is not a city,
                // so Aladhan rejected it outright.
                var (city, country) = SplitLocality(Subtitle);
                if (city.Length > 0) settings.City = city;
                if (country.Length > 0) settings.Country = country;
            }
            else
            {
                if (settings.Provider == ApiProvider.Mawaqit) settings.Provider = ApiProvider.Aladhan;
                settings.MawaqitMosqueUuid = "";
                settings.MawaqitMosqueName = "";
                settings.MawaqitMosqueSlug = "";
                if (City.Length > 0) settings.City = City;
                if (Country.Length > 0) settings.Country = Country;

                // A geocoded place is labelled by what the user actually picked, which may be a
                // neighbourhood or a mosque the map knows and Mawaqit does not.
                if (Title.Length > 0) settings.City = Title;
            }
        }

        /// <summary>
        /// Pulls a city and country out of a Mawaqit "localisation" line, which reads as a postal
        /// address: "12 rue des Fleurs, 75011 Paris, France". The last comma-separated part is the
        /// country and the one before it the town, with any leading postcode trimmed off. Anything
        /// that does not look like that yields empty strings, and the caller keeps what it had —
        /// a wrong city is worse than the previous one.
        /// </summary>
        private static (string City, string Country) SplitLocality(string localisation)
        {
            if (string.IsNullOrWhiteSpace(localisation)) return ("", "");

            var parts = localisation
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length < 2) return ("", "");

            var country = parts[^1];
            var city = parts[^2];

            // "75011 Paris" → "Paris". A part that is nothing but digits is a postcode on its own,
            // in which case the town is the part before it.
            var words = city.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1 && words[0].All(char.IsDigit))
                city = string.Join(' ', words[1..]);
            else if (words.Length == 1 && words[0].All(char.IsDigit))
                city = parts.Length >= 3 ? parts[^3] : "";

            return (city.Trim(), country.Trim());
        }
    }

    /// <summary>Unified lookup shared by onboarding and settings.</summary>
    public static class LocationSearchService
    {
        public static async Task<List<LocationResult>> SearchAsync(
            string query, SearchMode mode = SearchMode.Mosques, CancellationToken token = default)
        {
            query = query.Trim();
            if (query.Length < 2) return new List<LocationResult>();

            return mode == SearchMode.Places
                ? await SearchPlacesAsync(query, token)
                : await SearchMosquesAsync(query, token);
        }

        /// <summary>
        /// Mawaqit mosques first — they are the only source with a real published timetable — then
        /// the built-in city list, which keeps working with no network at all.
        /// </summary>
        private static async Task<List<LocationResult>> SearchMosquesAsync(string query, CancellationToken token)
        {
            var results = new List<LocationResult>();
            var weak = new List<LocationResult>();

            try
            {
                var mosques = await MawaqitService.SearchMosquesAsync(query, token);
                token.ThrowIfCancellationRequested();

                // Mawaqit's search is loose — it answers "london" with mosques in Bandon, Leesburg
                // and Yogyakarta alongside the two actual London ones, in no useful order. Ranking
                // here is what puts the mosque somebody typed the name of at the top rather than
                // below the fold under places on other continents.
                var ranked = mosques
                    .Select(m => (Mosque: m, Score: Relevance(m, query)))
                    .OrderByDescending(x => x.Score)
                    .ToList();

                results.AddRange(ranked.Where(x => x.Score > 0).Take(6).Select(x => ToResult(x.Mosque)));
                weak.AddRange(ranked.Where(x => x.Score == 0).Take(5).Select(x => ToResult(x.Mosque)));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* offline: cities alone still let the user proceed */ }

            // Cities sit above the loose matches, never below them: when nothing matched properly,
            // "London, United Kingdom" is a far better offer than a mosque in the Comoros.
            results.AddRange(MatchingCities(query));

            // Only when nothing matched cleanly — a misspelling, or a mosque registered under a
            // name nobody would guess — is it worth showing what the search did turn up.
            if (results.Count == 0) results.AddRange(weak);

            return results;
        }

        private static async Task<List<LocationResult>> SearchPlacesAsync(string query, CancellationToken token)
        {
            var results = new List<LocationResult>();

            try
            {
                // Mosques the map knows, first: someone who reached this mode did so because their
                // mosque was not in Mawaqit, so an actual mosque is what they are looking for even
                // though its times will have to be calculated.
                Add(await GeocodingService.SearchMosquesAsync(query, token));
                token.ThrowIfCancellationRequested();

                // Only widen to everything when that turned up too little to choose from.
                if (results.Count < 3)
                {
                    Add(await GeocodingService.SearchPlacesAsync(query, token));
                    token.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* the built-in cities below are still a usable answer */ }

            // The local list last here — in this mode the user has already said the obvious
            // answers were not what they wanted.
            foreach (var city in MatchingCities(query))
                if (!Duplicate(city.Title)) results.Add(city);

            return results;

            void Add(List<GeocodedPlace> places)
            {
                foreach (var place in places)
                {
                    if (Duplicate(place.Name)) continue;

                    results.Add(new LocationResult
                    {
                        IsMosque = false,
                        Title = place.Name,
                        Subtitle = place.Context,
                        City = place.City.Length > 0 ? place.City : place.Name,
                        Country = place.Country,
                        Latitude = place.Latitude,
                        Longitude = place.Longitude
                    });
                }
            }

            bool Duplicate(string name) =>
                results.Any(r => string.Equals(r.Title, name, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<LocationResult> MatchingCities(string query) =>
            CityDatabase.Cities
                .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                // "Medina" before "Medan" for the query "med": a city the query starts is much
                // more likely to be the one meant than one it merely appears inside.
                .OrderByDescending(c => c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(c => c.Name.Length)
                .Take(4)
                .Select(c => new LocationResult
                {
                    IsMosque = false,
                    Title = c.Name,
                    Subtitle = c.Country,
                    City = c.Name,
                    Country = c.Country,
                    Latitude = c.Latitude,
                    Longitude = c.Longitude
                });

        private static LocationResult ToResult(MawaqitService.MawaqitMosque mosque) => new()
        {
            IsMosque = true,
            Title = mosque.Name,
            Subtitle = mosque.Localisation,
            MosqueUuid = mosque.Uuid,
            MosqueSlug = mosque.Slug,
            Latitude = mosque.Latitude,
            Longitude = mosque.Longitude
        };

        /// <summary>
        /// How well a mosque answers the query, scored on normalised text so spelling, case,
        /// accents and the Arabic definite article stop mattering. Every query word present in the
        /// name beats some of them, which beats a hit on the address alone.
        /// </summary>
        private static int Relevance(MawaqitService.MawaqitMosque mosque, string query)
        {
            var terms = TextMatching.Tokenize(query);
            if (terms.Count == 0) return 0;

            var name = TextMatching.Normalize(mosque.Name);
            var where = TextMatching.Normalize(mosque.Localisation);

            if (name == TextMatching.Normalize(query)) return 5;

            int inName = terms.Count(t => name.Contains(t, StringComparison.Ordinal));
            if (inName == terms.Count) return 4;
            if (inName > 0) return 3;

            int inPlace = terms.Count(t => where.Contains(t, StringComparison.Ordinal));
            if (inPlace == terms.Count) return 2;
            return inPlace > 0 ? 1 : 0;
        }
    }

    /// <summary>
    /// Text folding shared by the mosque search and its ranking, so a query is normalised exactly
    /// the same way on both sides of the comparison.
    /// </summary>
    public static class TextMatching
    {
        private static readonly char[] Separators = { ' ', '\t', '-', '_', '\'', '’', '.', ',' };

        /// <summary>
        /// Case-folded, accent-stripped, and with the Arabic script's interchangeable forms
        /// collapsed: hamza carriers to bare alif, ta marbuta to ha, alif maqsura to ya, and the
        /// leading "ال" dropped. Without this "Mosquée" never matches "mosquee", and جامع الرحمة
        /// never matches رحمة.
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var folded = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(folded.Length);

            foreach (var ch in folded)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;

                builder.Append(ch switch
                {
                    'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                    'ة' => 'ه',
                    'ى' => 'ي',
                    'ؤ' => 'و',
                    'ئ' => 'ي',
                    _ => ch
                });
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>Normalised words worth matching on, with the Arabic article and noise dropped.</summary>
        public static List<string> Tokenize(string text)
        {
            var words = Normalize(text).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            var terms = new List<string>();

            foreach (var word in words)
            {
                var term = word.StartsWith("ال", StringComparison.Ordinal) && word.Length > 3
                    ? word[2..]
                    : word;

                if (term.Length >= 2 && !terms.Contains(term)) terms.Add(term);
            }

            return terms;
        }
    }
}
