namespace GoPray.Services
{
    /// <summary>Shared by every provider: strips a timezone suffix such as "05:12 (CET)" down to the clock part.</summary>
    public static class TimeCleaning
    {
        public static string Clean(string time)
            => string.IsNullOrWhiteSpace(time) ? "--:--" : time.Trim().Split(' ')[0];
    }
}
