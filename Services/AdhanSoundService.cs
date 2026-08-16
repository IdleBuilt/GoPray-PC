using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NAudio.Wave;

namespace GoPray.Services
{
    /// <summary>
    /// Plays the bundled adhan through NAudio.
    ///
    /// WPF's own MediaPlayer cannot read <c>pack://</c> resource URIs — it hands the URI to
    /// Media Foundation, which only understands real files and http(s), so playback failed
    /// silently. NAudio decodes straight from the resource stream instead, which keeps the
    /// audio embedded in the executable and independent of any system player.
    /// </summary>
    public static class AdhanSoundService
    {
        private static readonly Uri Source = new("adhan.mp3", UriKind.Relative);
        private const int FadeSteps = 20;
        private static readonly TimeSpan FadeStep = TimeSpan.FromMilliseconds(20);

        private static IWavePlayer? _output;
        private static WaveStream? _reader;
        private static Stream? _resource;
        private static DispatcherTimer? _fade;
        private static Action? _onFinished;
        private static float _targetVolume = 1f;

        public static bool IsPlaying { get; private set; }

        /// <param name="onFinished">Invoked on the UI thread when playback ends, fails, or is stopped.</param>
        public static void Play(double volume, Action? onFinished = null)
        {
            Stop();
            _onFinished = onFinished;
            _targetVolume = (float)Math.Clamp(volume, 0, 1);

            try
            {
                _resource = Application.GetResourceStream(Source)?.Stream
                            ?? throw new FileNotFoundException("adhan.mp3 is not embedded in this build");

                // Mp3FileReader needs to seek the frame index, and pack streams are seekable.
                _reader = new Mp3FileReader(_resource);
                _output = new WaveOutEvent { Volume = _targetVolume };
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(_reader);
                _output.Play();

                IsPlaying = true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                Stop();
            }
        }

        /// <summary>NAudio raises this on its own thread; hop back before touching UI state.</summary>
        private static void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null) App.LogError(e.Exception);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) Stop();
            else dispatcher.BeginInvoke(new Action(Stop));
        }

        /// <summary>Ramps the volume down before stopping, so dismissing never cuts the adhan dead.</summary>
        public static void FadeOutAndStop()
        {
            var output = _output;
            if (output == null || !IsPlaying) { Stop(); return; }

            _fade?.Stop();
            float start = output.Volume;
            int step = 0;

            var fade = new DispatcherTimer { Interval = FadeStep };
            fade.Tick += (_, _) =>
            {
                // A newer Play() already replaced this output (and stopped this timer's claim on
                // the field). Bow out quietly — calling Stop() here would kill that new playback.
                if (!ReferenceEquals(_output, output)) { fade.Stop(); return; }

                step++;
                if (step >= FadeSteps) { Stop(); return; }

                try { output.Volume = start * (1f - (float)step / FadeSteps); }
                catch { Stop(); }
            };

            _fade = fade;
            fade.Start();
        }

        public static void Stop()
        {
            _fade?.Stop();
            _fade = null;

            var output = _output;
            var reader = _reader;
            var resource = _resource;

            _output = null;
            _reader = null;
            _resource = null;
            IsPlaying = false;

            if (output != null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                try { output.Stop(); } catch { }
                try { output.Dispose(); } catch { }
            }
            try { reader?.Dispose(); } catch { }
            try { resource?.Dispose(); } catch { }

            var callback = _onFinished;
            _onFinished = null;
            callback?.Invoke();
        }

        /// <summary>Live volume change, used while the settings slider is being dragged.</summary>
        public static void SetVolume(double volume)
        {
            _targetVolume = (float)Math.Clamp(volume, 0, 1);
            try { if (_output != null) _output.Volume = _targetVolume; }
            catch { }
        }
    }
}
