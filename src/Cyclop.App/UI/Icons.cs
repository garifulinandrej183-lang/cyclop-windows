using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cyclop.App.UI;

internal static class Icons
{
    // Authored geometric paths; no font glyph dependencies.
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["music"] = "M9,18 L9,5 20,3 20,16 M9,9 L20,7 M9,18 C9,22 2,22 2,19 C2,16 9,15 9,18 M20,16 C20,20 13,20 13,17 C13,14 20,13 20,16",
        ["shelf"] = "M3,5 L3,20 21,20 21,5 M3,14 L8,14 10,17 14,17 16,14 21,14 M8,4 L16,4 M8,8 L16,8",
        ["clipboard"] = "M8,5 L4,5 4,22 20,22 20,5 16,5 M8,3 L16,3 16,7 8,7 Z M8,12 L16,12 M8,16 L16,16",
        ["translate"] = "M2,5 L14,5 M8,2 L8,5 M12,5 C11,12 6,16 2,18 M4,8 C6,12 9,15 13,17 M14,22 L18,12 22,22 M16,18 L20,18",
        ["play"] = "M8,4 L20,12 8,20 Z", ["pause"] = "M8,4 L8,20 M16,4 L16,20",
        ["previous"] = "M5,5 L5,19 M19,5 L7,12 19,19 Z", ["next"] = "M19,5 L19,19 M5,5 L17,12 5,19 Z",
        ["close"] = "M6,6 L18,18 M18,6 L6,18", ["copy"] = "M8,8 L21,8 21,21 8,21 Z M16,8 L16,3 3,3 3,16 8,16",
        ["swap"] = "M3,7 L21,7 M17,3 L21,7 17,11 M21,17 L3,17 M7,13 L3,17 7,21",
        ["folder"] = "M2,6 L10,6 12,9 22,9 20,21 2,21 Z M2,6 L2,3 10,3 12,6 20,6 20,9",
        ["add"] = "M12,4 L12,20 M4,12 L20,12",
        ["trash"] = "M4,6 L20,6 M9,6 L9,3 15,3 15,6 M6,6 L7,21 17,21 18,6 M10,10 L10,17 M14,10 L14,17",
        ["volume"] = "M3,9 L7,9 12,5 12,19 7,15 3,15 Z M16,8 C19,10 19,14 16,16 M19,5 C23,9 23,15 19,19",
        ["volume-off"] = "M3,9 L7,9 12,5 12,19 7,15 3,15 Z M16,9 L22,15 M22,9 L16,15",
        ["pin"] = "M7,3 L17,3 15,8 18,12 14,14 12,22 10,14 6,12 9,8 Z",
        ["file"] = "M5,2 L14,2 20,8 20,22 5,22 Z M14,2 L14,8 20,8 M8,13 L17,13 M8,17 L17,17"
    };
    public static FrameworkElement Create(string name, double size = 18, Brush? color = null)
    {
        Geometry geometry;
        if (name == "settings")
        {
            // Eight symmetric teeth around the same center as the inner ring.
            var outline = new StreamGeometry();
            using (var path = outline.Open())
            {
                for (var tooth = 0; tooth < 8; tooth++)
                    for (var corner = 0; corner < 4; corner++)
                    {
                        var angle = (tooth * 45 + (corner - 1.5) * 9) * Math.PI / 180;
                        var radius = corner is 1 or 2 ? 10 : 8;
                        var point = new Point(12 + Math.Cos(angle) * radius, 12 + Math.Sin(angle) * radius);
                        if (tooth == 0 && corner == 0) path.BeginFigure(point, false, true); else path.LineTo(point, true, false);
                    }
            }
            geometry = new GeometryGroup { Children = { outline, new EllipseGeometry(new Point(12, 12), 4, 4) } };
        }
        else geometry = Geometry.Parse(Paths.GetValueOrDefault(name, Paths["file"]));
        var pen = new Pen(color ?? Brushes.White, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        return Drawing(geometry, null, pen, size);
    }

    public static FrameworkElement CreateMedia(string name, double size = 16)
    {
        var path = name switch
        {
            "pause" => "M6,5 H9 V19 H6 Z M15,5 H18 V19 H15 Z",
            "previous" => "M4,5 H6 V19 H4 Z M20,5 L9,12 20,19 Z",
            "next" => "M18,5 H20 V19 H18 Z M4,5 L15,12 4,19 Z",
            _ => "M8,5 L19,12 8,19 Z"
        };
        return Drawing(Geometry.Parse(path), Brushes.White, null, size);
    }

    private static Image Drawing(Geometry geometry, Brush? fill, Pen? pen, double size)
    {
        // A fixed viewBox preserves authored coordinates instead of stretching each path's bounds.
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        drawing.Children.Add(new GeometryDrawing(fill, pen, geometry)); drawing.Freeze();
        var source = new DrawingImage(drawing); source.Freeze();
        return new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }
}
