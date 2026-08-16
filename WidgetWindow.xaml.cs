using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GoPray.Models;
using GoPray.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace GoPray
{
    /// <summary>
    /// The always-available desktop card: next prayer, countdown, progress.
    ///
    /// <para>Never activated, so it can never steal the caret from whatever the user is typing in;
    /// it stays on screen until explicitly hidden. Pinning additionally asserts true always-on-top
    /// on a timer.</para>
    ///
    /// <para>Constructed once and kept for the app's whole run — hiding is <see cref="Window.Hide"/>,
    /// never <see cref="Window.Close"/>. Recreating it per show is what used to leave one window
    /// mid-teardown while a new one was already being built, visible as a flickering double
    /// widget.</para>
    /// </summary>
    public partial class WidgetWindow : FluentWindow
    {
        private const double EdgeMargin = 12;
        private static readonly TimeSpan TopmostReassertInterval = TimeSpan.FromSeconds(2);

        private readonly DispatcherTimer _positionSave = new() { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly DispatcherTimer _topmostReassert = new() { Interval = TopmostReassertInterval };

        private bool _closing;
        private bool _measured;
        private bool _subscribed;
        private bool _appExiting;

        private string _lastCountdown = "";
        private string _lastName = "";
        private string _lastSubLine = "";
        private bool _imminent;

        public bool IsCardVisible => IsVisible && !_closing;

        /// <summary>Screen rectangle of the card. The main window anchors above it and the adhan
        /// overlay centres on it, so it is kept current even while the card is hidden.</summary>
        public Rect CardBounds { get; private set; }

        public WidgetWindow()
        {
            InitializeComponent();

            _positionSave.Tick += (_, _) => { _positionSave.Stop(); SavePosition(); };
            _topmostReassert.Tick += (_, _) => WindowInterop.SetTopmost(this, true);

            // The handle only exists from the first Show, and this window must never be activatable.
            SourceInitialized += (_, _) => WindowInterop.SetActivatable(this, false);
            Loaded += OnLoaded;
            Closing += OnClosing;
            LocationChanged += OnLocationChanged;

            // The per-second countdown is the only thing in the app that needs one, so the service
            // ticks fast exactly while this card is on screen.
            IsVisibleChanged += (_, _) => PrayerService.Instance.SetDisplayActive(IsVisible);
        }

        // ── Lifetime ───────────────────────────────────────────────────────────

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppearanceService.Register(this);

            // Loaded fires once for a window that is only ever hidden and reshown, which is this
            // one — but subscribing twice would double every tick, so the guarantee is made local
            // rather than inherited from WPF's lifecycle.
            if (!_subscribed)
            {
                _subscribed = true;
                ApplicationThemeManager.Changed += OnThemeChanged;
                Localization.Changed += OnLanguageChanged;
                PrayerService.Instance.Changed += OnDataChanged;
                PrayerService.Instance.Ticked += OnTick;
            }

            OnDataChanged();
            OnTick(PrayerService.Instance.Snapshot);
        }

        /// <summary>The card has no close affordance of its own; only <see cref="App.Quit"/> really
        /// closes it. Anything else — Alt+F4 with it focused, a shell close — just hides it.</summary>
        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_appExiting) return;

            e.Cancel = true;
            App.Host.HideWidget();
        }

        /// <summary>Lets <see cref="Application.Shutdown"/> actually close this window.</summary>
        public void PrepareForShutdown()
        {
            _appExiting = true;
            _topmostReassert.Stop();

            // The position save is debounced by 600ms. Quitting inside that window — exactly what
            // "drag the widget somewhere, then quit" does — used to drop the move.
            _positionSave.Stop();
            if (!SettingsService.Suspended) SavePosition();
        }

        private void OnThemeChanged(ApplicationTheme theme, Color accent)
        {
            AppearanceService.Register(this);
            UpdatePinVisual();

            // The countdown brush was resolved from the old dictionary and cached on the element,
            // so it has to be re-resolved even though the imminent state has not moved.
            SetImminent(PrayerService.IsImminent(PrayerService.Instance.Snapshot.Remaining), force: true);
        }

        private void OnLanguageChanged()
        {
            _lastName = _lastCountdown = _lastSubLine = "";
            OnDataChanged();
            OnTick(PrayerService.Instance.Snapshot);
        }

        // ── Show / hide ────────────────────────────────────────────────────────

        /// <summary>Puts the card on screen and lifts it to the front. Safe to call repeatedly.</summary>
        public void ShowCard()
        {
            if (IsVisible && !_closing)
            {
                WindowInterop.RaiseWithoutActivating(this);
                SyncPinState();
                return;
            }

            // _closing means a hide is mid-fade. The window still counts as visible for those
            // 180ms, so without cancelling here a show landing in that gap did nothing and the
            // card carried on disappearing.
            _closing = false;

            ApplySizing();
            RestorePosition(PrayerService.Instance.Settings);
            UpdateBounds();

            CompactRoot.Opacity = 0;
            Show();
            PlayEntry();
            SyncPinState();
        }

        public void HideCard()
        {
            if (!IsVisible || _closing) return;
            _closing = true;

            if (!PrayerService.Instance.Settings.AnimationsEnabled) { FinishHide(); return; }

            bool finished = false;
            void Finish() { if (finished) return; finished = true; FinishHide(); }

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(CompactRoot.Opacity, 0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => Finish();

            CompactShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            CompactRoot.BeginAnimation(OpacityProperty, fade);

            // Belt and braces: if the animation is ever replaced before it completes, Completed
            // never fires and _closing would stay stuck true, silently breaking every later hide.
            Ui.RunAfter(TimeSpan.FromMilliseconds(400), Finish);
        }

        private void FinishHide()
        {
            // A show that arrived during the fade already cleared the flag and put the card back;
            // this stale callback must not hide it again.
            if (!_closing) return;

            _closing = false;
            Hide();
        }

        private void PlayEntry()
        {
            if (!PrayerService.Instance.Settings.AnimationsEnabled)
            {
                CompactRoot.Opacity = 1;
                CompactShift.Y = 0;
                return;
            }

            CompactRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
            CompactShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(320))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // ── Size and position ──────────────────────────────────────────────────

        /// <summary>
        /// FluentWindow attaches its zero-thickness WindowChrome in OnSourceInitialized, but the
        /// very first SizeToContent measurement WPF performs still reserves space for the default
        /// OS frame, which the chrome has not replaced yet at that point — leaving extra empty
        /// height on first launch. Toggling SizeToContent forces a remeasure against the correct
        /// chrome, and it is only ever needed once.
        /// </summary>
        private void ApplySizing()
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            ApplyAppearance(PrayerService.Instance.Settings);

            if (_measured) return;
            _measured = true;

            Dispatcher.BeginInvoke(() =>
            {
                SizeToContent = SizeToContent.Manual;
                UpdateLayout();
                SizeToContent = SizeToContent.WidthAndHeight;
                UpdateLayout();
                ApplyAppearance(PrayerService.Instance.Settings);

                // That remeasure is the first time the card's real size is known, and the corner
                // it sits in is measured back from that size. Without repositioning here, a first
                // launch settled the size but kept the guessed placement, leaving the card
                // floating short of the corner.
                RestorePosition(PrayerService.Instance.Settings);
                UpdateBounds();
            }, DispatcherPriority.Loaded);
        }

        private void RestorePosition(AppSettings settings)
        {
            // Only the never-placed-yet default needs a work area, and "bottom-right of the primary
            // display" is exactly the right first-run home. A saved position is clamped to the whole
            // virtual desktop below, so a card parked on a second monitor stays there.
            var work = SystemParameters.WorkArea;
            var (width, height) = CardSize();

            double left = settings.WidgetLeft ?? work.Right - width - EdgeMargin;
            double top = settings.WidgetTop ?? work.Bottom - height - EdgeMargin;

            // A monitor may have been unplugged since the position was saved.
            Left = Ui.Clamp(left, SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - width);
            Top = Ui.Clamp(top, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height);
        }

        /// <summary>
        /// The card's size, never NaN. Under SizeToContent, Width/Height stay NaN until WPF has
        /// measured the window, and NaN poisons everything downstream silently.
        /// </summary>
        private (double Width, double Height) CardSize()
        {
            // ActualWidth/ActualHeight first: under SizeToContent those are the measured truth.
            return (Pick(ActualWidth, Width), Pick(ActualHeight, Height));

            static double Pick(double measured, double declared)
            {
                if (double.IsFinite(measured) && measured > 0) return measured;
                if (double.IsFinite(declared) && declared > 0) return declared;
                return 0;
            }
        }

        private void UpdateBounds()
        {
            var (width, height) = CardSize();
            CardBounds = new Rect(Left, Top, width, height);
        }

        private void SavePosition()
        {
            var settings = PrayerService.Instance.Settings;
            if (Math.Abs((settings.WidgetLeft ?? double.MinValue) - Left) < 0.5 &&
                Math.Abs((settings.WidgetTop ?? double.MinValue) - Top) < 0.5) return;

            settings.WidgetLeft = Left;
            settings.WidgetTop = Top;
            SettingsService.Save(settings);
        }

        private void OnLocationChanged(object? sender, EventArgs e)
        {
            UpdateBounds();
            if (!IsLoaded || _closing) return;
            _positionSave.Start();
        }

        // ── Pin ────────────────────────────────────────────────────────────────

        /// <summary>Applies the pinned state: starts or stops the topmost reassertion loop.</summary>
        public void SyncPinState()
        {
            bool pinned = PrayerService.Instance.Settings.WidgetPinned;

            MenuPin.IsChecked = pinned;
            UpdatePinVisual();

            if (pinned)
            {
                WindowInterop.SetTopmost(this, true);
                _topmostReassert.Start();
            }
            else
            {
                _topmostReassert.Stop();
                WindowInterop.SetTopmost(this, false);
                if (IsVisible) WindowInterop.RaiseWithoutActivating(this);
            }
        }

        private void Pin_Click(object sender, RoutedEventArgs e) => TogglePin();
        private void MenuPin_Click(object sender, RoutedEventArgs e) => TogglePin();

        private void TogglePin()
        {
            var settings = PrayerService.Instance.Settings.Clone();
            settings.WidgetPinned = !settings.WidgetPinned;
            PrayerService.Instance.ApplySettings(settings);

            SyncPinState();
            App.Host.RefreshMainSettings();
        }

        /// <summary>Pinned is red — unambiguous, and consistent with the imminent-countdown colour.</summary>
        private void UpdatePinVisual()
        {
            bool pinned = PrayerService.Instance.Settings.WidgetPinned;
            PinIcon.Symbol = pinned ? SymbolRegular.Pin24 : SymbolRegular.PinOff24;
            PinIcon.Foreground = Ui.Theme(this,
                pinned ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
            PinButton.ToolTip = Localization.T(pinned ? "S_StopKeepAbove" : "S_KeepAbove");
        }

        // ── Interaction ────────────────────────────────────────────────────────

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                App.Host.ToggleMain();
                e.Handled = true;
                return;
            }

            try { DragMove(); }
            catch { /* mouse released before the drag loop started */ }
        }

        private void Expand_Click(object sender, RoutedEventArgs e) => App.Host.ToggleMain();

        private void MenuOpen_Click(object sender, RoutedEventArgs e) => App.Host.ShowMain(MainView.Today);
        private void MenuSettings_Click(object sender, RoutedEventArgs e) => App.Host.ShowMain(MainView.Settings);
        private void MenuHide_Click(object sender, RoutedEventArgs e) => App.Host.HideWidget();
        private void MenuQuit_Click(object sender, RoutedEventArgs e) => App.Host.Quit();
        private void MenuRefresh_Click(object sender, RoutedEventArgs e) => _ = PrayerService.Instance.ForceRefreshAsync();

        // ── Appearance ─────────────────────────────────────────────────────────

        /// <summary>Re-reads preferences the settings page may have changed underneath us.</summary>
        public void ApplyPreferences()
        {
            var settings = PrayerService.Instance.Settings;
            SyncPinState();
            ApplyAppearance(settings);

            // The clock format lives in these two, and both are cached against their last value.
            _lastSubLine = "";
            _lastCountdown = "";
            OnTick(PrayerService.Instance.Snapshot);
        }

        /// <summary>
        /// What the card shows: which lines, which extras, and how large. Colour is not in here —
        /// every brush comes from WPF-UI's theme, which follows Windows.
        /// </summary>
        private void ApplyAppearance(AppSettings settings)
        {
            bool countdownOnly = settings.WidgetLayout == "Countdown";
            bool compact = settings.WidgetLayout == "Compact";

            LabelStack.Visibility = countdownOnly ? Visibility.Collapsed : Visibility.Visible;
            LabelColumn.Width = countdownOnly ? new GridLength(0) : new GridLength(104);
            SubLine.Visibility = compact || countdownOnly ? Visibility.Collapsed : Visibility.Visible;

            Actions.Visibility = settings.WidgetShowActions ? Visibility.Visible : Visibility.Collapsed;
            Progress.Visibility = settings.WidgetShowProgress && !countdownOnly
                ? Visibility.Visible
                : Visibility.Collapsed;

            MainRow.Margin = countdownOnly
                ? new Thickness(12, 8, settings.WidgetShowActions ? 6 : 12, 8)
                : new Thickness(12, 8, 6, settings.WidgetShowProgress ? 0 : 8);

            double scale = settings.WidgetScale();
            ContentScale.ScaleX = ContentScale.ScaleY = scale;
        }

        // ── Data ───────────────────────────────────────────────────────────────

        private void OnDataChanged()
        {
            var service = PrayerService.Instance;

            CompactRoot.ToolTip = service.Status switch
            {
                DataStatus.Live or DataStatus.Cached =>
                    $"{service.Settings.LocationLabel()} · {service.Timetable?.ProviderName}",
                DataStatus.Unavailable => Localization.T("S_TooltipNoTimes"),
                _ => Localization.T("S_TooltipLoading")
            };

            UpdateSubLine(service);
        }

        private void OnTick(PrayerSnapshot snapshot)
        {
            var service = PrayerService.Instance;

            if (!snapshot.HasData)
            {
                Ui.SetText(ref _lastName, "—", t => PrayerName.Text = t);
                Ui.SetText(ref _lastCountdown, "--:--", t => Countdown.Text = t);

                // Without these the card kept the previous location's time under the new
                // location's name, and the progress bar stayed frozen at its last fill, while the
                // replacement timetable was still being fetched.
                SetImminent(false);
                UpdateProgress(0);
            }
            else
            {
                Ui.SetText(ref _lastName, Localization.Prayer(snapshot.NextName), t => PrayerName.Text = t);
                Ui.SetText(ref _lastCountdown, PrayerService.FormatCountdown(snapshot.Remaining),
                    t => Countdown.Text = t);

                SetImminent(PrayerService.IsImminent(snapshot.Remaining));
                UpdateProgress(snapshot.Progress);
            }

            UpdateSubLine(service, snapshot);
        }

        /// <summary>Under a minute the countdown turns critical — the last call to get moving.</summary>
        /// <param name="force">Re-resolve the brush even when the state has not changed, for when
        /// the theme moved underneath it and the cached state is still correct.</param>
        private void SetImminent(bool imminent, bool force = false)
        {
            if (imminent == _imminent && !force) return;
            _imminent = imminent;

            Countdown.Foreground = Ui.Theme(this,
                imminent ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush");
        }

        private void UpdateSubLine(PrayerService service, PrayerSnapshot? snapshot = null)
        {
            snapshot ??= service.Snapshot;

            string text;
            if (!snapshot.HasData)
            {
                text = service.Status == DataStatus.Loading
                    ? Localization.T("S_Loading")
                    : Localization.T("S_CantReach", PrayerService.ProviderLabel());
            }
            else
            {
                var clock = PrayerService.FormatClock(snapshot.NextTimeRaw, service.Settings.Is24Hour);
                text = snapshot.NextIsTomorrow ? Localization.T("S_Tomorrow", clock) : clock;
            }

            Ui.SetText(ref _lastSubLine, text, t => SubLine.Text = t);
        }

        private void UpdateProgress(double progress) => Progress.Value = Math.Clamp(progress, 0, 1);
    }
}
