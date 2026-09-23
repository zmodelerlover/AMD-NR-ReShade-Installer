// A window that opens taller than the screen loses its footer, and the footer is where the buttons
// are. Nothing here touches a window that already fits, so a large screen is left alone.

using Avalonia;
using Avalonia.Controls;

namespace AmdNr.App;

public static class WindowFit
{
    public static void ToScreen(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null) return;

        // WorkingArea is in physical pixels; Width and Height are in the layout's own units.
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var maxWidth = screen.WorkingArea.Width / scale - 40;
        var maxHeight = screen.WorkingArea.Height / scale - 40;
        if (maxWidth <= 0 || maxHeight <= 0) return;

        var width = Math.Min(window.Width, maxWidth);
        var height = Math.Min(window.Height, maxHeight);
        if (width >= window.Width && height >= window.Height) return;

        window.Width = width;
        window.Height = height;
        // Centred again on the size it actually got, or shrinking leaves it hanging off one edge.
        window.Position = new PixelPoint(
            (int)(screen.WorkingArea.X + (screen.WorkingArea.Width - width * scale) / 2),
            (int)(screen.WorkingArea.Y + (screen.WorkingArea.Height - height * scale) / 2));
    }
}
