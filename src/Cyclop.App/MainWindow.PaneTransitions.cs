using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Cyclop.Core.Panel;

namespace Cyclop.App;

public sealed partial class MainWindow
{
    private PanelTab? _displayedTab;
    private bool _animatePaneSelection, _paneTransitionActive;
    private int _paneTransitionGeneration;
    private readonly Dictionary<FrameworkElement, KeyboardNavigationMode> _outgoingTabNavigation = [];

    private void RenderPaneSelection()
    {
        var changed = _displayedTab != _state.Tab;
        var animate = changed && _displayedTab is not null && _animatePaneSelection && MotionEnabled && !_instantPanelChange;
        _displayedTab = _state.Tab;
        if (!animate)
        {
            if (changed || !_state.IsExpanded || !MotionEnabled || _instantPanelChange) FinishPaneTransition();
            return;
        }

        var generation = ++_paneTransitionGeneration;
        _paneTransitionActive = true;
        foreach (var pair in _panes)
        {
            var pane = pair.Value;
            var incoming = pair.Key == _state.Tab;
            // Read before replacing the clock, including when a previous fade is interrupted.
            var current = pane.Visibility == Visibility.Visible ? pane.Opacity : 0;
            pane.Opacity = current;
            pane.BeginAnimation(OpacityProperty, null);
            if (!incoming && current <= .001) { pane.Visibility = Visibility.Collapsed; RestorePaneNavigation(pane); continue; }
            pane.Visibility = Visibility.Visible;
            pane.IsHitTestVisible = incoming;
            if (incoming) RestorePaneNavigation(pane);
            else
            {
                _outgoingTabNavigation.TryAdd(pane, KeyboardNavigation.GetTabNavigation(pane));
                KeyboardNavigation.SetTabNavigation(pane, KeyboardNavigationMode.None);
            }
            var fade = new DoubleAnimation(current, incoming ? 1 : 0, TimeSpan.FromMilliseconds(incoming ? 160 : 120))
            { EasingFunction = new PaneFadeEase(), FillBehavior = FillBehavior.HoldEnd };
            if (incoming) fade.Completed += (_, _) =>
            {
                if (generation == _paneTransitionGeneration && !_disposed) FinishPaneTransition();
            };
            pane.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void FinishPaneTransition()
    {
        _paneTransitionGeneration++; _paneTransitionActive = false;
        foreach (var pair in _panes)
        {
            var pane = pair.Value;
            pane.Opacity = 1; pane.BeginAnimation(OpacityProperty, null);
            pane.Visibility = pair.Key == _state.Tab ? Visibility.Visible : Visibility.Collapsed;
            pane.IsHitTestVisible = true;
            RestorePaneNavigation(pane);
        }
        _displayedTab = _state.Tab;
    }

    private void RestorePaneNavigation(FrameworkElement pane)
    {
        if (_outgoingTabNavigation.Remove(pane, out var navigation)) KeyboardNavigation.SetTabNavigation(pane, navigation);
    }

    private sealed class PaneFadeEase : IEasingFunction
    {
        private readonly System.Windows.Media.Animation.KeySpline _curve = new(.23, 1, .32, 1);
        public double Ease(double normalizedTime) => _curve.GetSplineProgress(normalizedTime);
    }
}
