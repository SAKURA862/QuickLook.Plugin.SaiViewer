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
        private const double MaxLongSide = 900.0;
        private const double MaxUpscale = 2.5;
        private const double FooterHeight = 30.0;
        private const int DecodeLongSide = 1600;

        private string _path;
        private Sai2Info _info;
        private string _error;

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
                context.PreferredSize = PickSize(_info);

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
                    var pixels = Sai2Thumbnail.ExtractIntgScaled(path, _info, DecodeLongSide, out w, out h);
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

        private static Size PickSize(Sai2Info info)
        {
            double w = info.CanvasWidth;
            double h = info.CanvasHeight;
            if (w <= 0 || h <= 0)
            {
                w = info.ThumbWidth > 0 ? info.ThumbWidth : 512;
                h = info.ThumbHeight > 0 ? info.ThumbHeight : 512;
            }

            double longer = Math.Max(w, h);
            double scale = Math.Min(MaxLongSide / longer, MaxUpscale);
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;

            double panelWidth = Math.Round(w * scale);
            double panelHeight = Math.Round(h * scale) + FooterHeight;

            // 别比屏幕工作区还大（留一点边距给任务栏和窗口装饰）
            var work = SystemParameters.WorkArea;
            var maxWidth = Math.Max(240.0, work.Width * 0.92);
            var maxHeight = Math.Max(240.0, work.Height * 0.92);
            if (panelWidth > maxWidth || panelHeight > maxHeight)
            {
                var fit = Math.Min(maxWidth / panelWidth, maxHeight / panelHeight);
                panelWidth = Math.Round(panelWidth * fit);
                panelHeight = Math.Round(panelHeight * fit);
            }

            return new Size(panelWidth, panelHeight);
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
