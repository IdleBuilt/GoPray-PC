using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GoPray.Models;
using GoPray.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace GoPray
{
    /// <summary>Row model for the day's timetable; theme brushes are resolved once per rebuild.</summary>
    public sealed class PrayerRowView
    {
        public string Name { get; init; } = "";
        public string Time { get; init; } = "";
        /// <summary>Countdown shown against the next prayer only.</summary>
        public string Note { get; init; } = "";
        /// <summary>When the congregation starts, if the mosque publishes it and it is switched on.</summary>
        public string Iqamah { get; init; } = "";
        /// <summary>Tracks the row: greyed out once the prayer has passed, accented while it is next.</summary>
        public Brush IqamahForeground { get; init; } = Brushes.Gray;
        public Brush RowBackground { get; init; } = Brushes.Transparent;
        public Brush Foreground { get; init; } = Brushes.Gray;
        public FontWeight Weight { get; init; } = FontWeights.Normal;
        public Visibility MarkerVisibility { get; init; } = Visibility.Collapsed;
        public Visibility NoteVisibility =>
            string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility IqamahVisibility =>
            string.IsNullOrEmpty(Iqamah) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Presentation wrapper around a <see cref="LocationResult"/>.</summary>
    public sealed class LocationResultRow
    {
        public LocationResultRow(LocationResult result) => Result = result;

        public LocationResult Result { get; }
        public string Title => Result.Title;
        public string Subtitle => Result.Subtitle;
        public Visibility MosqueIconVisibility => Result.IsMosque ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CityIconVisibility => Result.IsMosque ? Visibility.Collapsed : Visibility.Visible;
        public Visibility SubtitleVisibility =>
            string.IsNullOrWhiteSpace(Result.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>What a screen reader announces for the row. Without this it reads the type
        /// name — every result in the list came out as "GoPray.LocationResultRow".</summary>
        public override string ToString() =>
            string.IsNullOrWhiteSpace(Subtitle) ? Title : $"{Title}, {Subtitle}";
    }

    /// <summary>
    /// The whole of GoPray outside the adhan overlay, as one persistent window with two states.
    ///
    /// <para><b>Compact</b> is the always-available card: next prayer, countdown, progress. It is
    /// never activated (so it can never steal keyboard focus) and stays on screen until explicitly
    /// hidden (tray/menu); pinning additionally asserts true always-on-top on a timer.</para>
    ///
    /// <para><b>Expanded</b> is the same window grown into today's timetable, settings and the
    /// first-run picker — a normal, activatable, typeable window while it is showing.</para>
    ///
    /// <para>This window is constructed once and lives for the app's whole run: "hiding" the
    /// compact card calls <see cref="Window.Hide"/>, never <see cref="Window.Close"/>. The old
    /// design recreated a WidgetWindow on every show/hide, which left a window mid-teardown while
    /// a new one was already being built if a second show request landed inside the fade-out —
    /// visible as a flickering double widget. A single instance that is never destroyed removes
    /// that race outright rather than guarding around it.</para>
    /// </summary>
    public partial class MainWindow : FluentWindow
    {
        private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(350);

        private readonly DispatcherTimer _positionSave = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = SearchDebounce };
        private readonly DispatcherTimer _settingsCommitDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

        private CancellationTokenSource? _searchCts;
        private TextBox? _pendingSearchSource;
        private LocationResult? _onboardingChoice;
        private SearchMode _searchMode = SearchMode.Mosques;
        private bool _capturingHotkey;
        private string _listSignature = "";
        private bool _suppressSettingEvents;
        private bool _appExiting;
        private bool _subscribed;

        public MainWindow()
        {
            InitializeComponent();

            _positionSave.Tick += (_, _) => { _positionSave.Stop(); SavePosition(); };
            _searchDebounceTimer.Tick += async (_, _) => { _searchDebounceTimer.Stop(); await RunSearchAsync(); };
            _settingsCommitDebounce.Tick += (_, _) => CommitSettings();

            Loaded += OnLoaded;
            Closing += OnClosing;
            LocationChanged += OnLocationChanged;

            // Switching language re-keys everything bound with DynamicResource on its own; only
            // the strings this file builds in code have to be redrawn by hand.
            Localization.Changed += OnLanguageChanged;

            // Tunnelling, so the shortcut capture sees the keystroke before the focused control
            // turns it into a click, a caret move or a combo-box jump.
            PreviewKeyDown += OnPreviewKeyDownForHotkey;
        }

        private void OnLanguageChanged()
        {
            _listSignature = "";
            BuildPrayerAlertRows();
            OnDataChanged();
            RebuildList(PrayerService.Instance.Snapshot);
        }

        // ── Lifetime ───────────────────────────────────────────────────────────

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppearanceService.Register(this);

            // Loaded is raised once for a window that is only ever hidden and reshown, which is
            // this one — but subscribing twice would double every tick, so it costs nothing to
            // make that guarantee local rather than inherited from WPF's lifecycle.
            if (!_subscribed)
            {
                _subscribed = true;
                ApplicationThemeManager.Changed += OnThemeChanged;
                PrayerService.Instance.Changed += OnDataChanged;
                PrayerService.Instance.Ticked += OnTick;
            }

            OnDataChanged();
            RebuildList(PrayerService.Instance.Snapshot);
        }

        /// <summary>
        /// First run. Everything is arranged before <see cref="Window.Show"/> so the picker is
        /// centred and correctly sized in its first frame, instead of flashing up as a stray
        /// widget-sized card in a corner and then jumping.
        /// </summary>
        public void ShowOnboarding()
        {
            Navigate(MainView.Onboarding);
            ShowAt(MainView.Onboarding);
            FocusOnboardingSearch();
        }

        /// <summary>
        /// ShowActivated="False" and WS_EX_NOACTIVATE both exist for the widget's sake — neither
        /// may apply while someone is typing a mosque name, so this undoes them for the picker and
        /// then puts the caret where they are about to type.
        /// </summary>
        private void FocusOnboardingSearch()
        {
            WindowInterop.SetActivatable(this, true);
            Activate();
            WindowInterop.Foreground(this);

            // After the activation above has actually landed, or the box takes logical focus
            // while the keyboard is still pointed at whatever was in front.
            Dispatcher.BeginInvoke(() =>
            {
                OnboardingSearchBox.Focus();
                Keyboard.Focus(OnboardingSearchBox);
            }, DispatcherPriority.Input);
        }

        /// <summary>
        /// The title bar's close button (and Alt+F4) never really close this window — only
        /// <see cref="App.Quit"/> does, via <see cref="PrepareForShutdown"/>. Any other close
        /// request collapses back to the compact widget instead, except during onboarding, where
        /// there is no tray or widget yet to collapse to, so it quits the app.
        /// </summary>
        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_appExiting) return;

            e.Cancel = true;

            // During onboarding there is no widget to fall back to, so closing means quitting.
            if (!PrayerService.Instance.Settings.OnboardingComplete) { App.Host.Quit(); return; }

            Dismiss();
        }

        /// <summary>Lets <see cref="Application.Shutdown"/> actually close this window instead of
        /// bouncing back to Compact, so quitting the app does not get silently cancelled.</summary>
        public void PrepareForShutdown()
        {
            _appExiting = true;

            // The position save is debounced by half a second; quitting inside that window used to
            // drop a move the user had just made.
            _positionSave.Stop();
            _settingsCommitDebounce.Stop();

            // Nothing to flush on the way out of a "clear all data" quit — the whole point there
            // is that the wiped folder stays wiped.
            if (SettingsService.Suspended) return;

            SavePosition();
        }

        private void OnThemeChanged(ApplicationTheme theme, Color accent)
        {
            AppearanceService.Register(this);
            UpdateDiscordQuickToggle();

            // Everything below is painted with brushes resolved once and cached on the element, so
            // a theme swap leaves them pointing at the old dictionary's objects until something
            // rebuilds. Nothing here re-reads a DynamicResource on its own.
            SupportGlyph.Fill = Ui.Theme(this, PrayerService.Instance.Settings.IsSupporter
                ? "AccentTextFillColorPrimaryBrush"
                : "TextFillColorTertiaryBrush");

            _listSignature = "";
            RebuildList(PrayerService.Instance.Snapshot);
        }

        // ── Compact: show / hide ───────────────────────────────────────────────

        // ── Expanded: show / hide ──────────────────────────────────────────────

        /// <summary>Brings the window up on the given page. The widget is a separate window and
        /// is deliberately left exactly as it was.</summary>
        public void ShowAt(MainView view)
        {
            bool wasHidden = !IsVisible;

            if (wasHidden)
            {
                PlaceWindow();
                if (PrayerService.Instance.Settings.OnboardingComplete) LoadSettingsControls();
            }

            Navigate(view);

            if (wasHidden)
            {
                Show();
                PlayEntry();
            }

            Activate();
            WindowInterop.Foreground(this);
        }

        /// <summary>Closes the panel back down. Hide, never Close — this window is built once.</summary>
        public void Dismiss()
        {
            if (!IsVisible || _dismissing) return;
            _dismissing = true;
            SavePosition();

            if (!PrayerService.Instance.Settings.AnimationsEnabled) { FinishDismiss(); return; }

            bool finished = false;
            void Finish() { if (finished) return; finished = true; FinishDismiss(); }

            var fade = new DoubleAnimation(ExpandedRoot.Opacity, 0, TimeSpan.FromMilliseconds(170));
            fade.Completed += (_, _) => Finish();

            ExpandedShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, 14, TimeSpan.FromMilliseconds(170))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            ExpandedRoot.BeginAnimation(OpacityProperty, fade);

            // Guarantees _dismissing always clears, so a dropped Completed callback can never
            // permanently wedge the open/close toggle.
            Ui.RunAfter(TimeSpan.FromMilliseconds(400), Finish);
        }

        private void FinishDismiss()
        {
            _dismissing = false;
            Hide();
        }

        private bool _dismissing;

        private void PlayEntry()
        {
            if (!PrayerService.Instance.Settings.AnimationsEnabled)
            {
                ExpandedRoot.Opacity = 1;
                ExpandedShift.Y = 0;
                return;
            }

            ExpandedRoot.Opacity = 0;
            ExpandedRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            ExpandedShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(300))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // ── Mode transitions: low-level visual/window state ───────────────────

        // ── Pin ────────────────────────────────────────────────────────────────

        // ── Position ───────────────────────────────────────────────────────────

        /// <summary>Anchored above the widget by default so expanding reads as one motion. Once the
        /// user turns that off, the window simply reopens wherever they last dragged it.</summary>
        private void PlaceWindow()
        {
            var settings = PrayerService.Instance.Settings;

            double width = Width;
            double height = Height;

            // The monitor the widget is on, not whichever one Windows calls primary. Clamping to
            // SystemParameters.WorkArea used to yank the expanded window across to the main display
            // every time it was opened from a widget parked on a second screen.
            var card = App.Host.WidgetBounds;

            var work = WindowInterop.WorkAreaAround(this,
                Ui.IsPlaced(card) ? card : new Rect(Left, Top, width, height));

            // On first launch the widget has never been shown, so there is nothing to anchor
            // above. Centre on screen instead.
            if (!Ui.IsPlaced(card))
            {
                Left = work.Left + (work.Width - width) / 2;
                Top = work.Top + (work.Height - height) / 2;
            }
            else if (!settings.MainFollowsWidget && settings.MainLeft is { } savedLeft && settings.MainTop is { } savedTop)
            {
                Left = savedLeft;
                Top = savedTop;
            }
            else
            {
                Left = card.Left + card.Width / 2 - width / 2;
                Top = card.Top - height - 10;

                // Not enough headroom above the widget: drop below it instead.
                if (Top < work.Top) Top = Math.Min(card.Bottom + 10, work.Bottom - height);
            }

            Left = Ui.Clamp(Left, work.Left + 4, Math.Max(work.Left + 4, work.Right - width - 4));
            Top = Ui.Clamp(Top, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - height - 4));
        }

        private void SavePosition()
        {
            var settings = PrayerService.Instance.Settings;
            if (settings.MainFollowsWidget) return;
            if (settings.MainLeft == Left && settings.MainTop == Top) return;

            settings.MainLeft = Left;
            settings.MainTop = Top;
            SettingsService.Save(settings);
        }

        private void OnLocationChanged(object? sender, EventArgs e)
        {
            if (!IsLoaded || _dismissing) return;
            _positionSave.Start();
        }

        /// <summary>Only meaningful for Expanded: a DPI change under a fixed-size window can leave
        /// it mis-anchored relative to the widget.</summary>
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.HeightChanged && IsLoaded && IsVisible
                && PrayerService.Instance.Settings.MainFollowsWidget)
            {
                PlaceWindow();
            }
        }

        // ── Interaction ────────────────────────────────────────────────────────

        private void ExpandedHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); }
            catch { /* mouse released before the drag loop started */ }
        }

        /// <summary>Routes through the real Close(), so OnClosing's existing collapse-vs-quit
        /// logic handles it exactly as it would the title bar's own close button.</summary>
        private void ExpandedClose_Click(object sender, RoutedEventArgs e) => Close();

        // ── Data (both compact and expanded read from the same tick/changed events) ────

        private void OnDataChanged()
        {
            var service = PrayerService.Instance;

            LocationText.Text = service.Settings.LocationLabel();
            HijriText.Text = PrayerService.HijriToday();

            // Only a genuine absence of times warrants a banner. "Cached" means the provider's own
            // times for today are on screen and simply were not re-checked — nothing is wrong, and
            // saying so would train people to ignore the bar for when it matters.
            bool unavailable = service.Status == DataStatus.Unavailable;
            StatusBar.IsOpen = unavailable;
            if (unavailable)
            {
                StatusBar.Title = Localization.T("S_NoTimesTitle");
                StatusBar.Message = Localization.T("S_NoTimesMessage", PrayerService.ProviderLabel());
            }

            UpdateDiscordQuickToggle();

            _suppressSettingEvents = true;
            PinToggle.IsChecked = service.Settings.WidgetPinned;
            _suppressSettingEvents = false;

            _listSignature = "";
            RebuildList(service.Snapshot);
            UpdateSourceSummary();
        }

        /// <summary>
        /// The timetable is the only thing here that moves with the clock, and its countdown note
        /// has minute resolution — <see cref="RebuildList"/> short-circuits on an unchanged
        /// signature, so this is cheap even at the fast tick rate the widget asks for.
        /// </summary>
        private void OnTick(PrayerSnapshot snapshot) => RebuildList(snapshot);

        // ── Menu / footer actions ──────────────────────────────────────────────

        // ── Navigation (expanded sub-views) ────────────────────────────────────

        public void Navigate(MainView view)
        {
            // Onboarding owns the window until it is finished.
            if (!PrayerService.Instance.Settings.OnboardingComplete) view = MainView.Onboarding;

            TodayView.Visibility = view == MainView.Today ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = view == MainView.Settings ? Visibility.Visible : Visibility.Collapsed;
            OnboardingView.Visibility = view == MainView.Onboarding ? Visibility.Visible : Visibility.Collapsed;
            SupportView.Visibility = Visibility.Collapsed;

            if (view == MainView.Settings && IsLoaded) LoadSettingsControls();
            if (view == MainView.Onboarding) PreselectClockFormat();
            if (view == MainView.Onboarding && IsLoaded) OnboardingSearchBox.Focus();

            FadeInView(view switch
            {
                MainView.Settings => SettingsView,
                MainView.Onboarding => OnboardingView,
                _ => TodayView
            });
        }

        /// <summary>
        /// Discord presence is the one setting people flip often (on for a stream, off for work),
        /// so it gets a one-click home in the footer as well as a row in settings.
        /// </summary>
        /// <summary>Cross-view transition. Skipped before the window is loaded, where animating
        /// from zero opacity would just leave the first frame blank.</summary>
        private void FadeInView(UIElement view) =>
            Ui.FadeIn(view, IsLoaded && PrayerService.Instance.Settings.AnimationsEnabled);

        private void DiscordQuickToggle_Click(object sender, RoutedEventArgs e)
        {
            var settings = PrayerService.Instance.Settings.Clone();
            settings.DiscordRpcEnabled = !settings.DiscordRpcEnabled;

            PrayerService.Instance.ApplySettings(settings);
            App.Host.SyncDiscord(settings.DiscordRpcEnabled);

            _suppressSettingEvents = true;
            DiscordToggle.IsChecked = settings.DiscordRpcEnabled;
            DiscordShortcutToggle.IsChecked = settings.ShowDiscordShortcut;
            _suppressSettingEvents = false;

            UpdateDiscordQuickToggle();
        }

        private void UpdateDiscordQuickToggle()
        {
            var settings = PrayerService.Instance.Settings;
            bool on = settings.DiscordRpcEnabled;

            // The countdown row only means anything while presence is actually being published.
            Ui.SetRowEnabled(DiscordCountdownToggle, DiscordCountdownLabel, on);

            DiscordButton.Visibility = settings.ShowDiscordShortcut
                ? Visibility.Visible
                : Visibility.Collapsed;

            DiscordIcon.Fill = Ui.Theme(this,
                on ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
            DiscordButton.ToolTip = Localization.T(on ? "S_DiscordOn" : "S_DiscordOff");
        }

        // ── Support ────────────────────────────────────────────────────────────

        private void UpdateSupportAffordance()
        {
            var settings = PrayerService.Instance.Settings;

            if (settings.IsSupporter)
            {
                SupportGlyph.Fill = Ui.Theme(this, "AccentTextFillColorPrimaryBrush");
                SupportButton.ToolTip = "Thank you for supporting GoPray";
                SupportDot.Visibility = Visibility.Collapsed;
                return;
            }

            SupportGlyph.Fill = Ui.Theme(this, "TextFillColorTertiaryBrush");
            SupportButton.ToolTip = "Support GoPray";

            if (settings.ShouldHintSupport())
            {
                SupportDot.Visibility = Visibility.Visible;
                settings.SupportPrompt = "hinted";
                SettingsService.Save(settings);
            }
            else
            {
                SupportDot.Visibility = settings.SupportPrompt == "hinted"
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void Support_Click(object sender, RoutedEventArgs e)
        {
            var settings = PrayerService.Instance.Settings;
            SupportHeadline.Text = Localization.T(
                settings.IsSupporter ? "S_ThankYouSupporting" : "S_ThankYouUsing");

            if (settings.SupportPrompt != "dismissed")
            {
                settings.SupportPrompt = "dismissed";
                SettingsService.Save(settings);
            }

            SupportDot.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            SupportView.Visibility = Visibility.Visible;

            FadeInView(SupportView);
        }

        private void CloseSupport_Click(object sender, RoutedEventArgs e)
        {
            SupportView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Visible;

            FadeInView(SettingsView);
        }

        private void Share_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(Localization.T("S_ShareText"));
                ShareButton.Content = Localization.T("S_Copied");

                var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                reset.Tick += (_, _) =>
                {
                    reset.Stop();
                    ShareButton.SetResourceReference(ContentProperty, "S_CopyNote");
                };
                reset.Start();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e) => Navigate(MainView.Settings);
        private void BackToToday_Click(object sender, RoutedEventArgs e) => Navigate(MainView.Today);
        private void Quit_Click(object sender, RoutedEventArgs e) => App.Host.Quit();

        /// <summary>
        /// Wipes settings, the cached timetable and the log, then quits — GoPray starts at the
        /// location picker next time. Two clicks, because it is not reversible.
        /// </summary>
        private void ClearData_Click(object sender, RoutedEventArgs e)
        {
            // `as`, not a cast: Content is typed object, and a hard cast turns any future
            // non-string content into an exception on the one button that must never misfire.
            // Compared against the live resource rather than a literal, so the two-click guard
            // still works in a language where "Clear" is not the word on the button.
            if (ClearDataButton.Content as string != Localization.T("S_Clear"))
            {
                SettingsService.ClearAll();
                StartupService.SetEnabled(false);
                App.Host.QuitWithoutSaving();
                return;
            }

            ClearDataButton.Content = Localization.T("S_Confirm");
            ClearDataButton.Appearance = ControlAppearance.Danger;

            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                ClearDataButton.SetResourceReference(ContentProperty, "S_Clear");
                ClearDataButton.Appearance = ControlAppearance.Secondary;
            };
            reset.Start();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => _ = PrayerService.Instance.ForceRefreshAsync();

        // ── Today ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the timetable when the next prayer, the clock format or the day changes.
        /// The countdown against the next prayer is minute-resolution, so a per-second rebuild
        /// would be wasted work.
        /// </summary>
        private void RebuildList(PrayerSnapshot snapshot)
        {
            var service = PrayerService.Instance;
            var data = service.Today;
            if (data == null)
            {
                // Dropped on purpose when the source changes. Returning early here left the
                // previous location's timetable on screen under the new location's heading.
                if (PrayerList.ItemsSource != null)
                {
                    PrayerList.ItemsSource = null;
                    _listSignature = "";
                }
                return;
            }

            int minutesLeft = (int)Math.Ceiling(Math.Max(0, snapshot.Remaining.TotalMinutes));
            var signature = string.Join('|', snapshot.NextName, service.Settings.Is24Hour,
                service.Settings.ShowIqamah, Localization.Current,
                service.Timetable?.FetchedAt.Ticks ?? 0, DateTime.Today.DayOfYear, minutesLeft);

            if (signature == _listSignature) return;
            _listSignature = signature;

            var accent = Ui.Theme(this, "AccentTextFillColorPrimaryBrush");
            var accentRow = Ui.Theme(this, "SubtleFillColorSecondaryBrush");
            var textPrimary = Ui.Theme(this, "TextFillColorPrimaryBrush");
            var textSecondary = Ui.Theme(this, "TextFillColorSecondaryBrush");
            var textDisabled = Ui.Theme(this, "TextFillColorDisabledBrush");

            var now = DateTime.Now;
            var rows = new List<PrayerRowView>();

            bool showIqamah = service.Settings.ShowIqamah;

            foreach (var (name, time) in data.GetAllForDay())
            {
                bool isNext = name == snapshot.NextName && !snapshot.NextIsTomorrow;
                bool isPast = !isNext && PrayerService.IsPast(time, now);
                bool isSunrise = name == "Sunrise";

                var iqamah = showIqamah ? data.IqamahFor(name) : "";

                rows.Add(new PrayerRowView
                {
                    Name = Localization.Prayer(name),
                    Time = PrayerService.FormatClock(time, service.Settings.Is24Hour),
                    Iqamah = iqamah.Length == 0
                        ? ""
                        : Localization.T("S_Iqamah",
                            PrayerService.FormatClock(iqamah, service.Settings.Is24Hour)),
                    Note = isNext ? DescribeWait(minutesLeft) : "",
                    RowBackground = isNext ? accentRow : Brushes.Transparent,
                    Foreground = isNext ? accent
                               : isPast ? textDisabled
                               : isSunrise ? textSecondary
                               : textPrimary,
                    IqamahForeground = isNext ? accent
                                     : isPast ? textDisabled
                                     : textSecondary,
                    Weight = isNext ? FontWeights.SemiBold : FontWeights.Normal,
                    MarkerVisibility = isNext ? Visibility.Visible : Visibility.Collapsed
                });
            }

            PrayerList.ItemsSource = rows;
        }

        private static string DescribeWait(int minutes) => minutes switch
        {
            <= 1 => Localization.T("S_WaitNow"),
            < 60 => Localization.T("S_WaitMinutes", minutes),
            _ => Localization.T("S_WaitHours", minutes / 60, minutes % 60)
        };

        // ── Settings ───────────────────────────────────────────────────────────

        /// <summary>
        /// Re-reads settings into the controls, but only when they are actually on screen. Called
        /// when something outside this window changed a setting — the widget's own pin button, or
        /// the tray — so the two never disagree about what is switched on.
        /// </summary>
        public void ReloadSettingsIfShowing()
        {
            if (IsVisible && SettingsView.Visibility == Visibility.Visible) LoadSettingsControls();
        }

        private void LoadSettingsControls()
        {
            var settings = PrayerService.Instance.Settings;
            _suppressSettingEvents = true;

            OverlayToggle.IsChecked = settings.AdhanOverlayEnabled;
            AdhanToggle.IsChecked = settings.AdhanEnabled;
            NotifyToggle.IsChecked = settings.NotificationsEnabled;
            Clock24Toggle.IsChecked = settings.Is24Hour;
            AnimationsToggle.IsChecked = settings.AnimationsEnabled;
            WidgetToggle.IsChecked = settings.WidgetVisible;
            PinToggle.IsChecked = settings.WidgetPinned;
            FollowWidgetToggle.IsChecked = settings.MainFollowsWidget;
            StartupToggle.IsChecked = settings.StartWithWindows;
            DiscordToggle.IsChecked = settings.DiscordRpcEnabled;
            // Easy to miss, and expensive to miss: CommitSettings reads every switch on this page
            // back into settings, so a switch that is never loaded silently writes its unset
            // default over the saved value the first time any other setting is touched.
            DiscordShortcutToggle.IsChecked = settings.ShowDiscordShortcut;
            DiscordCountdownToggle.IsChecked = settings.DiscordRpcShowCountdown;
            VolumeSlider.Value = settings.AdhanVolume;

            SelectByTag(MethodCombo, ((int)settings.Method).ToString());

            SelectByTag(WidgetLayoutCombo, settings.WidgetLayout);
            SelectByTag(WidgetTextSizeCombo, settings.WidgetTextSize);
            WidgetProgressToggle.IsChecked = settings.WidgetShowProgress;
            WidgetActionsToggle.IsChecked = settings.WidgetShowActions;

            SelectByTag(LanguageCombo, settings.Language);
            SelectByTag(HijriOffsetCombo, settings.HijriOffset.ToString(CultureInfo.InvariantCulture));
            IqamahToggle.IsChecked = settings.ShowIqamah;
            UpdatesToggle.IsChecked = settings.CheckForUpdates;

            if (_alertSwitches.Count == 0) BuildPrayerAlertRows();
            else LoadPrayerAlertRows();

            RefreshHotkeyLabel();
            ShowUpdateResult(App.Host.LatestUpdate);

            UpdateSourceSummary();
            UpdateVolumeRowState();
            UpdatePreviewState();
            BuildSettingsRail();
            UpdateSupportAffordance();
            UpdateWidgetOptionStates();

            // Patch included: it is the part that distinguishes two builds people actually have
            // installed, and this line is what they read back when reporting something.
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version == null
                ? "GoPray"
                : $"GoPray {version.Major}.{version.Minor}.{version.Build}";

            _suppressSettingEvents = false;
        }

        private static void SelectByTag(ComboBox combo, string tag) =>
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                                            .FirstOrDefault(i => i.Tag?.ToString() == tag);

        /// <summary>
        /// The settings that matter depend on where the times come from. A Mawaqit mosque
        /// publishes its own timetable, so a calculation method would be meaningless there.
        /// </summary>
        private void UpdateSourceSummary()
        {
            var settings = PrayerService.Instance.Settings;
            bool published = settings.Provider == ApiProvider.Mawaqit
                             && !string.IsNullOrEmpty(settings.MawaqitMosqueUuid);

            CurrentLocationText.Text = settings.LocationLabel();
            SourceMosqueIcon.Visibility = published ? Visibility.Visible : Visibility.Collapsed;
            SourceCityIcon.Visibility = published ? Visibility.Collapsed : Visibility.Visible;

            SourceDetailText.Text = published
                ? Localization.T("S_PublishedByMosque")
                : Localization.T("S_CalculatedFor", settings.City, settings.Country);

            MethodCard.Visibility = published ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateVolumeRowState()
        {
            bool on = AdhanToggle.IsChecked == true;
            VolumeRow.IsEnabled = on;
            VolumeRow.Opacity = on ? 1 : 0.4;
        }

        // ── Widget options ─────────────────────────────────────────────────────

        private void WidgetOption_Changed(object sender, RoutedEventArgs e) => CommitSettings();
        private void WidgetOption_Changed(object sender, SelectionChangedEventArgs e) => CommitSettings();

        private void UpdateWidgetOptionStates()
        {
            bool hasRows = PrayerService.Instance.Settings.WidgetLayout != "Countdown";
            Ui.SetRowEnabled(WidgetProgressToggle, WidgetProgressLabel, hasRows);
        }

        // ── Settings sections ──────────────────────────────────────────────────

        /// <summary>Rail entries, paired with the resource key for their tooltip.</summary>
        private readonly List<(string Key, string TipKey, SymbolRegular Icon)> _settingsSections = new()
        {
            ("Location", "S_SecLocationTip", SymbolRegular.Location24),
            ("Alerts", "S_SecAlerts", SymbolRegular.Alert24),
            ("Widget", "S_SecWidget", SymbolRegular.Board24),
            ("App", "S_SecApp", SymbolRegular.Options24),
            ("About", "S_SecAboutTip", SymbolRegular.Info24)
        };

        private string _settingsSection = "Location";

        private void BuildSettingsRail()
        {
            if (SettingsRail.Children.Count > 0) { RefreshSettingsSection(); return; }

            foreach (var (key, tipKey, icon) in _settingsSections)
            {
                var button = new Wpf.Ui.Controls.Button
                {
                    Tag = key,
                    Appearance = ControlAppearance.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 4),
                    Icon = new SymbolIcon { Symbol = icon, FontSize = 17 }
                };

                // DynamicResource, not a resolved string: the tooltip then follows a language
                // change like every other label rather than needing the rail rebuilt.
                button.SetResourceReference(ToolTipProperty, tipKey);

                button.Click += (_, _) => { _settingsSection = key; RefreshSettingsSection(); };
                SettingsRail.Children.Add(button);
            }

            RefreshSettingsSection();
        }

        private void RefreshSettingsSection()
        {
            SectionLocation.Visibility = Show("Location");
            SectionAlerts.Visibility = Show("Alerts");
            SectionWidget.Visibility = Show("Widget");
            SectionApp.Visibility = Show("App");
            SectionAbout.Visibility = Show("About");

            foreach (var child in SettingsRail.Children)
            {
                if (child is not Wpf.Ui.Controls.Button button) continue;

                bool active = (string?)button.Tag == _settingsSection;
                button.Appearance = active ? ControlAppearance.Secondary : ControlAppearance.Transparent;
                button.Opacity = active ? 1 : 0.6;
            }

            var shown = _settingsSection switch
            {
                "Alerts" => (FrameworkElement)SectionAlerts,
                "Widget" => SectionWidget,
                "App" => SectionApp,
                "About" => SectionAbout,
                _ => SectionLocation
            };

            FadeInView(shown);

            Visibility Show(string key) =>
                _settingsSection == key ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Setting_Changed(object sender, RoutedEventArgs e) => CommitSettings();
        private void MethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => CommitSettings();

        private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSettingEvents) return;

            AdhanSoundService.SetVolume(e.NewValue);
            _settingsCommitDebounce.Stop();
            _settingsCommitDebounce.Start();
        }

        private void CommitSettings()
        {
            if (_suppressSettingEvents) return;
            _settingsCommitDebounce.Stop();

            var settings = PrayerService.Instance.Settings.Clone();

            settings.AdhanOverlayEnabled = OverlayToggle.IsChecked == true;
            settings.AdhanEnabled = AdhanToggle.IsChecked == true;
            settings.NotificationsEnabled = NotifyToggle.IsChecked == true;
            settings.Is24Hour = Clock24Toggle.IsChecked == true;
            settings.AnimationsEnabled = AnimationsToggle.IsChecked == true;
            settings.WidgetPinned = PinToggle.IsChecked == true;
            settings.MainFollowsWidget = FollowWidgetToggle.IsChecked == true;
            settings.StartWithWindows = StartupToggle.IsChecked == true;
            settings.DiscordRpcEnabled = DiscordToggle.IsChecked == true;

            settings.ShowDiscordShortcut = DiscordShortcutToggle.IsChecked == true;
            settings.DiscordRpcShowCountdown = DiscordCountdownToggle.IsChecked == true;
            settings.AdhanVolume = VolumeSlider.Value;

            if (MethodCombo.SelectedItem is ComboBoxItem method && method.Tag is string mt && int.TryParse(mt, out var m))
                settings.Method = (CalculationMethod)m;

            settings.WidgetShowProgress = WidgetProgressToggle.IsChecked == true;
            settings.WidgetShowActions = WidgetActionsToggle.IsChecked == true;
            settings.ShowIqamah = IqamahToggle.IsChecked == true;
            settings.CheckForUpdates = UpdatesToggle.IsChecked == true;

            if (HijriOffsetCombo.SelectedItem is ComboBoxItem offset && offset.Tag is string ot
                && int.TryParse(ot, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var days))
                settings.HijriOffset = days;

            foreach (var (prayer, switches) in _alertSwitches)
            {
                settings.PrayerAlerts[prayer] = new PrayerAlertSetting
                {
                    Enabled = switches.Enabled.IsChecked == true,
                    Adhan = switches.Sound.IsChecked == true
                };
            }

            if (WidgetLayoutCombo.SelectedItem is ComboBoxItem layout && layout.Tag is string lt)
                settings.WidgetLayout = lt;
            if (WidgetTextSizeCombo.SelectedItem is ComboBoxItem size && size.Tag is string zt)
                settings.WidgetTextSize = zt;

            var previous = PrayerService.Instance.Settings;
            bool discordChanged = settings.DiscordRpcEnabled != previous.DiscordRpcEnabled;
            bool startupChanged = settings.StartWithWindows != previous.StartWithWindows;
            PrayerService.Instance.ApplySettings(settings);

            if (startupChanged) StartupService.SetEnabled(settings.StartWithWindows);
            if (discordChanged) App.Host.SyncDiscord(settings.DiscordRpcEnabled);

            App.Host.ApplyWidgetPreferences();

            UpdateVolumeRowState();
            UpdatePreviewState();
            UpdateWidgetOptionStates();
            UpdateDiscordQuickToggle();

            // The Hijri line and the iqamah column are both rebuilt from settings rather than
            // from the tick, so they need an explicit nudge.
            HijriText.Text = PrayerService.HijriToday();
            _listSignature = "";
            RebuildList(PrayerService.Instance.Snapshot);
        }

        private void WidgetToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressSettingEvents) return;
            App.Host.SetWidgetVisible(WidgetToggle.IsChecked == true);
        }

        /// <summary>Shows exactly what happens at prayer time, using the current settings.</summary>
        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsCommitDebounce.IsEnabled) CommitSettings();

            var settings = PrayerService.Instance.Settings;
            var snapshot = PrayerService.Instance.Snapshot;
            var prayer = snapshot.HasData ? snapshot.NextName : "Prayer";

            if (settings.AdhanOverlayEnabled)
            {
                App.Host.PreviewReminder(prayer,
                    PrayerService.FormatClock(snapshot.NextTimeRaw, settings.Is24Hour));
            }
            else if (settings.AdhanEnabled)
            {
                if (AdhanSoundService.IsPlaying) AdhanSoundService.FadeOutAndStop();
                else AdhanSoundService.Play(settings.AdhanVolume);
            }
        }

        private void UpdatePreviewState()
        {
            var settings = PrayerService.Instance.Settings;
            PreviewButton.IsEnabled = settings.AdhanOverlayEnabled || settings.AdhanEnabled;
        }

        // ── Location search (shared by settings and onboarding) ────────────────

        private void LocationSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox box) return;

            _pendingSearchSource = box;
            _searchCts?.Cancel();
            _searchDebounceTimer.Stop();

            if (box.Text.Trim().Length < 2)
            {
                ShowResults(box, new List<LocationResult>(), "");
                return;
            }

            ShowHint(box, Localization.T("S_Searching"));
            _searchDebounceTimer.Start();
        }

        private async System.Threading.Tasks.Task RunSearchAsync()
        {
            var box = _pendingSearchSource;
            if (box == null) return;

            var query = box.Text.Trim();
            if (query.Length < 2) return;

            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                var results = await LocationSearchService.SearchAsync(query, _searchMode, token);
                if (token.IsCancellationRequested) return;

                ShowResults(box, results, results.Count == 0 ? Localization.T("S_NoMatches") : null);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.LogError(ex);
                ShowResults(box, new List<LocationResult>(), Localization.T("S_SearchFailed"));
            }
        }

        private void ShowResults(TextBox source, List<LocationResult> results, string? emptyMessage)
        {
            var rows = results.Select(r => new LocationResultRow(r)).ToList();
            bool onboarding = ReferenceEquals(source, OnboardingSearchBox);

            if (onboarding)
            {
                OnboardingResults.ItemsSource = rows;
                OnboardingResults.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                OnboardingHint.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
                if (rows.Count == 0) OnboardingHint.Text = emptyMessage ?? "";
            }
            else
            {
                SettingsResults.ItemsSource = rows;
                SettingsResults.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ShowHint(TextBox source, string message)
        {
            if (!ReferenceEquals(source, OnboardingSearchBox)) return;
            OnboardingResults.Visibility = Visibility.Collapsed;
            OnboardingHint.Visibility = Visibility.Visible;
            OnboardingHint.Text = message;
        }

        private void LocationResult_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not LocationResultRow row) return;

            if (ReferenceEquals(list, OnboardingResults))
            {
                _onboardingChoice = row.Result;
                OnboardingContinue.IsEnabled = true;
                return;
            }

            var settings = PrayerService.Instance.Settings.Clone();
            row.Result.ApplyTo(settings);
            PrayerService.Instance.ApplySettings(settings);

            SettingsSearchBox.Text = "";
            SettingsResults.Visibility = Visibility.Collapsed;
            list.SelectedItem = null;
            UpdateSourceSummary();
        }

        // ── Onboarding ─────────────────────────────────────────────────────────

        // ── Search mode ────────────────────────────────────────────────────────

        /// <summary>
        /// Flips between Mawaqit mosques and every place on the map. Mosques are the default
        /// because only they carry a real published timetable; the geocoder is the answer for a
        /// mosque that simply is not registered, and it comes with coordinates so the calculated
        /// times are for that exact spot rather than a city name a provider has to guess at.
        /// </summary>
        private void ToggleSearchMode_Click(object sender, RoutedEventArgs e)
        {
            _searchMode = _searchMode == SearchMode.Mosques ? SearchMode.Places : SearchMode.Mosques;
            bool places = _searchMode == SearchMode.Places;

            var label = places ? "S_SearchMosquesAgain" : "S_CantFindMosque";
            SettingsModeButton.SetResourceReference(ContentProperty, label);
            OnboardingModeButton.SetResourceReference(ContentProperty, label);
            SettingsModeHint.Visibility = places ? Visibility.Visible : Visibility.Collapsed;

            // Re-run whatever is already typed, so switching mode answers immediately instead of
            // leaving the previous pool's results sitting under the new label.
            var box = ReferenceEquals(sender, OnboardingModeButton) ? OnboardingSearchBox : SettingsSearchBox;
            if (box.Text.Trim().Length >= 2)
            {
                _pendingSearchSource = box;
                ShowHint(box, Localization.T("S_Searching"));
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
            }
        }

        // ── Per-prayer alerts ──────────────────────────────────────────────────

        /// <summary>
        /// One row per alertable prayer, generated from <see cref="AppSettings.AlertablePrayers"/>
        /// rather than written out five times in XAML — the list, the settings model and the
        /// dispatcher then cannot drift apart, and adding a prayer is a one-line change.
        /// </summary>
        private void BuildPrayerAlertRows()
        {
            PrayerAlertRows.Children.Clear();
            var settings = PrayerService.Instance.Settings;

            foreach (var prayer in AppSettings.AlertablePrayers)
            {
                var alert = settings.AlertFor(prayer);

                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

                var label = new Wpf.Ui.Controls.TextBlock
                {
                    Text = Localization.Prayer(prayer),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                row.Children.Add(label);

                var enabled = NewSwitch(alert.Enabled, 1);
                var sound = NewSwitch(alert.Adhan, 2);

                // The sound switch is meaningless for a prayer that does not alert at all.
                void SyncSound() => Ui.SetRowEnabled(sound, sound, enabled.IsChecked == true);
                SyncSound();

                enabled.Click += (_, _) => { SyncSound(); CommitSettings(); };
                sound.Click += (_, _) => CommitSettings();

                row.Children.Add(enabled);
                row.Children.Add(sound);
                PrayerAlertRows.Children.Add(row);

                _alertSwitches[prayer] = (enabled, sound);

                Wpf.Ui.Controls.ToggleSwitch NewSwitch(bool on, int column)
                {
                    var toggle = new Wpf.Ui.Controls.ToggleSwitch
                    {
                        IsChecked = on,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center
                    };
                    Grid.SetColumn(toggle, column);
                    return toggle;
                }
            }
        }

        private readonly Dictionary<string, (Wpf.Ui.Controls.ToggleSwitch Enabled,
                                             Wpf.Ui.Controls.ToggleSwitch Sound)> _alertSwitches = new();

        /// <summary>Pushes the saved per-prayer values back into the generated switches.</summary>
        private void LoadPrayerAlertRows()
        {
            var settings = PrayerService.Instance.Settings;

            foreach (var (prayer, switches) in _alertSwitches)
            {
                var alert = settings.AlertFor(prayer);
                switches.Enabled.IsChecked = alert.Enabled;
                switches.Sound.IsChecked = alert.Adhan;
                Ui.SetRowEnabled(switches.Sound, switches.Sound, alert.Enabled);
            }
        }

        // ── Global shortcut ────────────────────────────────────────────────────

        /// <summary>
        /// Enters capture mode: the next real key combination pressed becomes the shortcut. The
        /// keys are read through PreviewKeyDown on the window so nothing else — including the
        /// button's own space/enter handling — sees them first.
        /// </summary>
        private void Hotkey_Click(object sender, RoutedEventArgs e)
        {
            _capturingHotkey = true;
            HotkeyWarning.Visibility = Visibility.Collapsed;
            HotkeyButton.SetResourceReference(ContentProperty, "S_HotkeyPress");
            HotkeyButton.Appearance = ControlAppearance.Primary;
            Keyboard.Focus(HotkeyButton);
        }

        private void HotkeyClear_Click(object sender, RoutedEventArgs e)
        {
            App.Host.SetHotkey("");
            EndHotkeyCapture();
        }

        private void OnPreviewKeyDownForHotkey(object sender, KeyEventArgs e)
        {
            if (!_capturingHotkey) return;

            // A dead press — the user is still holding modifiers and has not chosen a key yet.
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (!HotkeyService.IsUsable(Keyboard.Modifiers, key))
            {
                // Escape backs out without changing anything.
                if (key == Key.Escape) { EndHotkeyCapture(); e.Handled = true; }
                return;
            }

            e.Handled = true;

            var gesture = HotkeyService.Format(Keyboard.Modifiers, key);
            bool claimed = App.Host.SetHotkey(gesture);

            EndHotkeyCapture();
            HotkeyWarning.Visibility = claimed ? Visibility.Collapsed : Visibility.Visible;
        }

        private void EndHotkeyCapture()
        {
            _capturingHotkey = false;
            HotkeyButton.Appearance = ControlAppearance.Secondary;
            RefreshHotkeyLabel();
        }

        private void RefreshHotkeyLabel()
        {
            var gesture = PrayerService.Instance.Settings.ToggleWidgetHotkey;

            if (string.IsNullOrWhiteSpace(gesture))
            {
                HotkeyButton.SetResourceReference(ContentProperty, "S_HotkeyNone");
                HotkeyClearButton.Visibility = Visibility.Collapsed;
                return;
            }

            HotkeyButton.Content = gesture;
            HotkeyClearButton.Visibility = Visibility.Visible;
        }

        // ── Language ───────────────────────────────────────────────────────────

        private void Language_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSettingEvents) return;
            if (LanguageCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string code) return;

            App.Host.SetLanguage(code);
        }

        // ── Updates ────────────────────────────────────────────────────────────

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatusText.Text = Localization.T("S_Checking");
            UpdateDownloadButton.Visibility = Visibility.Collapsed;

            try { ShowUpdateResult(await UpdateService.CheckAsync()); }
            finally { CheckUpdatesButton.IsEnabled = true; }
        }

        private void ShowUpdateResult(UpdateCheck? check)
        {
            _update = check;

            if (check?.Version == null)
            {
                // Not checked yet, or checked and there is simply no release published — neither
                // is worth a message. Only a genuine failure gets one.
                UpdateStatusText.Text = ReferenceEquals(check, UpdateCheck.Failed)
                    ? Localization.T("S_UpdateFailed")
                    : "";
                UpdateDownloadButton.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateStatusText.Text = check.IsNewer
                ? Localization.T("S_UpdateAvailable", check.Version.ToString())
                : Localization.T("S_UpToDate");

            UpdateDownloadButton.Visibility = check.IsNewer ? Visibility.Visible : Visibility.Collapsed;
        }

        private UpdateCheck? _update;

        /// <summary>Opens the release page in the browser. GoPray never downloads or installs
        /// anything itself — the user sees what they are getting before it lands.</summary>
        private void UpdateDownload_Click(object sender, RoutedEventArgs e)
        {
            var url = _update?.Url ?? AppInfo.ProjectUrl;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        private void PreselectClockFormat()
        {
            bool system24 = !System.Globalization.CultureInfo.CurrentCulture
                .DateTimeFormat.ShortTimePattern.Contains('h');

            SelectByTag(OnboardingClockCombo, system24 ? "24" : "12");
        }

        private void OnboardingContinue_Click(object sender, RoutedEventArgs e)
        {
            if (_onboardingChoice == null) return;

            var settings = PrayerService.Instance.Settings.Clone();
            _onboardingChoice.ApplyTo(settings);

            if (OnboardingClockCombo.SelectedItem is ComboBoxItem clock && clock.Tag is string ct)
                settings.Is24Hour = ct == "24";

            settings.OnboardingComplete = true;
            PrayerService.Instance.ApplySettings(settings);

            App.Host.CompleteOnboarding();
        }
    }
}
