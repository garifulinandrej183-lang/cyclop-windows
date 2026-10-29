using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Cyclop.App.ViewModels;

namespace Cyclop.App.UI;

/// <summary>Playback-state indicator, not an audio amplitude meter.</summary>
public sealed class MusicPill : Border
{
    public const double PillWidth = 144;
    public const double PillHeight = 38;
    private readonly List<ScaleTransform> _bars = [];
    private readonly double[] _rest = [.65, .4, .85, 1, .35];
    public bool IsAnimating { get; private set; }

    public MusicPill()
    {
        Width = PillWidth; Height = PillHeight; CornerRadius = new CornerRadius(PillHeight / 2);
        Background = Brushes.Black; Padding = new Thickness(6, 0, 12, 0);
        AutomationProperties.SetName(this, "Spotify играет. Открыть Cyclop наведением или кликом.");
        var content = new Grid(); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var cover = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        cover.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(30, 30, 33)) });
        var fallback = Icons.Create("music", 11.5); fallback.Opacity = .55; cover.Children.Add(fallback);
        var image = new Image { Width = 26, Height = 26, Stretch = Stretch.UniformToFill,
            Clip = new EllipseGeometry(new Point(13, 13), 13, 13) };
        image.SetBinding(Image.SourceProperty, new Binding(nameof(MainViewModel.Artwork)));
        cover.Children.Add(image); content.Children.Add(cover);
        var equalizer = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < _rest.Length; i++)
        {
            var transform = new ScaleTransform(1, _rest[i]); _bars.Add(transform);
            equalizer.Children.Add(new Border { Width = 2, Height = 15, Margin = new Thickness(0, 0, 2, 0),
                CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(Color.FromRgb(218, 218, 222)),
                RenderTransform = transform, RenderTransformOrigin = new Point(.5, .5) });
        }
        Grid.SetColumn(equalizer, 2); content.Children.Add(equalizer); Child = content;
        Unloaded += (_, _) => SetAnimation(false);
    }

    public void SetAnimation(bool animate)
    {
        if (animate == IsAnimating) return;
        IsAnimating = animate;
        for (var i = 0; i < _bars.Count; i++)
        {
            _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _bars[i].ScaleY = _rest[i];
            if (!animate) continue;
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900), RepeatBehavior = RepeatBehavior.Forever };
            var values = new[] { _rest[i], .25 + ((i + 2) % 5) * .15, .9 - i * .1, .3 + (i % 3) * .2, _rest[i] };
            for (var frame = 0; frame < values.Length; frame++)
                animation.KeyFrames.Add(new EasingDoubleKeyFrame(values[frame], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frame * 225)),
                    new SineEase { EasingMode = EasingMode.EaseInOut }));
            Timeline.SetDesiredFrameRate(animation, 30);
            _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
    }
}
