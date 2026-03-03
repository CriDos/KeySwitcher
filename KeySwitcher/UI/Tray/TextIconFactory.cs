using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Avalonia.Controls;

namespace KeySwitcher.UI.Tray;

internal static class TextIconFactory
{
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64];
    private const int SupersampleFactor = 6;
    private const float HorizontalPaddingFactor = 0.04f;
    private const float VerticalPaddingFactor = 0.03f;
    private const float VerticalOffsetFactor = 0.01f;
    private const int OutlineRadius = 1;
    private static readonly string[] PreferredFontFamilies =
    [
        "Segoe UI Variable Display",
        "Segoe UI",
        "Bahnschrift",
    ];

    public static WindowIcon Create(string text, Color foregroundColor)
    {
        text = string.IsNullOrWhiteSpace(text) ? "?" : text.Trim().ToUpperInvariant();
        var frames = BuildFrames(text, foregroundColor);
        var iconBytes = BuildMultiSizeIcon(frames);
        return new WindowIcon(new MemoryStream(iconBytes));
    }

    private static List<IconFrame> BuildFrames(string text, Color foregroundColor)
    {
        var fontFamily = ResolveFontFamily();
        var frames = new List<IconFrame>(IconSizes.Length);

        foreach (var size in IconSizes)
        {
            var png = RenderFramePng(size, text, foregroundColor, fontFamily);
            frames.Add(new IconFrame(size, png));
        }

        return frames;
    }

    private static byte[] RenderFramePng(
        int iconSize,
        string text,
        Color foregroundColor,
        FontFamily fontFamily
    )
    {
        var renderSize = iconSize * SupersampleFactor;
        using var renderBitmap = new Bitmap(renderSize, renderSize, PixelFormat.Format32bppPArgb);
        using var renderGraphics = Graphics.FromImage(renderBitmap);
        renderGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        renderGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        renderGraphics.CompositingQuality = CompositingQuality.HighQuality;
        renderGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        renderGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        renderGraphics.Clear(Color.Transparent);

        using var font = FindBestFont(renderGraphics, fontFamily, text, iconSize);
        using var textFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        var renderRect = new RectangleF(
            0,
            iconSize * VerticalOffsetFactor * SupersampleFactor,
            renderSize,
            renderSize
        );
        using var outlineBrush = new SolidBrush(Color.FromArgb(180, foregroundColor));
        using var textBrush = new SolidBrush(foregroundColor);

        for (var y = -OutlineRadius; y <= OutlineRadius; y++)
        {
            for (var x = -OutlineRadius; x <= OutlineRadius; x++)
            {
                if (x == 0 && y == 0)
                {
                    continue;
                }

                var shifted = new RectangleF(
                    renderRect.X + x,
                    renderRect.Y + y,
                    renderRect.Width,
                    renderRect.Height
                );
                renderGraphics.DrawString(text, font, outlineBrush, shifted, textFormat);
            }
        }

        renderGraphics.DrawString(text, font, textBrush, renderRect, textFormat);

        using var bitmap = new Bitmap(iconSize, iconSize, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        graphics.DrawImage(
            renderBitmap,
            new Rectangle(0, 0, iconSize, iconSize),
            new Rectangle(0, 0, renderSize, renderSize),
            GraphicsUnit.Pixel
        );

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static Font FindBestFont(
        Graphics graphics,
        FontFamily fontFamily,
        string text,
        int iconSize
    )
    {
        var targetWidth = iconSize * (1f - HorizontalPaddingFactor * 2f) * SupersampleFactor;
        var targetHeight = iconSize * (1f - VerticalPaddingFactor * 2f) * SupersampleFactor;
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        var maxFontSize = (float)(iconSize * SupersampleFactor);
        for (var size = maxFontSize; size >= 8f; size -= 1f)
        {
            using var probe = new Font(fontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
            var measured = graphics.MeasureString(text, probe, PointF.Empty, format);
            if (measured.Width <= targetWidth && measured.Height <= targetHeight)
            {
                return new Font(fontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
            }
        }

        return new Font(fontFamily, 8f, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static byte[] BuildMultiSizeIcon(IReadOnlyList<IconFrame> frames)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0); // ICONDIR.Reserved
        writer.Write((ushort)1); // ICONDIR.Type (icon)
        writer.Write((ushort)frames.Count); // ICONDIR.Count

        var imageOffset = 6 + (16 * frames.Count);
        foreach (var frame in frames)
        {
            writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size)); // Width
            writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size)); // Height
            writer.Write((byte)0); // ColorCount
            writer.Write((byte)0); // Reserved
            writer.Write((ushort)1); // Planes
            writer.Write((ushort)32); // BitCount
            writer.Write(frame.Data.Length); // BytesInRes
            writer.Write(imageOffset); // ImageOffset
            imageOffset += frame.Data.Length;
        }

        foreach (var frame in frames)
        {
            writer.Write(frame.Data);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static FontFamily ResolveFontFamily()
    {
        foreach (var familyName in PreferredFontFamilies)
        {
            try
            {
                using var candidate = new FontFamily(familyName);
                return new FontFamily(familyName);
            }
            catch
            {
                // try next font
            }
        }

        return FontFamily.GenericSansSerif;
    }

    private readonly record struct IconFrame(int Size, byte[] Data);
}
