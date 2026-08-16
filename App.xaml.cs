using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using GoPray.Models;
using GoPray.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace GoPray
{
    /// <summary>
    /// Application host. Owns the tray icon, the single-instance guard and the lifetime of the
    /// two windows (the widget/full-view window, and the adhan overlay). Windows never create
    /// each other directly; they route through here so there is exactly one of each.
    /// </summary>
    public partial class App : Application
    {
        private const string InstanceMutexName = @"Local\GoPray.SingleInstance";
        private const string ActivationEventName = @"Local\GoPray.Activate";

        public static App Host => (App)Current;

        private Mutex? _mutex;
        private EventWaitHandle? _activationSignal;
        private NotifyIcon? _tray;
        private ContextMenu? _trayMenu;
        private Window? _menuHost;
        private readonly DiscordRpcService _discord = new();
        private readonly HotkeyService _hotkey = new();

        private WidgetWindow? _widget;
        private MainWindow? _main;
        private AdhanOverlayWindow? _overlay;

        private void App_Startup(object sender, StartupEventArgs e)
        {
            _mutex = new Mutex(true, InstanceMutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                SignalRunningInstance();
                Shutdown();
                return;
            }

            // A fault in one UI handler should not take a background tray app down; record it instead.
            DispatcherUnhandledException += (_, args) =>
            {
                LogError(args.Exception);
                args.Handled = true;
            };

            // Background-thread faults cannot be handled, but they can at least be explained.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex) LogError(ex);
            };

            ListenForActivation();

            var service = PrayerService.Instance;
            service.PrayerDue += OnPrayerDue;
            service.PrayerApproaching += OnPrayerApproaching;
            service.Ticked += OnTicked;
            service.Start();

            // Before any window exists, so the first frame is already in the right language and
            // reading direction rather than flipping once it loads.
            Localization.Apply(service.Settings.Language);
            Localization.Changed += OnLanguageChanged;

            AppearanceService.Initialize();

            if (service.Settings.FirstRunUtc == null)
                Persist(s => s.FirstRunUtc = DateTime.UtcNow);

            if (service.Settings.DiscordRpcEnabled) _discord.Enable();

            _hotkey.Pressed += ToggleWidget;
            _hotkey.Register(service.Settings.ToggleWidgetHotkey);

            // The Run entry and the saved preference drift apart on their own (a reinstall into a
            // different folder, a cleanup tool, another machine's roamed settings). Settings win.
            StartupService.Sync(service.Settings.StartWithWindows);

            EnsureWindows();

            // The tray icon comes up before anything else, including onboarding. Neither window
            // has a taskbar button by design, so without the tray there is no way back to them
            // once they fall behind something — during first run that meant a running app with no
            // reachable UI at all.
            CreateTray();

            if (!service.Settings.OnboardingComplete)
            {
                // This used to be an empty branch, on the belief that the window's Loaded handler
                // would take it to the picker. Loaded never fires for a window nobody shows: a
                // first run put up no window, no tray icon and no way to reach either.
                _main!.ShowOnboarding();
            }
            else if (service.Settings.WidgetVisible)
            {
                ShowWidget();
            }

            if (service.Settings.CheckForUpdates) _ = CheckForUpdatesAsync();
        }

        /// <summary>
        /// A quiet start-up check. It never interrupts: the result only reaches the About page, and
        /// nothing is ever downloaded or installed automatically.
        /// </summary>
        private async Task CheckForUpdatesAsync()
        {
            LatestUpdate = await UpdateService.CheckAsync();
        }

        /// <summary>Result of the last start-up check; the About page reads it when it opens.</summary>
        public UpdateCheck? LatestUpdate { get; private set; }

        /// <summary>Rebuilds the few strings that live outside the resource dictionaries.</summary>
        private void OnLanguageChanged()
        {
            // The tray menu is built once and cached; its headers are plain strings, not resource
            // references, so it has to be thrown away and rebuilt in the new language.
            if (_trayMenu != null) { _trayMenu.IsOpen = false; _trayMenu = null; }
            if (_tray != null) _tray.Text = Localization.T("S_AppName");
        }

        /// <summary>Applies a language chosen in settings, and stores it.</summary>
        public void SetLanguage(string language)
        {
            Persist(s => s.Language = language);
            Localization.Apply(language);
        }

        /// <summary>Claims a new global shortcut. Returns false when Windows refused it.</summary>
        public bool SetHotkey(string gesture)
        {
            Persist(s => s.ToggleWidgetHotkey = gesture);
            return _hotkey.Register(gesture);
        }

        private void App_Exit(object sender, ExitEventArgs e) => Cleanup();

        // ── Single instance ────────────────────────────────────────────────────

        private static void SignalRunningInstance()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var handle))
                    using (handle) handle.Set();
            }
            catch { }
        }

        /// <summary>Bring the widget forward when the user launches GoPray a second time.</summary>
        private void ListenForActivation()
        {
            _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            var signal = _activationSignal;

            var thread = new Thread(() =>
            {
                try
                {
                    while (signal.WaitOne())
                        Dispatcher.BeginInvoke(new Action(ShowWidget));
                }
                catch { /* handle disposed during shutdown */ }
            })
            { IsBackground = true, Name = "GoPray.Activation" };

            thread.Start();
        }

        /// <summary>Appends to a small rolling log so unexpected faults are diagnosable after the fact.</summary>
        internal static void LogError(Exception ex)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoPray");
                Directory.CreateDirectory(dir);

                var path = Path.Combine(dir, "error.log");
                if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024) File.Delete(path);

                File.AppendAllText(path, $"[{DateTime.Now:u}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }

        // ── Tray ───────────────────────────────────────────────────────────────

        private void CreateTray()
        {
            if (_tray != null) return;
            try
            {
                using var stream = GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
                if (stream == null) return;

                // No ContextMenuStrip: the WinForms menu looks nothing like the rest of the app.
                _tray = new NotifyIcon
                {
                    Icon = new System.Drawing.Icon(stream),
                    Text = Localization.T("S_AppName"),
                    Visible = true
                };

                // Left click — single or double — means the same thing: put the widget on screen.
                // NotifyIcon raises MouseClick once per release, so a double-click simply calls
                // this twice and the second is a no-op, which is why no double-click handler and
                // no disambiguation timer are needed. The full view has its own doors: the
                // widget's chevron, its context menu, and "Open GoPray" in the tray menu.
                _tray.MouseClick += (_, args) =>
                {
                    if (args.Button == MouseButtons.Left) ShowWidget();
                    else if (args.Button == MouseButtons.Right) ShowTrayMenu();
                };
            }
            catch (Exception ex) { LogError(ex); }
        }

        /// <summary>
        /// The tray menu is a WPF ContextMenu so WPF-UI styles it like the rest of the app.
        /// A WPF popup needs a live PresentationSource to attach to, and the tray icon is not one
        /// — without a host window it throws. A 1x1 off-screen window provides that anchor, and
        /// bringing it foreground is what lets the menu dismiss when you click elsewhere.
        /// </summary>
        private void ShowTrayMenu()
        {
            try
            {
                EnsureMenuHost();
                _trayMenu ??= BuildTrayMenu();

                // Labels reflect current state each time it opens.
                ((MenuItem)_trayMenu.Items[1]).Header = Localization.T(
                    _widget is { IsCardVisible: true } ? "S_HideWidget" : "S_ShowWidget");

                var cursor = WindowInterop.CursorPosition(_menuHost!);

                _trayMenu.PlacementTarget = _menuHost;
                _trayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
                _trayMenu.HorizontalOffset = cursor.X;
                _trayMenu.VerticalOffset = cursor.Y;

                WindowInterop.Foreground(_menuHost!);
                _trayMenu.IsOpen = true;
            }
            catch (Exception ex) { LogError(ex); }
        }

        private void EnsureMenuHost()
        {
            if (_menuHost != null) return;

            _menuHost = new Window
            {
                Width = 1,
                Height = 1,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowActivated = false
            };
            _menuHost.Show();
        }

        private ContextMenu BuildTrayMenu()
        {
            var menu = new ContextMenu { StaysOpen = false };

            menu.Items.Add(Item(Localization.T("S_Open"), () => ShowMain(MainView.Today)));
            menu.Items.Add(Item(Localization.T("S_HideWidget"), ToggleWidget));
            menu.Items.Add(Item(Localization.T("S_RefreshTimes"), () => _ = PrayerService.Instance.ForceRefreshAsync()));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Localization.T("S_Settings"), () => ShowMain(MainView.Settings)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Localization.T("S_Quit"), Quit));

            return menu;

            static MenuItem Item(string header, Action action)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => action();
                return item;
            }
        }

        private void ShowBalloon(string title, string message)
        {
            try { _tray?.ShowBalloonTip(5000, title, message, ToolTipIcon.Info); }
            catch { }
        }

        // ── Windows ────────────────────────────────────────────────────────────

        /// <summary>
        /// Both windows are built once here and kept for the app's whole run; they are only ever
        /// hidden. Two separate windows — rather than one window in two states — is what lets the
        /// widget stay on the desktop while the full view is open. The old flickering-double-widget
        /// race came from <i>recreating</i> a window on every show, not from having two of them, so
        /// nothing here reintroduces it.
        /// </summary>
        private void EnsureWindows()
        {
            if (_widget == null)
            {
                _widget = new WidgetWindow();
            }

            if (_main == null)
            {
                _main = new MainWindow();

                // The full view is the one that behaves like an application window, so it is the
                // one WPF should consider the main window.
                Current.MainWindow = _main;
            }
        }

        /// <summary>
        /// Puts the widget on screen and lifts it to the front. Deliberately not a toggle, which is
        /// what the tray icon used to do and what made it feel broken:
        ///
        /// <list type="bullet">
        /// <item>The widget is never activated, so "visible" and "visible where you can see it" are
        /// different things. Clicking the tray to fetch a widget buried under a browser hid it
        /// instead — the one outcome the user certainly did not want.</item>
        /// <item>The old handler deferred its action by the system double-click time (500ms by
        /// default) to tell single from double clicks apart, so every click felt dead. Clicking
        /// again out of impatience then registered as a double-click and opened the full window.</item>
        /// <item>A click landing during the 180ms hide animation was swallowed entirely, because
        /// the widget still counted as visible while fading out.</item>
        /// </list>
        ///
        /// Show-and-raise has one outcome, needs no timer, and is safe to repeat. Hiding lives in
        /// the tray menu and on the global shortcut, both of which are unambiguous.
        /// </summary>
        public void ShowWidget()
        {
            CreateTray();
            EnsureWindows();
            _widget!.ShowCard();
            Persist(s => s.WidgetVisible = true);
        }

        public void HideWidget()
        {
            _widget?.HideCard();
            Persist(s => s.WidgetVisible = false);
        }

        /// <summary>The settings switch. Now that the widget is its own window it can simply be
        /// shown or hidden straight away, even while the settings page is open.</summary>
        public void SetWidgetVisible(bool visible)
        {
            EnsureWindows();

            if (visible) ShowWidget();
            else HideWidget();
        }

        public void ToggleWidget()
        {
            if (_widget is { IsCardVisible: true }) HideWidget();
            else ShowWidget();
        }

        /// <summary>Screen rectangle of the widget card, for anchoring the full view and the
        /// adhan overlay. Empty when the widget has never been placed.</summary>
        public Rect WidgetBounds => _widget?.CardBounds ?? default;

        /// <summary>Pushes preference changes into the live widget.</summary>
        public void ApplyWidgetPreferences() => _widget?.ApplyPreferences();

        /// <summary>Re-reads settings into the full view's controls, for changes made elsewhere
        /// (the widget's own pin button, the tray).</summary>
        public void RefreshMainSettings() => _main?.ReloadSettingsIfShowing();

        /// <summary>The widget's chevron: opens the full view, or closes it if already showing.</summary>
        public void ToggleMain()
        {
            EnsureWindows();
            if (_main!.IsVisible) _main.Dismiss();
            else ShowMain(MainView.Today);
        }

        public void ShowMain(MainView view)
        {
            EnsureWindows();
            _main!.ShowAt(view);
        }

        /// <summary>Called once onboarding completes: the widget becomes the app's home.</summary>
        public void CompleteOnboarding()
        {
            CreateTray();
            EnsureWindows();
            _main!.Dismiss();
            ShowWidget();
        }

        // ── Prayer events ──────────────────────────────────────────────────────

        /// <summary>
        /// A few seconds out. Opens the overlay early so it can count the last seconds down and be
        /// settled by the time the adhan starts, instead of appearing on top of it.
        /// </summary>
        private void OnPrayerApproaching(string prayer, string time)
        {
            var settings = PrayerService.Instance.Settings;
            if (!settings.AdhanOverlayEnabled || !settings.ShouldAlert(prayer)) return;

            ShowAdhanOverlay(prayer, PrayerService.FormatClock(time, settings.Is24Hour),
                (int)PrayerService.LeadIn.TotalSeconds);
        }

        private void OnPrayerDue(string prayer, string time)
        {
            var settings = PrayerService.Instance.Settings;
            if (!settings.ShouldAlert(prayer)) return;

            var display = PrayerService.FormatClock(time, settings.Is24Hour);

            // Counts how often GoPray has actually done its job; the support icon uses this to
            // decide whether it has earned the right to hint once.
            Persist(s => s.RemindersDelivered++);

            if (settings.AdhanOverlayEnabled)
            {
                // The lead-in normally opened this already, and it starts its own adhan when the
                // count reaches zero. Opening a second one here would restart the animation and
                // cut the sound off a moment after it began.
                if (_overlay?.PrayerKey != prayer) ShowAdhanOverlay(prayer, display);
            }
            else if (settings.ShouldPlayAdhan(prayer))
            {
                AdhanSoundService.Play(settings.AdhanVolume);
            }

            // The overlay is its own on-screen alert; only fall back to a toast without it.
            if (settings.NotificationsEnabled && !settings.AdhanOverlayEnabled)
                ShowBalloon(Localization.T("S_TimeFor", Localization.Prayer(prayer)), display);
        }

        /// <summary>Runs the real reminder on demand so the user can see what they configured.</summary>
        public void PreviewReminder(string prayer, string displayTime)
            => ShowAdhanOverlay(prayer, displayTime);

        private void ShowAdhanOverlay(string prayer, string displayTime, int leadInSeconds = 0)
        {
            _overlay?.Dismiss();

            // The outgoing overlay closes on a fade, so its Closed fires *after* this assignment.
            // Clearing the field unconditionally in that handler wiped the reference to the
            // overlay now on screen, which then could never be dismissed — two reminders in quick
            // succession left one stranded. Only the current overlay may clear the field.
            var overlay = new AdhanOverlayWindow(prayer, displayTime, leadInSeconds);
            overlay.Closed += (_, _) => { if (ReferenceEquals(_overlay, overlay)) _overlay = null; };

            _overlay = overlay;
            overlay.ShowOver(_widget);
        }

        private void OnTicked(PrayerSnapshot snapshot)
        {
            var settings = PrayerService.Instance.Settings;
            if (!settings.DiscordRpcEnabled) return;

            if (!snapshot.HasData) { _discord.ShowWaiting(); return; }

            _discord.UpdatePrayer(snapshot.NextName, snapshot.NextAt,
                settings.LocationLabel(), settings.DiscordRpcShowCountdown);
        }

        public void SyncDiscord(bool enabled)
        {
            if (enabled) _discord.Enable();
            else _discord.Disable();
        }

        /// <summary>Mutates and saves settings without triggering a source refetch.</summary>
        private static void Persist(Action<AppSettings> mutate)
        {
            var settings = PrayerService.Instance.Settings;
            mutate(settings);
            SettingsService.Save(settings);
        }

        // ── Shutdown ───────────────────────────────────────────────────────────

        public void Quit()
        {
            Cleanup();
            _widget?.PrepareForShutdown();
            _main?.PrepareForShutdown();
            Shutdown();
        }

        /// <summary>
        /// Quits without letting anything flush its state to disk. Used right after the data folder
        /// has been deleted, where the ordinary save-on-close would recreate it immediately.
        /// </summary>
        public void QuitWithoutSaving()
        {
            SettingsService.Suspended = true;
            Quit();
        }

        private bool _cleaned;

        private void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;

            try { AdhanSoundService.Stop(); } catch { }
            try { _discord.Dispose(); } catch { }
            try { _hotkey.Dispose(); } catch { }

            if (_trayMenu != null) { _trayMenu.IsOpen = false; _trayMenu = null; }
            if (_menuHost != null) { try { _menuHost.Close(); } catch { } _menuHost = null; }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }

            try { _activationSignal?.Dispose(); } catch { }
            try { _mutex?.Dispose(); } catch { }
        }
    }

    public enum MainView { Today, Settings, Onboarding }
}
