using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace GoPray.Services
{
    /// <param name="Version">The released version, or null when nothing could be determined.</param>
    /// <param name="IsNewer">True only when it is genuinely ahead of this build.</param>
    /// <param name="Url">Where to send the user; the release page, never a direct binary.</param>
    public sealed record UpdateCheck(Version? Version, bool IsNewer, string Url)
    {
        /// <summary>The check itself did not work — worth saying so.</summary>
        public static readonly UpdateCheck Failed = new(null, false, AppInfo.ProjectUrl);
    }

    /// <summary>
    /// Looks for a newer release on GitHub. Read-only and unauthenticated — GoPray never downloads
    /// or installs anything by itself; it points at the release page and the user decides.
    /// </summary>
    public static class UpdateService
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // GitHub rejects requests without a User-Agent outright.
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("GoPray", AppInfo.Version));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            return client;
        }

        /// <summary>The check worked and there is simply no release to compare against.</summary>
        private static readonly UpdateCheck Nothing = new(null, false, AppInfo.ProjectUrl);

        public static async Task<UpdateCheck> CheckAsync()
        {
            try
            {
                var url = $"https://api.github.com/repos/{AppInfo.Owner}/{AppInfo.Repository}/releases/latest";

                using var response = await Http.GetAsync(url);

                // A repository with no published release answers 404. That is not a failure — it
                // is simply nothing to report, and telling the user the check broke would be a
                // lie they can do nothing about.
                if (response.StatusCode == HttpStatusCode.NotFound) return Nothing;
                if (!response.IsSuccessStatusCode) return UpdateCheck.Failed;

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = doc.RootElement;

                var latest = AppInfo.Parse(Read(root, "tag_name"));
                if (latest == null) return Nothing;

                var page = Read(root, "html_url");
                if (page.Length == 0) page = AppInfo.ProjectUrl;

                return new UpdateCheck(latest, latest > AppInfo.Current, page);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return UpdateCheck.Failed;
            }
        }

        private static string Read(JsonElement element, string property)
            => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
    }
}
