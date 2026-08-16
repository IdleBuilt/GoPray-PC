using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using GoPray.Models;

namespace GoPray.Services
{
    /// <summary>
    /// Where the displayed times came from.
    /// <list type="bullet">
    /// <item><c>Loading</c> — nothing yet; the first fetch is still in flight.</item>
    /// <item><c>Live</c> — fetched from a provider this session, and it covers today.</item>
    /// <item><c>Cached</c> — a stored timetable covers today, but the last fetch did not succeed.
    /// The times are still the provider's own, just not re-checked.</item>
    /// <item><c>Unavailable</c> — no provider reachable and nothing stored reaches today. The app
    /// shows no times at all rather than a guess.</item>
    /// </list>
    /// </summary>
    public enum DataStatus { Loading, Live, Cached, Unavailable }

    /// <summary>A point-in-time view of "what is next", recomputed once a second.</summary>
    public sealed class PrayerSnapshot
    {
        public string NextName { get; init; } = "--";
        public DateTime NextAt { get; init; }
        public string NextTimeRaw { get; init; } = "--:--";
        public TimeSpan Remaining { get; init; }
        public bool NextIsTomorrow { get; init; }
        /// <summary>0..1 progress from the previous prayer to the next one.</summary>
        public double Progress { get; init; }
        public bool HasData { get; init; }
    }

    /// <summary>
    /// The app's single source of truth for prayer data. Owns the only ticking timer, all
    /// fetching/caching, next-prayer maths and prayer-due dispatch. Every window subscribes;
    /// no window fetches or schedules on its own.
    /// </summary>
    public sealed class PrayerService
    {
        public static PrayerService Instance { get; } = new();

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);
        /// <summary>How late a prayer may be detected; also bounds duplicate suppression.</summary>
        private static readonly TimeSpan DueWindow = TimeSpan.FromSeconds(60);

        /// <summary>Countdown resolution, used only while a window is actually on screen.</summary>
        private static readonly TimeSpan VisibleTick = TimeSpan.FromSeconds(1);
        /// <summary>
        /// Tick rate with nothing on screen. Only prayer-due detection still needs to run, and that
        /// tolerates any interval below <see cref="DueWindow"/> — so the UI thread is woken roughly
        /// 4,300 times a day instead of 86,400 for countdowns nobody is looking at.
        /// </summary>
        private static readonly TimeSpan IdleTick = TimeSpan.FromSeconds(20);

        /// <summary>
        /// How long before a prayer the reminder opens, so the overlay can count the last seconds
        /// down instead of appearing on top of the adhan.
        /// </summary>
        public static readonly TimeSpan LeadIn = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Within this of the next prayer the tick runs at full resolution no matter what is on
        /// screen. Without it the idle rate would step straight over the five-second lead-in.
        /// </summary>
        private static readonly TimeSpan PrecisionWindow = TimeSpan.FromSeconds(90);

        private readonly DispatcherTimer _timer = new() { Interval = VisibleTick };
        private readonly HashSet<string> _fired = new();
        private readonly HashSet<string> _announced = new();
        private readonly SemaphoreSlim _fetchGate = new(1, 1);

        private DateTime _today = DateTime.MinValue;
        private DateTime _nextRefreshAt = DateTime.MinValue;
        private bool _started;
        private bool _displayActive;

        private PrayerService() { }

        public AppSettings Settings { get; private set; } = new();
        public GoPrayTimetable? Timetable { get; private set; }
        public DataStatus Status { get; private set; } = DataStatus.Loading;
        public PrayerSnapshot Snapshot { get; private set; } = new();

        /// <summary>Today's times, or null when nothing on hand reaches today.</summary>
        public GoPrayData? Today => Timetable?.Today;

        /// <summary>Data or status changed — rebuild lists and static labels.</summary>
        public event Action? Changed;
        /// <summary>Fired every tick with a fresh snapshot — update countdowns only.</summary>
        public event Action<PrayerSnapshot>? Ticked;
        /// <summary>A prayer time just arrived (at most once per prayer per day).</summary>
        public event Action<string, string>? PrayerDue;
        /// <summary><see cref="LeadIn"/> before a prayer, so the reminder can open and count down.
        /// Fires at most once per prayer per day, and may be missed if the machine was asleep —
        /// <see cref="PrayerDue"/> is always the authoritative one.</summary>
        public event Action<string, string>? PrayerApproaching;

        public void Start()
        {
            if (_started) return;
            _started = true;

            _today = DateTime.Today;
            Settings = SettingsService.Load();

            var cached = SettingsService.LoadCachedTimes();
            if (cached != null && cached.LocationKey == Settings.LocationKey() && cached.CoversToday)
            {
                Timetable = cached;
                Status = DataStatus.Cached;
            }
            else
            {
                Status = DataStatus.Loading;
            }

            _timer.Tick += OnTick;
            _timer.Start();

            Recompute();
            Changed?.Invoke();
            _ = RefreshAsync();
        }

