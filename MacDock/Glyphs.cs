using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;

namespace MacDock;

/// <summary>Vector artwork, so these stay sharp at any magnification.</summary>
static class Glyphs
{
    static Geometry G(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }

    // --- Small UI glyphs (24x24, stroked/filled white) -------------------------

    public static readonly Geometry Speaker = G("M3,9.2 L7,9.2 L11.8,5 L11.8,19 L7,14.8 L3,14.8 Z");
    public static readonly Geometry Wave1 = G("M15,9.6 A3.4,3.4 0 0 1 15,14.4");
    public static readonly Geometry Wave2 = G("M17.4,7.2 A6.8,6.8 0 0 1 17.4,16.8");
    public static readonly Geometry Wave3 = G("M19.8,4.8 A10.2,10.2 0 0 1 19.8,19.2");
    public static readonly Geometry MuteX = G("M15.5,9.5 L20.5,14.5 M20.5,9.5 L15.5,14.5");
    public static readonly Geometry WifiArc1 = G("M8.6,15.4 A4.8,4.8 0 0 1 15.4,15.4");
    public static readonly Geometry WifiArc2 = G("M5.6,12.4 A9,9 0 0 1 18.4,12.4");
    public static readonly Geometry WifiArc3 = G("M2.6,9.4 A13.2,13.2 0 0 1 21.4,9.4");
    public static readonly Geometry WifiDot = G("M12,17.2 m-1.7,0 a1.7,1.7 0 1 0 3.4,0 a1.7,1.7 0 1 0 -3.4,0");
    public static readonly Geometry Ethernet = G("M4.5,7 L19.5,7 L19.5,17 L15.5,17 L15.5,19.5 L8.5,19.5 L8.5,17 L4.5,17 Z M8,10 L8,12 M10.7,10 L10.7,12 M13.3,10 L13.3,12 M16,10 L16,12");
    public static readonly Geometry Chevron = G("M7,14.5 L12,9.5 L17,14.5");
    public static readonly Geometry ChevronDown = G("M7,9.5 L12,14.5 L17,9.5");
    public static readonly Geometry ChevronLeft = G("M14.5,7 L9.5,12 L14.5,17");
    public static readonly Geometry ChevronRight = G("M9.5,7 L14.5,12 L9.5,17");
    public static readonly Geometry Close = G("M6,6 L18,18 M18,6 L6,18");
    public static readonly Geometry Play = G("M7.5,4.5 L19.5,12 L7.5,19.5 Z");
    public static readonly Geometry Pause = G("M8.5,5.5 L8.5,18.5 M15.5,5.5 L15.5,18.5");
    public static readonly Geometry Previous = G("M5,5 L7.6,5 L7.6,19 L5,19 Z M19.5,5 L19.5,19 L8.5,12 Z");
    public static readonly Geometry Next = G("M19,5 L16.4,5 L16.4,19 L19,19 Z M4.5,5 L4.5,19 L15.5,12 Z");

    public static Shapes.Path Stroke(Geometry g, double thickness = 1.8, double opacity = 1) => new()
    {
        Data = g,
        Stroke = Brushes.White,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Opacity = opacity,
    };

    public static Shapes.Path Fill(Geometry g, double opacity = 1) => new() { Data = g, Fill = Brushes.White, Opacity = opacity };

    /// <summary>Wraps 24x24 glyph parts in a box of the given size.</summary>
    public static FrameworkElement Box(double size, params UIElement[] parts)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var p in parts) canvas.Children.Add(p);
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }
}
