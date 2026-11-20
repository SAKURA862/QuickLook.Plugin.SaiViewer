using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickLook.Common.Plugin;

namespace QuickLook.Plugin.SaiViewer
{
    /// <summary>
    ///     QuickLook 4.x 插件：预览 PaintTool SAI Ver.2 文件（.sai2）。
    ///     优先解出「intg」块里 1:1 的无损预览（dpcm），没有时退回文件内嵌的 jssf 缩略图。
    /// </summary>
    public class Plugin : IViewer
    {
        // 面板尺寸跟着屏幕走：大画布铺满工作区的 92%（和 QuickLook 预览 PNG 的感觉一致），
        // 小画布按原尺寸显示，最多放大 2.5 倍。
        private const double WorkAreaFraction = 0.92;
        private const double MaxUpscale = 2.5;
        private const double FooterHeight = 30.0;
        // ThumbnailView 里图片容器的 Margin：左右 16，上 16 下 8
        private const double ChromeWidth = 32.0;
        private const double ChromeHeight = 24.0;
        // 解码分辨率 = 面板显示尺寸的 1.3 倍（留出标题栏/缩放余量），并限制在合理区间
        private const double DecodeOversample = 1.3;
        private const int DecodeMinLongSide = 768;
        private const int DecodeMaxLongSide = 2048;

        private string _path;
        private Sai2Info _info;
        private string _error;
        private int _decodeLongSide = DecodeMinLongSide;

        public int Priority => 0;

        public void Init()
        {
            Diag.Log("Init");
        }

        public bool CanHandle(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            var ext = Path.GetExtension(path);
            if (ext.Equals(".sai2", StringComparison.OrdinalIgnoreCase))
            {
                bool ok = Sai2Thumbnail.IsSai2(path);
                Diag.Log("CanHandle " + ok + " " + path);
                return ok;
            }

            // .sai（SAI Ver.1）走另一套加密容器格式，尚未实现
            return false;
        }

        public void Prepare(string path, ContextObject context)
        {
            _path = path;
            _info = null;
            _error = null;

            Diag.Log("Prepare " + path);
            context.Title = Path.GetFileName(path);
            context.CanResize = true;
            context.PreferredSize = new Size(600, 800);

            try
            {
                _info = Sai2Thumbnail.ReadInfo(path);
                context.PreferredSize = PickSize(_info, out _decodeLongSide);

                if (!_info.HasIntg && !_info.HasJssfThumb)
                    _error = "文件里没有可用的预览数据。";
            }
            catch (Exception ex)
            {
                _error = "无法读取 SAI2 文件：" + ex.Message;
            }
        }

        public void View(string path, ContextObject context)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (_error != null)
                {
                    context.ViewerContent = ThumbnailView.CreateMessage(_error);
                    return;
                }

                BitmapSource image = null;
                string caption = null;

                if (_info.HasIntg)
                {
                    int w, h;
                    var pixels = Sai2Thumbnail.ExtractIntgScaled(path, _info, _decodeLongSide, out w, out h);
                    if (pixels != null && w > 0 && h > 0)
                    {
                        image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
                        image.Freeze();
                        caption = BuildCaption(_info, "完整画布");
                    }
                    else
                    {
                        Diag.Log("intg decode failed, falling back");
                    }
                }

                if (image == null && _info.HasJssfThumb)
                {
                    var jpeg = Sai2Thumbnail.ExtractJssfJpeg(path, _info);
                    if (jpeg != null && jpeg.Length > 0)
                    {
                        image = DecodeJpeg(jpeg);
                        caption = BuildCaption(_info, "内嵌缩略图");
                    }
                }

                if (image == null)
                {
                    context.ViewerContent = ThumbnailView.CreateMessage("预览数据无法解析。");
                    return;
                }

                context.ViewerContent = new ThumbnailView(image, caption);
                Diag.Log("View ok " + image.PixelWidth + "x" + image.PixelHeight + " in " + stopwatch.ElapsedMilliseconds + "ms");
            }
            catch (Exception ex)
            {
                Diag.Log("View failed: " + ex);
                context.ViewerContent = ThumbnailView.CreateMessage("解析失败：" + ex.Message);
            }
            finally
            {
                context.IsBusy = false;
            }
        }

        public void Cleanup()
        {
            _path = null;
            _info = null;
            _error = null;
        }

        private static Size PickSize(Sai2Info info, out int decodeLongSide)
        {
            decodeLongSide = DecodeMinLongSide;

            double w = info.CanvasWidth;
            double h = info.CanvasHeight;
            if (w <= 0 || h <= 0)
            {
                w = info.ThumbWidth > 0 ? info.ThumbWidth : 512;
                h = info.ThumbHeight > 0 ? info.ThumbHeight : 512;
            }

            // 图片区域最多占工作区的 92%（减掉窗口装饰的余量），小画布按原尺寸或最多 2.5 倍放大
            var work = SystemParameters.WorkArea;
            double maxImageWidth = Math.Max(240.0, work.Width * WorkAreaFraction - ChromeWidth - 24.0);
            double maxImageHeight = Math.Max(180.0, work.Height * WorkAreaFraction - ChromeHeight - FooterHeight - 24.0);

            double scale = Math.Min(maxImageWidth / w, maxImageHeight / h);
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1.0;
            scale = Math.Min(scale, MaxUpscale);

            double imageWidth = Math.Round(w * scale);
            double imageHeight = Math.Round(h * scale);

            // 解码按显示尺寸的 1.3 倍准备，留一点分辨率余量又不过分开销
            double wanted = Math.Max(imageWidth, imageHeight) * DecodeOversample;
            if (wanted < DecodeMinLongSide) wanted = DecodeMinLongSide;
            if (wanted > DecodeMaxLongSide) wanted = DecodeMaxLongSide;
            decodeLongSide = (int)Math.Ceiling(wanted);

            return new Size(imageWidth + ChromeWidth, imageHeight + ChromeHeight + FooterHeight);
        }

        private static string BuildCaption(Sai2Info info, string kind)
        {
            var w = info.CanvasWidth;
            var h = info.CanvasHeight;

            // 小画布的面板很窄，长标题会挤掉图片，只留尺寸
            if (Math.Max(w, h) <= 256)
                return w + "×" + h;

            return w + " × " + h + " px　·　SAI2 " + kind;
        }

        private static BitmapSource DecodeJpeg(byte[] jpeg)
        {
            using (var ms = new MemoryStream(jpeg))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.StreamSource = ms;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
    }
}
