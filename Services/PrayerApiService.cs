using System;
using System.Threading.Tasks;
using GoPray.Models;

namespace GoPray.Services
{
    /// <summary>
    /// Resolves prayer times for the configured source: Mawaqit first when a mosque is configured,
    /// then Aladhan.
    ///
    /// <para>There is deliberately no on-device fallback. The old one calculated times from a
    /// hardcoded 69-city coordinate table and, for any city outside it, silently fell back to
    /// Sousse — so a user in Birmingham with both providers unreachable was shown Tunisian times
    /// labelled as their own. Confidently wrong beats nothing only if it is actually right.
    /// Everything the app displays now came from a provider, and when nothing did, it says so.</para>
    /// </summary>
    public static class PrayerApiService
    {
        /// <summary>Returns null when no provider could be reached; the caller keeps its cache.</summary>
        public static async Task<GoPrayTimetable?> FetchTimetableAsync(AppSettings settings)
        {
            if (settings.Provider == ApiProvider.Mawaqit && !string.IsNullOrEmpty(settings.MawaqitMosqueUuid))
            {
                try
                {
                    return await MawaqitService.FetchTimetableAsync(
                        settings.MawaqitMosqueUuid, settings.MawaqitMosqueName, settings.MawaqitMosqueSlug);
                }
                catch (Exception ex) { App.LogError(ex); }
            }

            try
            {
                return await AladhanService.FetchTimetableAsync(
                    settings.City, settings.Country, settings.Method,
                    settings.Latitude, settings.Longitude);
            }
            catch (Exception ex) { App.LogError(ex); }

            return null;
        }
    }
}
