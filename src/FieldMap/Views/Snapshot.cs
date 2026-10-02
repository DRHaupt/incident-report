using System.Drawing.Printing;
using FieldMap.Map;
using SkiaSharp;

namespace FieldMap.Views;

/// <summary>Map snapshots as PNG, PDF or print-out, with a footer giving date, position, data sources and the disclaimer.</summary>
public static class Snapshot
{
    /// <summary>The map at twice screen resolution with a footer band (scale bar and north arrow are in the map itself).</summary>
    public static SKBitmap Compose(MapView map, string? title = null)
    {
        using var mapBmp = map.RenderSnapshot(2f);
        var footerLines = FooterLines(map, title);
        var lineHeight = 30f;
        var footer = (int)(footerLines.Count * lineHeight + 24);
        var bmp = new SKBitmap(mapBmp.Width, mapBmp.Height + footer);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(mapBmp, 0, 0);
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default, 22);
        using var bold = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default, 24);
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
        var y = mapBmp.Height + 12 + lineHeight * 0.8f;
        for (var i = 0; i < footerLines.Count; i++)
        {
            canvas.DrawText(footerLines[i], 16, y, SKTextAlign.Left, i == 0 ? bold : font, paint);
            y += lineHeight;
        }
        return bmp;
    }

    public static List<string> FooterLines(MapView map, string? title)
    {
        var c = map.Center;
        var data = AppServices.Data;
        return new List<string>
        {
            $"{title ?? Branding.ProductName} – {DateTime.Now:dd MMM yyyy HH:mm}{(data.IsSample ? "   SAMPLE DATA – FICTITIOUS" : "")}",
            $"Centre {CoordinateFormat.Decimal(c.Lat, c.Lon)} (WGS84) · MGRS {CoordinateFormat.Mgrs(c.Lat, c.Lon)} · map up = {(map.Bearing < 0.5 ? "north" : $"{map.Bearing:0}° true")}",
            $"Data: {data.Attribution}{(data.DataDate is { } d ? $" · data date {d:yyyy-MM-dd}" : "")}",
            "Supports, and does not replace, official CAD/dispatch. Map data may be incomplete or outdated."
        };
    }

    private static string? AskFile(IWin32Window owner, string filter, string ext)
    {
        using var d = new SaveFileDialog { Filter = filter, FileName = $"FieldMap-{DateTime.Now:yyyyMMdd-HHmm}.{ext}" };
        return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
    }

    public static void ExportPng(IWin32Window owner, MapView map)
    {
        var path = AskFile(owner, "PNG image (*.png)|*.png", "png");
        if (path == null)
            return;
        SavePng(map, path);
    }

    public static void SavePng(MapView map, string path)
    {
        using var bmp = Compose(map);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 95);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    public static void ExportPdf(IWin32Window owner, MapView map)
    {
        var path = AskFile(owner, "PDF document (*.pdf)|*.pdf", "pdf");
        if (path == null)
            return;
        SavePdf(map, path);
    }

    public static void SavePdf(MapView map, string path)
    {
        using var bmp = Compose(map);
        using var stream = File.Create(path);
        using var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = "Field Map snapshot", Creator = Branding.ProductName, Producer = "Field Map", Creation = DateTime.Now });
        // US Letter landscape (792 × 612 pt), image fitted with a margin.
        const float pageW = 792, pageH = 612, margin = 24;
        var scale = Math.Min((pageW - 2 * margin) / bmp.Width, (pageH - 2 * margin) / bmp.Height);
        var canvas = doc.BeginPage(pageW, pageH);
        var w = bmp.Width * scale;
        var h = bmp.Height * scale;
        canvas.DrawBitmap(bmp, new SKRect((pageW - w) / 2, margin, (pageW + w) / 2, margin + h));
        doc.EndPage();
        doc.Close();
    }

    public static void Print(IWin32Window owner, MapView map)
    {
        using var bmp = Compose(map);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 95);
        using var ms = new MemoryStream(data.ToArray());
        using var image = Image.FromStream(ms);
        using var doc = new PrintDocument { DocumentName = "Field Map snapshot" };
        doc.DefaultPageSettings.Landscape = true;
        doc.PrintPage += (_, e) =>
        {
            var area = e.MarginBounds;
            var scale = Math.Min((float)area.Width / image.Width, (float)area.Height / image.Height);
            e.Graphics!.DrawImage(image, area.Left, area.Top, image.Width * scale, image.Height * scale);
        };
        using var dialog = new PrintDialog { Document = doc, UseEXDialog = true };
        if (dialog.ShowDialog(owner) == DialogResult.OK)
            doc.Print();
    }
}
