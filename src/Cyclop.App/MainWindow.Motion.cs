using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Cyclop.App;

public sealed partial class MainWindow
{
    private readonly UI.PanelRevealClock _musicReveal = new();
    private readonly TranslateTransform _revealOffset = new();
    private bool _musicMorphActive, _musicMorphTargetOpen;
    private (double Width, double Height)? _morphBounds;
    private bool _settingsResizeActive;
    private double _settingsResizeTarget;
    private int _settingsResizeGeneration;

    private bool MotionEnabled => !_settings.ReducedMotion && SystemParameters.ClientAreaAnimation;

    private void BeginMusicMorph(bool open)
    {
        if (_musicMorphActive && _musicMorphTargetOpen == open) return;
        if (!_musicMorphActive)
        {
            // DPI transitions can temporarily change Window.Width; use state-derived DIP sizes.
            _morphBounds = open ? ExpandedPanelSize : (520, Height);
            if (_display is { } display)
            {
                var scale = display.Dpi / 96d;
                _morphBounds = (Math.Min(_morphBounds.Value.Width, (display.WorkRight - display.WorkLeft) / scale),
                    Math.Min(_morphBounds.Value.Height, (display.WorkBottom - display.WorkTop) / scale));
            }
            _musicReveal.Stop(open ? 0 : 1);
        }
        _musicMorphActive = true; _musicMorphTargetOpen = open;
        _musicPill.SetAnimation(false);
        _musicPill.Margin = new Thickness(0, 4, 0, 0);
        _expanded.BeginAnimation(OpacityProperty, null);
        _reveal.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _musicReveal.MoveTo(open ? 1 : 0, () => { EndMusicMorph(); RenderState(); });
    }

    private void EndMusicMorph()
    {
        _musicMorphActive = false; _morphBounds = null;
        _musicReveal.Stop(_state.IsExpanded ? 1 : 0);
        _expandedHost.Clip = null; _expanded.Opacity = 1;
        _reveal.ScaleX = _reveal.ScaleY = 1; _revealOffset.Y = 0;
        _musicPill.Opacity = 1; _musicPill.Margin = default;
    }

    private void UpdateMotionPreferences()
    {
        if (!MotionEnabled && (_musicMorphActive || _settingsResizeActive || _paneTransitionActive)) { FinishPaneTransition(); StopSettingsResize(); EndMusicMorph(); RenderState(); }
        UpdateMusicPill();
    }

    private void AnimateExpandedHeight()
    {
        var target = ExpandedPanelSize.Height;
        if (_display is { } display) target = Math.Min(target, (display.WorkBottom - display.WorkTop) * 96d / display.Dpi);
        if (_settingsResizeActive && Math.Abs(target - _settingsResizeTarget) < .5) return;
        var from = Height;
        if (Math.Abs(from - target) < .5) { StopSettingsResize(); return; }
        StopSettingsResize();
        _settingsResizeActive = true; _settingsResizeTarget = target;
        var generation = ++_settingsResizeGeneration;
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(Math.Clamp(Math.Abs(target - from), 120, 240)))
        { EasingFunction = new SettingsDrawerEase(), FillBehavior = FillBehavior.HoldEnd };
        animation.Completed += (_, _) =>
        {
            if (generation != _settingsResizeGeneration || _disposed) return;
            Height = target; BeginAnimation(HeightProperty, null); _settingsResizeActive = false;
            if (_musicMorphActive && _morphBounds is { } bounds) _morphBounds = (bounds.Width, target);
            RenderState();
        };
        BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopSettingsResize()
    {
        if (!_settingsResizeActive) return;
        var current = Height;
        _settingsResizeGeneration++; Height = current; BeginAnimation(HeightProperty, null); _settingsResizeActive = false;
    }

    private sealed class SettingsDrawerEase : IEasingFunction
    {
        private readonly KeySpline _curve = new(.32, .72, 0, 1);
        public double Ease(double normalizedTime) => _curve.GetSplineProgress(normalizedTime);
    }

    private void ApplyMusicMorph()
    {
        if (!_musicMorphActive || _morphBounds is not { } bounds) return;
        var progress = Math.Clamp(_musicReveal.Progress, 0, 1);
        var width = bounds.Width; var height = bounds.Height;
        var left = (width - UI.MusicPill.PillWidth) / 2;
        var right = left + UI.MusicPill.PillWidth;
        const double y = 4, radius = UI.MusicPill.PillHeight / 2, top = 12, bottom = 22;
        var endRight = width - top;
        Point P(double startX, double startY, double endX, double endY) => new(
            startX + (endX - startX) * progress, startY + (endY - startY) * progress);
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            path.BeginFigure(P(left + radius, y, 0, 0), true, true);
            path.QuadraticBezierTo(P(left, y, top, 0), P(left, y + radius, top, top), true, false);
            path.LineTo(P(left, y + UI.MusicPill.PillHeight - radius, top, height - bottom), true, false);
            path.QuadraticBezierTo(P(left, y + UI.MusicPill.PillHeight, top, height), P(left + radius, y + UI.MusicPill.PillHeight, top + bottom, height), true, false);
            path.LineTo(P(right - radius, y + UI.MusicPill.PillHeight, endRight - bottom, height), true, false);
            path.QuadraticBezierTo(P(right, y + UI.MusicPill.PillHeight, endRight, height), P(right, y + UI.MusicPill.PillHeight - radius, endRight, height - bottom), true, false);
            path.LineTo(P(right, y + radius, endRight, top), true, false);
            path.QuadraticBezierTo(P(right, y, endRight, 0), P(right - radius, y, width, 0), true, false);
        }
        shape.Freeze(); _silhouette.Data = shape; _expandedHost.Clip = shape;
        // Follow the shell's bounds so labels never get sliced by its moving edges.
        static double Smooth(double value) => value * value * (3 - 2 * value);
        var content = Smooth(Math.Clamp((progress - .25) / .75, 0, 1));
        _expanded.Opacity = content;
        var initialX = UI.MusicPill.PillWidth / width;
        var initialY = UI.MusicPill.PillHeight / height;
        _reveal.ScaleX = initialX + (1 - initialX) * progress;
        _reveal.ScaleY = initialY + (1 - initialY) * progress;
        _revealOffset.Y = 4 * (1 - progress);
        _musicPill.Opacity = 1 - Smooth(Math.Clamp(progress / .4, 0, 1));
    }
}
