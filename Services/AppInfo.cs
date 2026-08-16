using System;
using System.Reflection;

namespace GoPray.Services
{
    /// <summary>
    /// Identity of this build, in one place. The version is read from the assembly rather than
    /// written down, so it cannot drift from what the installer actually shipped.
    /// </summary>
    public static class AppInfo
    {
        public const string Owner = "IdleBuilt";
        public const string Repository = "GoPray-PC";
        public const string ProjectUrl = $"https://github.com/{Owner}/{Repository}";

        /// <summary>Three-part version, e.g. "0.9.2". Revision is never meaningful here.</summary>
        public static string Version { get; } = Read();

        private static string Read()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        /// <summary>Parses a version that may carry a "v" prefix, as GitHub tags usually do.</summary>
        public static Version? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var trimmed = text.Trim().TrimStart('v', 'V');

            // Anything after a pre-release marker is not part of the number.
            int cut = trimmed.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) trimmed = trimmed[..cut];

            return System.Version.TryParse(trimmed, out var parsed) ? Normalize(parsed) : null;
        }

        /// <summary>
        /// Comparable three-part version. System.Version treats an unspecified component as -1,
        /// so "0.9" and "0.9.0" compare unequal unless they are levelled first.
        /// </summary>
        private static Version Normalize(Version v)
            => new(v.Major, v.Minor, Math.Max(v.Build, 0));

        public static Version Current => Parse(Version) ?? new Version(0, 0, 0);
    }
}
