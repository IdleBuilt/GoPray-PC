using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using GoPray.Services;

namespace GoPray
{
    /// <summary>
    /// The prayer reminder. Not a dialog and not a card — a transparent layer of light that
    /// settles over whatever is on screen, plays the adhan, and disappears on a click.
    ///
    /// It never takes keyboard focus: WS_EX_NOACTIVATE keeps the user's typing exactly where it
    /// was, which matters because this appears unannounced five times a day.
    /// </summary>
    public partial class AdhanOverlayWindow : Window
    {
        private const int MaxParticles = 18;
        private static readonly TimeSpan SilentDuration = TimeSpan.FromSeconds(18);
        private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(6);

        private readonly DispatcherTimer _particleSpawner = new() { Interval = TimeSpan.FromMilliseconds(230) };
        private readonly DispatcherTimer _phraseCycle = new() { Interval = TimeSpan.FromSeconds(3.2) };
        private readonly DispatcherTimer _safetyTimeout = new();
        private readonly Random _random = new();
        private readonly bool _animate;

        private int _particleCount;
        private bool _showingPhrase;
        private bool _dismissing;
        private DateTime _adhanStartedAt;
        private Brush _particleBrush = Brushes.Gray;

        private readonly DispatcherTimer _leadIn = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly string _prayerKey;
        private int _leadInRemaining;

        /// <summary>Which prayer this overlay belongs to, so the same one is not opened twice when
        /// the lead-in has already put it on screen.</summary>
        public string PrayerKey => _prayerKey;

        /// <param name="leadInSeconds">
        /// Seconds to count down before the reminder proper. Zero shows it immediately, which is
        /// what a preview and a missed lead-in both want.
        /// </param>
        public AdhanOverlayWindow(string prayerName, string displayTime, int leadInSeconds = 0)
        {
            InitializeComponent();

            _animate = PrayerService.Instance.Settings.AnimationsEnabled;
            _prayerKey = prayerName;

            PrayerNameText.Text = Localization.Prayer(prayerName);
            TimeLine.Text = displayTime;

            // A lead-in only makes sense with animation; without it the numbers would snap in and
            // out and read as a glitch rather than a countdown.
            _leadInRemaining = _animate ? Math.Max(0, leadInSeconds) : 0;

            _particleSpawner.Tick += (_, _) => SpawnParticle();
            _phraseCycle.Tick += (_, _) => CyclePhrase();
            _safetyTimeout.Tick += (_, _) => Dismiss();
            _leadIn.Tick += (_, _) => TickLeadIn();

            SourceInitialized += (_, _) => MakeNonActivating();
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        // ── Focus safety ───────────────────────────────────────────────────────

        private const int GwlExStyle = -20;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        /// <summary>
        /// Marks the window as never-activate so showing it cannot pull focus away from whatever
        /// the user is doing. ShowActivated="False" alone still lets a click activate it.
        /// </summary>
        private void MakeNonActivating()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero) return;

                var style = (long)GetWindowLongPtr(handle, GwlExStyle);
                SetWindowLongPtr(handle, GwlExStyle,
                    new IntPtr(style | WsExNoActivate | WsExToolWindow));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        /// <summary>Centres the reminder on the compact widget, or the bottom-right when it isn't
        /// currently showing (hidden, or the expanded view is open instead).</summary>
        public void ShowOver(WidgetWindow? window)
        {
            // The monitor the widget is on, measured through the widget's own DPI transform, so a
            // reminder for a widget parked on a second screen is not clamped onto the primary one.
            var work = window is { IsCardVisible: true }
                ? WindowInterop.WorkAreaAround(window, window.CardBounds)
                : SystemParameters.WorkArea;

            if (window is { IsCardVisible: true })
            {
                var card = window.CardBounds;
                Left = card.Left + card.Width / 2 - Width / 2;
                Top = card.Top + card.Height / 2 - Height / 2;
            }
            else
            {
                Left = work.Right - Width - 8;
                Top = work.Bottom - Height - 8;
            }

            Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
            Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));

