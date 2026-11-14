using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace TinyUIFixApp;

/// Lays children out left to right and starts a new line when the next one doesn't fit, so chips and
/// preset buttons stay readable in narrow windows and long languages.
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 8;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > available.Width) { y += line + Spacing; x = 0; line = 0; }
            x += size.Width + Spacing;
            line = Math.Max(line, size.Height);
            widest = Math.Max(widest, x - Spacing);
        }
        return new Size(double.IsInfinity(available.Width) ? widest : Math.Min(widest, available.Width), y + line);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, line = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > final.Width) { y += line + Spacing; x = 0; line = 0; }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, final.Width), size.Height));
            x += size.Width + Spacing;
            line = Math.Max(line, size.Height);
        }
        return final;
    }
}
