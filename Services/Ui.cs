using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GoPray.Services
{
    /// <summary>
    /// Small window-layer helpers shared by the widget and the main window. They live here rather
    /// than being copied into both so the two windows cannot drift apart on things like edge
    /// clamping, where a difference would show up as the widget and the panel disagreeing about
    /// where the screen ends.
    /// </summary>
    public static class Ui
    {
        /// <summary>
        /// Clamp that refuses to pass NaN through. <see cref="Math.Clamp(double,double,double)"/>
        /// does — NaN fails every comparison — and WPF then reads the resulting Left/Top as "unset"
        /// and centres the window, which is a very confusing way to find out a size was never
        /// measured.
        /// </summary>
        public static double Clamp(double value, double min, double max)
        {
            if (!double.IsFinite(value)) return min;
            return max < min ? min : Math.Clamp(value, min, max);
        }

        /// <summary>Whether a rectangle describes something that has really been laid out. Checks
        /// finiteness as well as size: a NaN width is not <c>&lt;= 0</c>, so the obvious test lets an
        /// unmeasured card through and every coordinate derived from it comes out NaN.</summary>
        public static bool IsPlaced(Rect rect)
            => double.IsFinite(rect.Left) && double.IsFinite(rect.Top)
            && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
            && rect.Width > 0 && rect.Height > 0;

        /// <summary>
        /// Runs <paramref name="run"/> after a delay unless something else got there first. Used to
        /// guarantee an animation's Completed handler cannot be the only thing that clears a
        /// "closing" flag — a dropped callback would otherwise wedge the window permanently.
        /// </summary>
        public static void RunAfter(TimeSpan delay, Action run)
        {
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); run(); };
            timer.Start();
        }

        /// <summary>Greys a settings row out without hiding it, so the option stays discoverable.</summary>
        public static void SetRowEnabled(UIElement control, UIElement label, bool enabled)
        {
            control.IsEnabled = enabled;
            control.Opacity = enabled ? 1 : 0.4;
            label.Opacity = enabled ? 1 : 0.4;
        }

        /// <summary>A themed brush by key, never null.</summary>
        public static Brush Theme(FrameworkElement scope, string key)
            => scope.TryFindResource(key) as Brush ?? Brushes.Transparent;

        /// <summary>Cross-view transition: the incoming view rises a few pixels as it fades in.</summary>
        public static void FadeIn(UIElement view, bool animate)
        {
            if (!animate)
            {
                view.Opacity = 1;
                return;
            }

            var shift = new TranslateTransform();
            view.RenderTransform = shift;

            view.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        /// <summary>Assigns only when the value actually changed, so WPF is not asked to re-render
        /// identical text once a second.</summary>
        public static void SetText(ref string cache, string value, Action<string> apply)
        {
            if (cache == value) return;
            cache = value;
            apply(value);
        }
    }
}