            Show();
        }

        // ── Lifetime ───────────────────────────────────────────────────────────

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _particleBrush = TryFindResource("AccentTextFillColorPrimaryBrush") as Brush ?? Brushes.Gray;

            if (_leadInRemaining > 0) BeginLeadIn();
            else BeginReminder();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _particleSpawner.Stop();
            _phraseCycle.Stop();
            _safetyTimeout.Stop();
            _leadIn.Stop();
        }

        // ── Lead-in ────────────────────────────────────────────────────────────

        /// <summary>
        /// The final seconds, as plain fading numerals. The reminder itself is held back — hidden
        /// rather than merely transparent, so its halo and rings do not sit behind the count — and
        /// the adhan does not start until zero.
        /// </summary>
        private void BeginLeadIn()
        {
            MainContent.Visibility = Visibility.Hidden;
            Halo.Opacity = 0.35;

            // The layer itself still fades in, so the overlay does not appear as a hard cut.
            Root.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));

            ShowLeadInNumber();
            _leadIn.Start();
        }

        private void TickLeadIn()
        {
            _leadInRemaining--;

            if (_leadInRemaining > 0) { ShowLeadInNumber(); return; }

            _leadIn.Stop();
            LeadInText.BeginAnimation(OpacityProperty,
                new DoubleAnimation(LeadInText.Opacity, 0, TimeSpan.FromMilliseconds(220)));

            MainContent.Visibility = Visibility.Visible;
            BeginReminder();
        }

        /// <summary>One numeral: fades up quickly, drifts down over the second, never fully out
        /// until the next one replaces it.</summary>
        private void ShowLeadInNumber()
        {
            LeadInText.Text = _leadInRemaining.ToString(CultureInfo.InvariantCulture);

            var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(950) };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromPercent(0.2)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromPercent(0.65)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromPercent(1)));
            LeadInText.BeginAnimation(OpacityProperty, fade);

            var settle = new DoubleAnimation(1.25, 1, TimeSpan.FromMilliseconds(650))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            LeadInScale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
            LeadInScale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        }

        /// <summary>The reminder proper: entrance, adhan, ambient motion.</summary>
        private void BeginReminder()
        {
            if (Halo.Opacity < 1)
                Halo.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(Halo.Opacity, 1, TimeSpan.FromMilliseconds(300)));

            PlayEntrance();
            StartAdhan();

            if (!_animate) return;

            AnimateRings();
            BreatheHalo();
            _particleSpawner.Start();
            _phraseCycle.Start();
        }

        private void StartAdhan()
        {
            var settings = PrayerService.Instance.Settings;

            if (!settings.AdhanEnabled)
            {
                StartSafetyTimeout(SilentDuration);
                return;
            }

            // Hard ceiling in case playback never reports completion.
            StartSafetyTimeout(MaxDuration);
            _adhanStartedAt = DateTime.UtcNow;

            AdhanSoundService.Play(settings.AdhanVolume,
                () => Dispatcher.BeginInvoke(new Action(OnAdhanFinished)));
        }

        /// <summary>
        /// Playback finished. An "instant" finish means the audio failed to open rather than
        /// played through, so the reminder stays for its silent duration instead of blinking away.
        /// </summary>
        private void OnAdhanFinished()
        {
            if (_dismissing) return;

            if (DateTime.UtcNow - _adhanStartedAt < TimeSpan.FromSeconds(2))
            {
                _safetyTimeout.Stop();
                StartSafetyTimeout(SilentDuration);
                return;
            }

            Dismiss();
        }

        private void StartSafetyTimeout(TimeSpan duration)
        {
            _safetyTimeout.Interval = duration;
            _safetyTimeout.Start();
        }

        // ── Entrance / exit ────────────────────────────────────────────────────

        private void PlayEntrance()
        {
            if (!_animate)
            {
                Root.Opacity = 1;
                RootScale.ScaleX = RootScale.ScaleY = 1;
                NameScale.ScaleX = NameScale.ScaleY = 1;
                return;
            }

            // From wherever the layer already is: after a lead-in it is fully visible, and
            // restarting from zero would blink the whole reminder out and back at the exact
            // moment the adhan starts.
            Root.BeginAnimation(OpacityProperty,
                new DoubleAnimation(Root.Opacity, 1, TimeSpan.FromMilliseconds(420))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            var settle = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(520))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);

            var name = new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(700))
            {
                BeginTime = TimeSpan.FromMilliseconds(90),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
            };
            NameScale.BeginAnimation(ScaleTransform.ScaleXProperty, name);
            NameScale.BeginAnimation(ScaleTransform.ScaleYProperty, name);
        }

        /// <summary>Fades the adhan out and lets the light dissipate outward. Safe to call twice.</summary>
        public void Dismiss()
        {
            if (_dismissing) return;
            _dismissing = true;

            _particleSpawner.Stop();
            _phraseCycle.Stop();
            _safetyTimeout.Stop();
            _leadIn.Stop();

            if (AdhanSoundService.IsPlaying) AdhanSoundService.FadeOutAndStop();

            if (!_animate) { Close(); return; }

            var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(360))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (_, _) => Close();
            Root.BeginAnimation(OpacityProperty, fade);

            // Expanding rather than shrinking: it disperses instead of being snatched away.
            var expand = new DoubleAnimation(1, 1.06, TimeSpan.FromMilliseconds(360));
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, expand);
        }

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Dismiss();

        // ── Ambient animation ──────────────────────────────────────────────────

        /// <summary>The halo swells very slowly, so the layer never looks frozen.</summary>
        private void BreatheHalo()
        {
            var breathe = new DoubleAnimation(1, 1.06, TimeSpan.FromSeconds(3.5))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
            HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        }

        private void AnimateRings()
        {
            AnimateRing(Ring1Scale, Ring1, 0);
            AnimateRing(Ring2Scale, Ring2, 1000);
            AnimateRing(Ring3Scale, Ring3, 2000);
        }

        /// <summary>One ring expanding out of the centre and fading, on a rolling 3s loop.</summary>
        private static void AnimateRing(ScaleTransform scale, UIElement ring, int delayMs)
        {
            var duration = TimeSpan.FromMilliseconds(3000);
            var begin = TimeSpan.FromMilliseconds(delayMs);

            var grow = new DoubleAnimation(0.5, 4.2, duration)
            {
                BeginTime = begin,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

            var fade = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = begin,
                Duration = duration,
                RepeatBehavior = RepeatBehavior.Forever
            };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.5, KeyTime.FromPercent(0.15)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            ring.BeginAnimation(OpacityProperty, fade);
        }

        private void SpawnParticle()
        {
            if (_dismissing || _particleCount >= MaxParticles) return;
            if (ActualWidth <= 0 || ActualHeight <= 0) return;

            double size = _random.NextDouble() * 3 + 1.5;
            var particle = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = _particleBrush,
                Opacity = 0
            };

            // Spawn within the halo rather than the full window, so they rise out of the light.
            double x = ActualWidth / 2 + (_random.NextDouble() - 0.5) * 260;
            double startY = ActualHeight / 2 + 70;
            double endY = ActualHeight / 2 - 90;

            Canvas.SetLeft(particle, x);
            Canvas.SetTop(particle, startY);
            ParticleCanvas.Children.Add(particle);
            _particleCount++;

            int lifetime = _random.Next(2800, 4800);
            double peak = _random.NextDouble() * 0.4 + 0.25;

            var rise = new DoubleAnimation(startY, endY, TimeSpan.FromMilliseconds(lifetime))
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };

            rise.Completed += (_, _) =>
            {
                ParticleCanvas.Children.Remove(particle);
                _particleCount--;
            };

            // Gentle sway, so the field does not read as a column of dots.
            var drift = new DoubleAnimation(x, x + _random.Next(-26, 27), TimeSpan.FromMilliseconds(lifetime))
            { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };

            var glow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(lifetime) };
            glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            glow.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(0.25)));
            glow.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(0.65)));
            glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));

            particle.BeginAnimation(Canvas.TopProperty, rise);
            particle.BeginAnimation(Canvas.LeftProperty, drift);
            particle.BeginAnimation(OpacityProperty, glow);
        }

        private void CyclePhrase()
        {
            _showingPhrase = !_showingPhrase;
            var duration = TimeSpan.FromMilliseconds(600);

            PhraseLine.BeginAnimation(OpacityProperty,
                new DoubleAnimation(_showingPhrase ? 1 : 0, duration));
            MessageLine.BeginAnimation(OpacityProperty,
                new DoubleAnimation(_showingPhrase ? 0 : 1, duration));
        }
    }
}
