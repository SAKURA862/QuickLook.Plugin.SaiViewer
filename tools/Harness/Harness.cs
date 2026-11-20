using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickLook.Common.Plugin;

namespace SaiViewerHarness
{
    /// <summary>
    /// 离线验证器：模拟 QuickLook 宿主，加载插件、调用 Prepare/View，把 ViewerContent 渲染成 PNG。
    /// 用法：SaiViewerHarness &lt;文件...&gt; -o &lt;输出目录&gt;
    /// </summary>
    internal static class Harness
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string outDir = @"D:\data\sai\out";
            var files = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-o" && i + 1 < args.Length) outDir = args[++i];
                else files.Add(args[i]);
            }
            Directory.CreateDirectory(outDir);

            int fail = 0;
            foreach (var file in files)
            {
                try
                {
                    if (!RunOne(file, outDir)) fail++;
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.WriteLine("ERR   " + Path.GetFileName(file) + "  " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            Console.WriteLine("---- fail=" + fail);
            return fail == 0 ? 0 : 1;
        }

        private static bool RunOne(string file, string outDir)
        {
            var context = new ContextObject();
            var plugin = new QuickLook.Plugin.SaiViewer.Plugin();
            plugin.Init();

            bool can = plugin.CanHandle(file);
            plugin.Prepare(file, context);
            plugin.View(file, context);

            var content = context.ViewerContent as FrameworkElement;
            if (content == null)
            {
                Console.WriteLine("NOCONTENT " + Path.GetFileName(file));
                return false;
            }

            double w = Math.Max(context.PreferredSize.Width, 40);
            double h = Math.Max(context.PreferredSize.Height, 40);
            content.Width = w;
            content.Height = h;
            content.Measure(new Size(w, h));
            content.Arrange(new Rect(0, 0, w, h));
            content.UpdateLayout();

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".panel.png");
            using (var stream = File.Create(outPath))
                encoder.Save(stream);

            plugin.Cleanup();

            Console.WriteLine("OK    " + Path.GetFileName(file) + "  canHandle=" + can
                + "  size=" + w + "x" + h + "  -> " + Path.GetFileName(outPath));
            return true;
        }
    }
}
