using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace WallZ.Core;

public static class ThumbnailProvider
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WALL Z",
        "Thumbnails");

    /// In-memory LRU cache capped at 40 entries (~270x480 x 4 bytes x 40 ≈ 20 MB max).
    private static readonly LruImageCache Cache = new(maxSize: 40);
    private static readonly LruColorCache ColorCache = new(maxSize: 80);

    private static readonly HashSet<string> _inProgress = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _queueLock = new();

    public static event Action<string, ImageSource>? ThumbnailUpdated;

    static ThumbnailProvider()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int cx, int cy)
        {
            this.cx = cx;
            this.cy = cy;
        }
    }

    [Flags]
    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00000000,
        SIIGBF_BIGGERSIZEOK = 0x00000001,
        SIIGBF_MEMORYONLY = 0x00000002,
        SIIGBF_ICONONLY = 0x00000004,
        SIIGBF_THUMBNAILONLY = 0x00000008,
        SIIGBF_INCACHEONLY = 0x00000010
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            [In, MarshalAs(UnmanagedType.Struct)] SIZE size,
            [In] SIIGBF flags,
            [Out] out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    private static readonly Guid ShellItemImageFactoryGuid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    public static ImageSource GetThumbnail(string videoPath, int width = 480, int height = 270)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            return GetFallbackVisual(videoPath);

        // 1. In-memory LRU cache
        if (Cache.TryGet(videoPath, out var cached))
            return cached!;

        // 2. Persistent on-disk JPEG cache
        var diskCacheFile = GetDiskCachePath(videoPath);
        if (File.Exists(diskCacheFile))
        {
            var diskSource = LoadBitmapFromDisk(diskCacheFile, width);
            if (diskSource != null)
            {
                Cache.Set(videoPath, diskSource);
                _ = Task.Run(() => ExtractDominantColor(videoPath, diskSource));
                return diskSource;
            }
        }

        // 3. Queue asynchronous background generation
        QueueThumbnailExtraction(videoPath, width, height);

        // 4. Return modern luxury fallback card immediately (never a white document icon)
        var fallback = GetFallbackVisual(videoPath);
        return fallback;
    }

    private static string GetDiskCachePath(string videoPath)
    {
        try
        {
            var fi = new FileInfo(videoPath);
            var key = $"{videoPath}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";
            using var md5 = MD5.Create();
            var hash = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            return Path.Combine(CacheDir, $"{hash}.jpg");
        }
        catch
        {
            var fallbackKey = videoPath.ToLowerInvariant();
            using var md5 = MD5.Create();
            var hash = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(fallbackKey))).ToLowerInvariant();
            return Path.Combine(CacheDir, $"{hash}.jpg");
        }
    }

    private static BitmapImage? LoadBitmapFromDisk(string path, int decodeWidth)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.DecodePixelWidth = decodeWidth;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    public static void PrewarmThumbnails(IEnumerable<string> videoPaths)
    {
        Task.Run(() =>
        {
            foreach (var path in videoPaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;

                var cacheFile = GetDiskCachePath(path);
                if (!File.Exists(cacheFile))
                {
                    GenerateAndSaveThumbnail(path, cacheFile, 480, 270);
                }
            }
        });
    }

    private static void QueueThumbnailExtraction(string videoPath, int width, int height)
    {
        lock (_queueLock)
        {
            if (_inProgress.Contains(videoPath))
                return;
            _inProgress.Add(videoPath);
        }

        Task.Run(() =>
        {
            try
            {
                var cacheFile = GetDiskCachePath(videoPath);
                if (GenerateAndSaveThumbnail(videoPath, cacheFile, width, height))
                {
                    var source = LoadBitmapFromDisk(cacheFile, width);
                    if (source != null)
                    {
                        Cache.Set(videoPath, source);
                        ExtractDominantColor(videoPath, source);

                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            ThumbnailUpdated?.Invoke(videoPath, source);
                        });
                    }
                }
            }
            finally
            {
                lock (_queueLock)
                {
                    _inProgress.Remove(videoPath);
                }
            }
        });
    }

    private static bool GenerateAndSaveThumbnail(string videoPath, string outJpgPath, int width, int height)
    {
        if (!File.Exists(videoPath))
            return false;

        // Try Shell Thumbnail with SIIGBF_THUMBNAILONLY (never returns file icon)
        try
        {
            if (TryExtractShellThumbnail(videoPath, outJpgPath, width, height))
                return true;
        }
        catch { }

        // Robust video frame capture via STA MediaPlayer
        try
        {
            if (ExtractFrameWithMediaPlayer(videoPath, outJpgPath, width, height))
                return true;
        }
        catch { }

        return false;
    }

    private static bool TryExtractShellThumbnail(string videoPath, string outJpgPath, int width, int height)
    {
        bool success = false;
        var thread = new Thread(() =>
        {
            try
            {
                SHCreateItemFromParsingName(videoPath, IntPtr.Zero, ShellItemImageFactoryGuid, out var factory);
                if (factory != null)
                {
                    // CRITICAL: SIIGBF_THUMBNAILONLY (0x08) prevents returning generic file icon
                    var hr = factory.GetImage(new SIZE(width, height), SIIGBF.SIIGBF_THUMBNAILONLY, out var hBitmap);
                    if (hr == 0 && hBitmap != IntPtr.Zero)
                    {
                        try
                        {
                            var source = Imaging.CreateBitmapSourceFromHBitmap(
                                hBitmap,
                                IntPtr.Zero,
                                Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());

                            SaveBitmapSourceToJpeg(source, outJpgPath);
                            success = true;
                        }
                        finally
                        {
                            DeleteObject(hBitmap);
                        }
                    }
                }
            }
            catch { }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(2000);

        return success && File.Exists(outJpgPath);
    }

    private static bool ExtractFrameWithMediaPlayer(string videoPath, string outJpgPath, int width, int height)
    {
        var tcs = new TaskCompletionSource<bool>();
        var thread = new Thread(() =>
        {
            System.Windows.Media.MediaPlayer? player = null;
            try
            {
                player = new System.Windows.Media.MediaPlayer
                {
                    ScrubbingEnabled = true,
                    Volume = 0,
                    IsMuted = true
                };

                bool opened = false;
                player.MediaOpened += (s, e) =>
                {
                    opened = true;
                    // Seek past opening intro to avoid pure black starting frames
                    player.Position = TimeSpan.FromSeconds(2.5);
                    player.Play();
                };

                player.MediaFailed += (s, e) =>
                {
                    tcs.TrySetResult(false);
                };

                player.Open(new Uri(videoPath, UriKind.Absolute));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!opened && sw.ElapsedMilliseconds < 2500)
                {
                    Thread.Sleep(40);
                }

                if (!opened)
                {
                    player.Position = TimeSpan.FromSeconds(1.5);
                    player.Play();
                }

                // Brief pause to allow DirectShow / Media Foundation to decode the frame
                Thread.Sleep(500);
                player.Pause();

                var drawing = new System.Windows.Media.VideoDrawing
                {
                    Player = player,
                    Rect = new Rect(0, 0, width, height)
                };

                var group = new System.Windows.Media.DrawingGroup();
                group.Children.Add(drawing);

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawDrawing(group);
                }

                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);

                SaveBitmapSourceToJpeg(rtb, outJpgPath);
                tcs.TrySetResult(true);
            }
            catch
            {
                tcs.TrySetResult(false);
            }
            finally
            {
                try { player?.Close(); } catch { }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task.Wait(4500) && tcs.Task.Result;
    }

    private static void SaveBitmapSourceToJpeg(BitmapSource source, string outJpgPath)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(source));

        var dir = Path.GetDirectoryName(outJpgPath);
        if (dir != null) Directory.CreateDirectory(dir);

        var tempPath = outJpgPath + ".tmp";
        using (var fs = File.Create(tempPath))
        {
            encoder.Save(fs);
        }

        if (File.Exists(outJpgPath))
            File.Delete(outJpgPath);

        File.Move(tempPath, outJpgPath);
    }

    public static void TrimCacheIfNeeded(int maxMB = 350)
    {
        var usedMB = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);
        if (usedMB > maxMB)
        {
            Cache.Clear();
            GC.Collect(0, GCCollectionMode.Optimized);
        }
    }

    public static MediaColor GetDominantColor(string videoPath)
    {
        if (!string.IsNullOrWhiteSpace(videoPath) && ColorCache.TryGet(videoPath, out var col))
            return col;

        return MediaColor.FromRgb(0xC9, 0x99, 0x3A); // Luxury gold glow default
    }

    private static void ExtractDominantColor(string videoPath, BitmapSource source)
    {
        try
        {
            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            formatted.Freeze();

            var width = Math.Min(32, formatted.PixelWidth);
            var height = Math.Min(32, formatted.PixelHeight);
            if (width <= 0 || height <= 0) return;

            var stride = width * 4;
            var pixels = new byte[height * stride];

            formatted.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);

            long rTotal = 0, gTotal = 0, bTotal = 0;
            int count = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];

                if (r + g + b > 60)
                {
                    rTotal += r;
                    gTotal += g;
                    bTotal += b;
                    count++;
                }
            }

            if (count > 0)
            {
                var rAvg = (byte)Math.Clamp(rTotal / count, 30, 240);
                var gAvg = (byte)Math.Clamp(gTotal / count, 30, 240);
                var bAvg = (byte)Math.Clamp(bTotal / count, 30, 240);
                ColorCache.Set(videoPath, MediaColor.FromRgb(rAvg, gAvg, bAvg));
            }
        }
        catch
        {
            ColorCache.Set(videoPath, MediaColor.FromRgb(0xC9, 0x99, 0x3A));
        }
    }

    public static ImageSource GetFallbackVisual(string videoPath)
    {
        var dv = new DrawingVisual();
        var displayName = string.IsNullOrWhiteSpace(videoPath)
            ? "Wallpaper"
            : WallpaperLibrary.ToDisplayName(videoPath);

        using (var dc = dv.RenderOpen())
        {
            // Rich deep dark background
            var brush = new LinearGradientBrush(
                MediaColor.FromRgb(0x13, 0x16, 0x1F),
                MediaColor.FromRgb(0x0A, 0x0C, 0x10),
                new Point(0, 0),
                new Point(1, 1));
            dc.DrawRectangle(brush, null, new Rect(0, 0, 480, 270));

            // Atmospheric warm gold radiant ambient glow
            var auraBrush = new RadialGradientBrush(
                MediaColor.FromArgb(55, 0xC9, 0x99, 0x3A),
                Colors.Transparent)
            {
                Center = new Point(0.5, 0.45),
                RadiusX = 0.55,
                RadiusY = 0.55
            };
            dc.DrawRectangle(auraBrush, null, new Rect(0, 0, 480, 270));

            // Modern subtle play emblem
            var iconBrush = new SolidColorBrush(MediaColor.FromArgb(160, 0xFA, 0xFA, 0xFA));
            var iconPen = new System.Windows.Media.Pen(new SolidColorBrush(MediaColor.FromArgb(90, 0xC9, 0x99, 0x3A)), 1.5);
            dc.DrawEllipse(new SolidColorBrush(MediaColor.FromArgb(40, 0, 0, 0)), iconPen, new Point(240, 120), 28, 28);

            // Play triangle geometry
            var triGeometry = new StreamGeometry();
            using (var ctx = triGeometry.Open())
            {
                ctx.BeginFigure(new Point(235, 110), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(250, 120), isStroked: true, isSmoothJoin: false);
                ctx.LineTo(new Point(235, 130), isStroked: true, isSmoothJoin: false);
            }
            triGeometry.Freeze();
            dc.DrawGeometry(iconBrush, null, triGeometry);

            // Sleek subtitle text
            var typeface = new Typeface(new System.Windows.Media.FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var ft = new FormattedText(
                displayName,
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                14,
                new SolidColorBrush(MediaColor.FromRgb(0xE2, 0xE8, 0xF0)),
                96);

            ft.MaxTextWidth = 420;
            ft.TextAlignment = TextAlignment.Center;
            dc.DrawText(ft, new Point(30, 165));
        }

        var rtb = new RenderTargetBitmap(480, 270, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    // ── Thread-safe LRU caches ──────────────────────────────────────────────

    private sealed class LruImageCache
    {
        private readonly int _max;
        private readonly Dictionary<string, (ImageSource Source, int Tick)> _map =
            new(StringComparer.OrdinalIgnoreCase);
        private int _tick;
        private readonly object _lock = new();

        public LruImageCache(int maxSize) => _max = maxSize;

        public bool TryGet(string key, out ImageSource? value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var entry))
                {
                    _map[key] = (entry.Source, ++_tick);
                    value = entry.Source;
                    return true;
                }
                value = null;
                return false;
            }
        }

        public void Set(string key, ImageSource value)
        {
            lock (_lock)
            {
                if (_map.Count >= _max && !_map.ContainsKey(key))
                    Evict();
                _map[key] = (value, ++_tick);
            }
        }

        public void Clear()
        {
            lock (_lock) { _map.Clear(); }
        }

        private void Evict()
        {
            if (_map.Count == 0) return;
            var oldest = _map.MinBy(kv => kv.Value.Tick).Key;
            _map.Remove(oldest);
        }
    }

    private sealed class LruColorCache
    {
        private readonly int _max;
        private readonly Dictionary<string, (MediaColor Color, int Tick)> _map =
            new(StringComparer.OrdinalIgnoreCase);
        private int _tick;
        private readonly object _lock = new();

        public LruColorCache(int maxSize) => _max = maxSize;

        public bool TryGet(string key, out MediaColor value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var entry))
                {
                    _map[key] = (entry.Color, ++_tick);
                    value = entry.Color;
                    return true;
                }
                value = default;
                return false;
            }
        }

        public void Set(string key, MediaColor value)
        {
            lock (_lock)
            {
                if (_map.Count >= _max && !_map.ContainsKey(key))
                    Evict();
                _map[key] = (value, ++_tick);
            }
        }

        private void Evict()
        {
            if (_map.Count == 0) return;
            var oldest = _map.MinBy(kv => kv.Value.Tick).Key;
            _map.Remove(oldest);
        }
    }
}
