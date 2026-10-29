using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Cyclop.App.UI;

/// <summary>A retargetable WPF clock; one scalar drives geometry and opacity without per-frame layout.</summary>
internal sealed class PanelRevealClock : Animatable
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(PanelRevealClock), new PropertyMetadata(0d, (owner, _) => ((PanelRevealClock)owner).Changed?.Invoke()));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public new event Action? Changed;
    private int _generation;
    protected override Freezable CreateInstanceCore() => new PanelRevealClock();

    public void MoveTo(double target, Action completed)
    {
        var from = Progress;
        var generation = ++_generation;
        // The large pill-to-panel morph benefits from a softer start and a longer settling phase.
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(Math.Max(140, 360 * Math.Abs(target - from))))
        { EasingFunction = new MorphEase(), FillBehavior = FillBehavior.HoldEnd };
        animation.Completed += (_, _) =>
        {
            if (generation != _generation) return;
            Stop(target); completed();
        };
        BeginAnimation(ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public void Stop(double value)
    {
        _generation++; Progress = value;
        BeginAnimation(ProgressProperty, null);
    }

    private sealed class MorphEase : IEasingFunction
    {
        private readonly KeySpline _curve = new(.77, 0, .175, 1);
        public double Ease(double normalizedTime) => _curve.GetSplineProgress(normalizedTime);
    }
}
