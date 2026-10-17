using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XSeller.Models;

namespace XSeller.Services;

/// <summary>
/// Windows Shell thumbnails (IShellItemImageFactory) for videos, images, PDFs and exe
/// icons — the same pictures Explorer shows. Loaded on two background STA threads,
/// newest requests first, frozen bitmaps marshalled to the UI.
/// </summary>
public static class ThumbnailService
{
    public static int Size { get; set; } = 160;
    private static readonly ConcurrentStack<WeakReference<LibraryItem>> Queue = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static int _started;

    public static void Request(LibraryItem item)
    {
        if (item.IsOnPhone) return; // phone files: glyph only (no download just for a preview)
        Queue.Push(new WeakReference<LibraryItem>(item));
        Signal.Release();
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            for (var i = 0; i < 2; i++)
            {
                var t = new Thread(Worker) { IsBackground = true, Name = "thumbs-" + i, Priority = ThreadPriority.BelowNormal };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }
        }
    }

    private static void Worker()
    {
        while (true)
        {
            Signal.Wait();
            if (!Queue.TryPop(out var wr) || !wr.TryGetTarget(out var item)) continue;
            try
            {
                var bmp = Load(item.FilePath, Size);
                if (bmp != null)
                    Application.Current?.Dispatcher.BeginInvoke(() => item.Thumbnail = bmp);
            }
            catch { /* no thumbnail: keep glyph */ }
        }
    }

    public static BitmapSource? Load(string path, int size)
    {
        if (!File.Exists(path)) return null;
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null) return null;
        try
        {
            var hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF.ResizeToFit | SIIGBF.BiggerSizeOk, out var hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbmp); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    [Flags]
    private enum SIIGBF { ResizeToFit = 0, BiggerSizeOk = 1, MemoryOnly = 2, IconOnly = 4, ThumbnailOnly = 8, InCacheOnly = 16 }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