        /// <summary>
        /// Switches the tick between countdown resolution and the idle rate. Called as windows are
        /// shown and hidden: a per-second timer only earns its cost while a countdown is visible.
        /// </summary>
        public void SetDisplayActive(bool active)
        {
            if (_displayActive == active) return;
            _displayActive = active;

            ApplyTickInterval();

            // A freshly shown window gets its first countdown frame right away rather than
            // waiting out the interval.
            if (active) { Recompute(); Ticked?.Invoke(Snapshot); }
        }

        /// <summary>
        /// Full resolution while a countdown is visible, or while a prayer is close enough that the
        /// lead-in and due detection need the precision; the idle rate otherwise.
        /// </summary>
        private void ApplyTickInterval()
        {
            bool precise = _displayActive
                || (Snapshot.HasData && Snapshot.Remaining > TimeSpan.Zero
                    && Snapshot.Remaining <= PrecisionWindow);

            var interval = precise ? VisibleTick : IdleTick;

            // Assigning Interval restarts the timer, so only touch it on a real change.
            if (_timer.Interval != interval) _timer.Interval = interval;
        }

        /// <summary>
        /// Background refresh: start-up, the periodic timer, a source change. Dropped outright if a
        /// fetch is already running, because another automatic one is always due shortly anyway.
        /// </summary>
        public Task RefreshAsync() => FetchAsync(force: false);

        /// <summary>
        /// A refresh the user actually asked for (the tray menu, the widget menu, the Refresh
        /// button). Queues behind an in-flight fetch instead of being dropped on the floor: with
        /// <see cref="RefreshAsync"/>'s zero-timeout gate, clicking Refresh while the six-hourly
        /// fetch happened to be running did nothing at all, with nothing on screen to say so.
        /// </summary>
        public Task ForceRefreshAsync() => FetchAsync(force: true);

        /// <summary>Persist and adopt edited settings, refetching only when the source actually changed.</summary>
        public void ApplySettings(AppSettings updated)
        {
            bool sourceChanged = updated.LocationKey() != Settings.LocationKey();
            Settings = updated;
            SettingsService.Save(updated);

            if (sourceChanged)
            {
                Timetable = null;
                Status = DataStatus.Loading;
                ResetAlerts();
                Recompute();
                Changed?.Invoke();
                _ = RefreshAsync();
            }
            else
            {
                Recompute();
                Changed?.Invoke();
            }
        }

        private async Task FetchAsync(bool force)
        {
            if (force) await _fetchGate.WaitAsync();
            else if (!await _fetchGate.WaitAsync(0)) return;

            try
            {
                var settings = Settings;
                var key = settings.LocationKey();

                var fresh = await Task.Run(() => PrayerApiService.FetchTimetableAsync(settings));

                // The user may have changed location while this request was in flight.
                if (key != Settings.LocationKey()) return;

                if (fresh == null || !fresh.CoversToday)
                {
                    // Nothing reachable. Keep whatever is cached if it still covers today; that is
                    // the whole point of pulling a month at a time.
                    Status = Timetable?.CoversToday == true ? DataStatus.Cached : DataStatus.Unavailable;
                    ScheduleRetry();
                    Recompute();
                    Changed?.Invoke();
                    return;
                }

                fresh.FetchedAt = DateTime.Now;
                fresh.LocationKey = key;
                fresh.Trim();

                Timetable = fresh;
                _today = DateTime.Today;

                // Deliberately NOT clearing _fired here. Its keys are date-scoped, so a refresh
                // can never resurrect yesterday's alerts — but clearing would re-arm today's.
                // Any refresh landing while a prayer's DueWindow is still open re-adds that
                // prayer's key and fires its adhan a second time; the two-minute retry made that
                // easy to hit.

                Status = DataStatus.Live;
                SettingsService.CacheTimes(fresh);
                _nextRefreshAt = DateTime.Now + RefreshInterval;

                Recompute();
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                Status = Timetable?.CoversToday == true ? DataStatus.Cached : DataStatus.Unavailable;
                ScheduleRetry();
                Recompute();
                Changed?.Invoke();
            }
            finally { _fetchGate.Release(); }
        }

        private void ScheduleRetry() => _nextRefreshAt = DateTime.Now + RetryInterval;

