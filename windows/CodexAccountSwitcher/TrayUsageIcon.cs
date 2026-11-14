using System;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CodexAccountSwitcher;

internal static class TrayUsageIcon
{
    public static Icon Create(int remainingPercent, bool lightTaskbar, Size size)
    {
        var label = Math.Clamp(remainingPercent, 0, 100).ToString();
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap)) {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var color = lightTaskbar ? Color.Black : Color.White;
            using var brush = new SolidBrush(color);
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.FormatFlags |= StringFormatFlags.NoWrap;
            for (var pixels = size.Height * 0.88f; pixels >= 5; pixels -= 0.5f) {
                using var font = new Font("Segoe UI", pixels, FontStyle.Bold, GraphicsUnit.Pixel);
                var measured = graphics.MeasureString(label, font, PointF.Empty, format);
                if (measured.Width > size.Width - 1 || measured.Height > size.Height) continue;
                graphics.DrawString(label, font, brush,
                    new RectangleF(0, 0, size.Width, size.Height), format);
                break;
            }
        }
        var handle = bitmap.GetHicon();
        try {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        } finally {
            DestroyIcon(handle);
        }
    }

    // Icons live IN the Windows notification area; this is not a floating WPF window.
    // Each 16-24 pixel icon has a tiny identity label and a numeric value.
    public static Icon CreateMetric(string metric, string number, bool lightTaskbar,
        Size size, CodexAccountSwitcher.Core.MiniBarDisplayStatus status)
    {
        if (metric is not ("5H" or "7D" or "R")) throw new ArgumentException("Invalid metric.", nameof(metric));
        if (size.Width <= 0 || size.Height <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var textColor = lightTaskbar ? Color.FromArgb(24, 32, 43) : Color.White;
        var metricColor = status switch
        {
            CodexAccountSwitcher.Core.MiniBarDisplayStatus.FreshUnbound => metric switch
            {
                "5H" => lightTaskbar ? Color.FromArgb(0, 104, 185) : Color.FromArgb(105, 199, 255),
                "7D" => lightTaskbar ? Color.FromArgb(24, 128, 73) : Color.FromArgb(112, 223, 168),
                _ => lightTaskbar ? Color.FromArgb(145, 84, 15) : Color.FromArgb(255, 199, 105)
            },
            CodexAccountSwitcher.Core.MiniBarDisplayStatus.AgedUnbound =>
                lightTaskbar ? Color.FromArgb(163, 96, 20) : Color.FromArgb(255, 188, 101),
            _ => lightTaskbar ? Color.DimGray : Color.LightGray
        };
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var metricBrush = new SolidBrush(metricColor);
            using var numberBrush = new SolidBrush(textColor);
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.FormatFlags |= StringFormatFlags.NoWrap;

            var topHeight = Math.Max(5f, size.Height * 0.39f);
            using (var labelFont = new Font("Segoe UI", Math.Max(5f, topHeight * 0.90f),
                       FontStyle.Bold, GraphicsUnit.Pixel))
                graphics.DrawString(metric, labelFont, metricBrush,
                    new RectangleF(0, 0, size.Width, topHeight), format);

            var area = new RectangleF(0, topHeight - 1f, size.Width,
                size.Height - topHeight + 1f);
            for (var pixels = Math.Min(area.Height * 1.18f, size.Height * 0.70f);
                 pixels >= 5f; pixels -= 0.5f)
            {
                using var font = new Font("Segoe UI", pixels, FontStyle.Bold, GraphicsUnit.Pixel);
                var measured = graphics.MeasureString(number, font, PointF.Empty, format);
                if (measured.Width > size.Width - 1f || measured.Height > area.Height + 2f)
                    continue;
                graphics.DrawString(number, font, numberBrush, area, format);
                break;
            }
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
