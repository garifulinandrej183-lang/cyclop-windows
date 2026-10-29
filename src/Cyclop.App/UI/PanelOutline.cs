using System.Windows;
using System.Windows.Media;

namespace Cyclop.App.UI;

/// <summary>Paints the morphing outline without invalidating layout as Path.Data does.</summary>
public sealed class PanelOutline : FrameworkElement
{
    private Geometry _data = Geometry.Empty;
    public Geometry Data
    {
        get => _data;
        set { _data = value; InvalidateVisual(); }
    }
    protected override void OnRender(DrawingContext context) => context.DrawGeometry(Brushes.Black, null, _data);
}