        private void OnTick(object? sender, EventArgs e)
        {
            // Day rolled over (or the machine woke on a new day). A month-deep timetable already
            // holds tomorrow, so this is usually just a matter of re-arming the alerts — but the
            // times on screen have to be recomputed against the new day either way.
            if (_today != DateTime.Today)
            {
                _today = DateTime.Today;
                ResetAlerts();
                Timetable?.Trim();

                // A one-day Mawaqit timetable has just run out; anything deeper simply advances.
                if (Timetable?.CoversToday != true)
                {
                    Status = DataStatus.Unavailable;
                    _nextRefreshAt = DateTime.MinValue;
                }

                Recompute();
                Changed?.Invoke();
            }

            if (DateTime.Now >= _nextRefreshAt)
            {
                _nextRefreshAt = DateTime.Now + RetryInterval;
                _ = RefreshAsync();
            }

            Recompute();
            Ticked?.Invoke(Snapshot);
            DispatchDuePrayers();
            ApplyTickInterval();
        }

        /// <summary>Re-arms every prayer for the day. The two sets are always cleared together —
        /// an announced-but-never-fired prayer would keep its lead-in suppressed forever.</summary>
        private void ResetAlerts()
        {
            _fired.Clear();
            _announced.Clear();
        }

        private void DispatchDuePrayers()
        {
            var today = Today;
            if (today == null) return;

            var now = DateTime.Now;

            foreach (var (name, time) in today.GetAllForDay())
            {
                if (name == "Sunrise") continue;
                if (!TryParse(time, DateTime.Today, out var at)) continue;

                var key = $"{now:yyyyMMdd}|{name}";
                var until = at - now;

                // Lead-in first: the reminder opens a few seconds early and counts the last
                // seconds down, so the adhan starts against an overlay that is already settled.
                if (until > TimeSpan.Zero && until <= LeadIn && _announced.Add(key))
                    PrayerApproaching?.Invoke(name, time);

                var late = now - at;
                if (late < TimeSpan.Zero || late >= DueWindow) continue;

                if (!_fired.Add(key)) continue;
                PrayerDue?.Invoke(name, time);
            }
        }

        /// <summary>Alertable prayers for a day, in order. Sunrise is displayed but never counted down to.</summary>
        private static List<(string Name, string Raw, DateTime At)> ScheduleFor(GoPrayData? day, DateTime date)
        {
            var schedule = new List<(string Name, string Raw, DateTime At)>();
            if (day == null) return schedule;

            foreach (var (name, time) in day.GetAllForDay())
            {
                if (name == "Sunrise") continue;
                if (TryParse(time, date.Date, out var at)) schedule.Add((name, time, at));
            }

            schedule.Sort((a, b) => a.At.CompareTo(b.At));
            return schedule;
        }

        private void Recompute()
        {
            var now = DateTime.Now;
            var today = DateTime.Today;

            var schedule = ScheduleFor(Today, today);
            if (schedule.Count == 0)
            {
                Snapshot = new PrayerSnapshot();
                return;
            }

            (string Name, string Raw, DateTime At) next;
            bool tomorrow = false;
            int upcoming = schedule.FindIndex(p => p.At > now);

            if (upcoming >= 0)
            {
                next = schedule[upcoming];
            }
            else
            {
                // Past the last prayer of the day: the next one is tomorrow's first. Use tomorrow's
                // real times when the timetable reaches that far — repeating today's Fajr under a
                // "tomorrow" label was wrong by a minute or two every single night.
                var ahead = ScheduleFor(Timetable?.For(today.AddDays(1)), today.AddDays(1));
                if (ahead.Count > 0)
                {
                    next = ahead[0];
                }
                else
                {
                    var first = schedule[0];
                    next = (first.Name, first.Raw, first.At.AddDays(1));
                }
                tomorrow = true;
            }

            // Previous occurrence, wrapping to yesterday's last prayer before Fajr.
            DateTime previous = upcoming switch
            {
                < 0 => schedule[^1].At,
                0 => schedule[^1].At.AddDays(-1),
                _ => schedule[upcoming - 1].At
            };

            var span = (next.At - previous).TotalSeconds;
            var done = (now - previous).TotalSeconds;

            Snapshot = new PrayerSnapshot
            {
                HasData = true,
                NextName = next.Name,
                NextAt = next.At,
                NextTimeRaw = next.Raw,
                NextIsTomorrow = tomorrow,
                Remaining = next.At - now,
                Progress = span > 0 ? Math.Clamp(done / span, 0, 1) : 0
            };
        }

