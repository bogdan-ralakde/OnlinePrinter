using System.Drawing.Drawing2D;

namespace LabelBridge.UI;

internal static class IconFactory
{
    public static Bitmap Bitmap(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using (var path = Theme.RoundRect(new Rectangle(0, 0, size, size), Math.Max(3, size / 5)))
        using (var brush = new LinearGradientBrush(
                   new Rectangle(0, 0, size, size),
                   Color.FromArgb(0x3B, 0x82, 0xF6), Color.FromArgb(0x1D, 0x4E, 0xD8), 55f))
        {
            g.FillPath(brush, path);
        }

        float pad = size * 0.23f;
        var labelRect = Rectangle.Round(new RectangleF(pad, pad * 1.05f, size - pad * 2, size * 0.58f));
        using (var labelPath = Theme.RoundRect(labelRect, Math.Max(2, size / 18)))
        using (var white = new SolidBrush(Color.White))
        {
            g.FillPath(white, labelPath);
        }

        using (var pen = new Pen(Color.FromArgb(0x25, 0x63, 0xEB), Math.Max(1.2f, size / 20f)))
        {
            for (int i = 0; i < 3; i++)
            {
                float y = labelRect.Top + labelRect.Height * (0.3f + i * 0.2f);
                g.DrawLine(pen, labelRect.Left + labelRect.Width * 0.14f, y,
                    labelRect.Right - labelRect.Width * 0.14f, y);
            }
        }

        // little "network" dot at the bottom right
        float dot = size * 0.2f;
        using (var dotBrush = new SolidBrush(Color.FromArgb(0x7F, 0xE3, 0xB0)))
            g.FillEllipse(dotBrush, size - dot - pad * 0.55f, size - dot - pad * 0.55f, dot, dot);

        return bmp;
    }

    public static Icon AppIcon(int size)
    {
        using var bmp = Bitmap(size);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
