using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace XamaMaster.Services;

/// <summary>
/// Public poster for a protected PDF: page 1 rendered 320 px wide with the in-box
/// Windows.Data.Pdf API, baseline JPEG under 200 KB (same encoder as video posters).
/// Falls back to the Explorer/Shell thumbnail. Never throws; null if nothing worked.
/// </summary>
public static class PdfPoster
{
    public const int Width = 320;

    public static byte[]? Grab(string pdfPath)
    {
        try
        {
            var task = Task.Run(() => RenderFirstPageAsync(pdfPath));
            var png = task.Wait(TimeSpan.FromSeconds(30)) ? task.Result : null;
            if (png != null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(png);
                bmp.EndInit();
                bmp.Freeze();
                return PosterGrabber.Encode(bmp);
            }
        }
        catch { }
        try
        {
            BitmapSource? thumb = null;
            var t = new Thread(() => { try { thumb = PosterGrabber.ShellThumbnail(pdfPath, Width); } catch { } });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (t.Join(TimeSpan.FromSeconds(20)) && thumb != null) return PosterGrabber.Encode(thumb);
        }
        catch { }
        return null;
    }

    private static async Task<byte[]?> RenderFirstPageAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var doc = await PdfDocument.LoadFromFileAsync(file);
        if (doc.PageCount == 0) return null;
        using var page = doc.GetPage(0);
        using var stream = new InMemoryRandomAccessStream();
        var opts = new PdfPageRenderOptions { DestinationWidth = Width };
        await page.RenderToStreamAsync(stream, opts); // PNG by default
        var size = (uint)stream.Size;
        if (size == 0) return null;
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