        /// <summary>
        /// Provider times are machine-format "HH:mm", so they are parsed with the invariant
        /// culture. <see cref="TimeSpan.TryParse(string, out TimeSpan)"/> without one follows the
        /// current culture, which is how the same timetable parsed on a machine with a non-colon
        /// time separator produced no prayers at all.
        /// </summary>
        private static bool TryParse(string time, DateTime day, out DateTime result)
        {
            result = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(time) || time == "--:--") return false;
            if (!TimeSpan.TryParse(time.Trim(), CultureInfo.InvariantCulture, out var ts)) return false;
            if (ts < TimeSpan.Zero || ts >= TimeSpan.FromDays(1)) return false;
            result = day.Add(ts);
            return true;
        }

        /// <summary>
        /// Names the configured source in the user's own terms — the mosque if they picked one,
        /// otherwise the service generically — so a failure message points at something they
        /// recognise rather than at an API name they never chose.
        /// </summary>
        public static string ProviderLabel()
        {
            var settings = Instance.Settings;
            return settings.Provider == ApiProvider.Mawaqit && !string.IsNullOrEmpty(settings.MawaqitMosqueName)
                ? settings.MawaqitMosqueName
                : Localization.T("S_TheService");
        }

        /// <summary>Whether a listed time has already passed today (Sunrise included).</summary>
        public static bool IsPast(string time, DateTime now)
            => TryParse(time, now.Date, out var t) && t <= now;

        /// <summary>Below this the countdown is shown in the critical colour.</summary>
        private static readonly TimeSpan ImminentThreshold = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Wraps a clock value in a Unicode left-to-right isolate so it renders as a single
        /// left-to-right run wherever it lands. Without this, "12:29 PM" dropped into Arabic text
        /// comes out as "PM 12:29" — the bidi algorithm is treating the whole line as one
        /// right-to-left paragraph, which is correct for the words and wrong for the clock. An
        /// isolate fences the number off without affecting anything around it, and the two
        /// characters are invisible in a left-to-right UI.
        /// </summary>
        private static string Ltr(string value) => $"⁦{value}⁩";

        /// <summary>
        /// Counting down, so always signed: "-1:23:45" over an hour out, "-23:45" under.
        /// Never runs past zero.
        /// </summary>
        public static string FormatCountdown(TimeSpan remaining)
        {
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            int total = (int)remaining.TotalSeconds;
            int h = total / 3600, m = total % 3600 / 60, s = total % 60;
            return Ltr(h > 0 ? $"-{h}:{m:D2}:{s:D2}" : $"-{m:D2}:{s:D2}");
        }

        public static bool IsImminent(TimeSpan remaining) => remaining < ImminentThreshold;

        /// <summary>Largest shift the Hijri offset setting allows, either way.</summary>
        public const int MaxHijriOffset = 2;

        /// <summary>
        /// Today in the Hijri calendar, e.g. "12 Rajab 1448 AH". Shown instead of the Gregorian
        /// date, which Windows already puts in the taskbar.
        ///
        /// <para>Umm al-Qura is arithmetic; local moon sighting routinely lands a day either side
        /// of it, so the user's <see cref="AppSettings.HijriOffset"/> shifts the <i>date</i> before
        /// conversion rather than the day number after it. Adjusting the number afterwards would
        /// print "0 Ramadan" or "31 Shawwal" at a month boundary.</para>
        /// </summary>
        public static string HijriToday()
        {
            int offset = Math.Clamp(Instance.Settings.HijriOffset, -MaxHijriOffset, MaxHijriOffset);
            return HijriFor(DateTime.Today, offset);
        }

        internal static string HijriFor(DateTime date, int offsetDays)
        {
            try
            {
                var calendar = new UmAlQuraCalendar();
                var shifted = date.AddDays(offsetDays);

                // MinSupportedDateTime/MaxSupportedDateTime rather than a bare try/catch: the
                // offset can push an otherwise fine date over the edge of the calendar's range.
                if (shifted < calendar.MinSupportedDateTime || shifted > calendar.MaxSupportedDateTime)
                    return "";

                int day = calendar.GetDayOfMonth(shifted);
                int month = calendar.GetMonth(shifted);
                int year = calendar.GetYear(shifted);

                return Localization.HijriDate(day, month, year);
            }
            catch
            {
                // Outside the calendar's supported range; the header simply stays empty.
                return "";
            }
        }

        /// <summary>Renders a raw "HH:mm" provider time in the user's chosen clock format.</summary>
        public static string FormatClock(string raw, bool is24Hour)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "--:--") return "--:--";
            if (!TimeSpan.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out var ts)) return raw;
            if (is24Hour) return Ltr($"{ts.Hours:D2}:{ts.Minutes:D2}");

            int h = ts.Hours % 12;
            if (h == 0) h = 12;

            var marker = Localization.T(ts.Hours >= 12 ? "S_PM" : "S_AM");
            return Ltr($"{h}:{ts.Minutes:D2} {marker}");
        }
    }
}
