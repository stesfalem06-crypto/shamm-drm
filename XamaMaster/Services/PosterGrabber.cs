using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace XamaMaster.Services;

/// <summary>
/// Public poster for a protected video: one frame ~10% in (min 3 s), skipping black
/// frames, letterboxed into 320x180, baseline JPEG q≈70 (no EXIF), under 200 KB.
/// Uses WPF MediaPlayer (Windows Media Foundation, in-box) on a private STA thread;
/// falls back to the Windows Shell thumbnail. No new dependencies.
/// </summary>
public static class PosterGrabber
{
    public const int W = 320, H = 180, MaxBytes = 200 * 1024;

    /// <summary>Returns JPEG bytes, or null if no frame could be obtained (never throws).</summary>
    public static byte[]? Grab(string videoPath)
    {
        try
        {
            BitmapSource? frame = null;
            var t = new Thread(() =>
            {
                try { frame = GrabFrameWithMediaPlayer(videoPath); } catch { }
                if (frame == null) { try { frame = ShellThumbnail(videoPath, 640); } catch { } }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(TimeSpan.FromSeconds(45)) || frame == null) return null;
            return Encode(Letterbox(frame));
        }
        catch { return null; }
    }

    private static BitmapSource? GrabFrameWithMediaPlayer(string path)
    {
        BitmapSource? result = null;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var player = new MediaPlayer { ScrubbingEnabled = true, Volume = 0, IsMuted = true };
        var opened = false;
        var failed = false;
        player.MediaOpened += (_, _) => opened = true;
        player.MediaFailed += (_, _) => failed = true;
        player.Open(new Uri(Path.GetFullPath(path)));

        async void Run()
        {
            try
            {
                var start = DateTime.UtcNow;
                while (!opened && !failed && DateTime.UtcNow - start < TimeSpan.FromSeconds(15)) await Task.Delay(50);
                if (!opened) return;
                var dur = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.FromSeconds(30);
                int vw = player.NaturalVideoWidth, vh = player.NaturalVideoHeight;
                if (vw <= 0 || vh <= 0) return;
                var scale = Math.Min(1.0, 640.0 / Math.Max(vw, vh));
                int rw = Math.Max(1, (int)(vw * scale)), rh = Math.Max(1, (int)(vh * scale));
                player.Pause();
                BitmapSource? best = null;
                foreach (var frac in new[] { 0.10, 0.20, 0.30, 0.45, 0.05 })
                {
                    var pos = TimeSpan.FromSeconds(Math.Max(Math.Min(3, dur.TotalSeconds * 0.5), dur.TotalSeconds * frac));
                    player.Position = pos;
                    BitmapSource? shot = null;
                    for (var i = 0; i < 20; i++) // wait up to ~2 s for the frame
                    {
                        await Task.Delay(100);
                        shot = Render(player, rw, rh);
                        if (Luma(shot) > 4) break;
                    }
                    if (shot == null) continue;
                    best ??= shot;
                    if (Luma(shot) >= 18) { best = shot; break; } // not a black/fade frame
                }
                result = best;
            }
            catch { }
            finally
            {
                try { player.Close(); } catch { }
                dispatcher.InvokeShutdown();
            }
        }
        dispatcher.BeginInvoke(Run);
        Dispatcher.Run();
        return result;
    }

    private static BitmapSource Render(MediaPlayer p, int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawVideo(p, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Average luma 0..255 (sampled).</summary>
    private static double Luma(BitmapSource? b)
    {
        if (b == null) return 0;
        var conv = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);
        double sum = 0; long n = 0;
        for (var i = 0; i < px.Length; i += 4 * 7) { sum += 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2]; n++; }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>Fit into 320x180 on black (vertical videos are pillarboxed).</summary>
    private static BitmapSource Letterbox(BitmapSource src)
    {
        var s = Math.Min((double)W / src.PixelWidth, (double)H / src.PixelHeight);
        double dw = src.PixelWidth * s, dh = src.PixelHeight * s;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, H));
            dc.DrawImage(src, new Rect((W - dw) / 2, (H - dh) / 2, dw, dh));
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Baseline JPEG, no metadata; lowers quality until under 200 KB.</summary>
    private static byte[] Encode(BitmapSource bmp)
    {
        var rgb = new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0);
        byte[] bytes = Array.Empty<byte>();
        foreach (var q in new[] { 70, 60, 50, 40 })
        {
            var enc = new JpegBitmapEncoder { QualityLevel = q };
            enc.Frames.Add(BitmapFrame.Create(rgb)); // no metadata/EXIF attached
            using var ms = new MemoryStream();
            enc.Save(ms);
            bytes = ms.ToArray();
            if (bytes.Length <= MaxBytes) break;
        }
        return bytes;
    }

    // ---- Windows Shell thumbnail fallback (what Explorer shows) ----
    private static BitmapSource? ShellThumbnail(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var f) != 0 || f == null) return null;
        try
        {
            if (f.GetImage(new SIZE { cx = size, cy = size }, 0x8 /*ThumbnailOnly*/, out var hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbmp); }
        }
        finally { Marshal.ReleaseComObject(f); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx; public int cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory f);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
}
