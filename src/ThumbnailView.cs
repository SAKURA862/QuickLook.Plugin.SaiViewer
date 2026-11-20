using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.SaiViewer
{
    /// <summary>预览面板：居中的缩略图 + 底部一行画布信息。</summary>
    internal sealed class ThumbnailView : Grid
    {
        private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x23)));
        private static readonly Brush CaptionBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0)));

        /// <summary>透明画布下面垫的棋盘格，避免整幅透明的图看起来像“没预览”。</summary>
        private static readonly Brush CheckerBrush = Frozen(CreateChecker());

        private static Brush CreateChecker()
        {
            var group = new DrawingGroup();
            var light = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40));
            var dark = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x34));
            light.Freeze();
            dark.Freeze();

            var rect = new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 16, 16)));
            var a = new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8)));
            var b = new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 8, 8, 8)));
            group.Children.Add(rect);
            group.Children.Add(a);
            group.Children.Add(b);

            var brush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 16, 16),
                ViewportUnits = BrushMappingMode.Absolute,
            };
            return brush;
        }

        public ThumbnailView(BitmapSource image, string caption)
        {
            Background = BackgroundBrush;

            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var imageHost = new Border
            {
                Margin = new Thickness(16, 16, 16, 8),
                SnapsToDevicePixels = true,
                Background = CheckerBrush,
            };

            var img = new Image
            {
                Source = image,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            imageHost.Child = img;

            SetRow(imageHost, 0);
            Children.Add(imageHost);

            if (!string.IsNullOrEmpty(caption))
            {
                var captionText = new TextBlock
                {
                    Text = caption,
                    Foreground = CaptionBrush,
                    FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 9),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520,
                };
                SetRow(captionText, 1);
                Children.Add(captionText);
            }
        }

        public static ThumbnailView CreateMessage(string message)
        {
            var view = new ThumbnailView(null, null);
            var text = new TextBlock
            {
                Text = message,
                Foreground = CaptionBrush,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24),
                TextAlignment = TextAlignment.Center,
            };
            SetRow(text, 0);
            view.Children.Add(text);
            return view;
        }

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
