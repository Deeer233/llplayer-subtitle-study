using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OffscreenUiQa;

internal static partial class Program
{
    private sealed class Surface : IDisposable
    {
        private readonly Window _window;
        private readonly FrameworkElement _content;
        private readonly double _width, _height, _scale;
        internal Border Root { get; }

        internal Surface(Window window, double width, double height, double scale)
        {
            AssertNoWindow(window);
            _window = window;
            _content = (FrameworkElement)window.Content;
            _width = width;
            _height = height;
            _scale = scale;
            window.Content = null;
            Root = new Border
            {
                Language = window.Language,
                FlowDirection = window.FlowDirection,
                UseLayoutRounding = window.UseLayoutRounding,
                SnapsToDevicePixels = window.SnapsToDevicePixels,
                Background = window.Background ?? Brushes.White,
                ClipToBounds = true
            };
            TextElement.SetFontFamily(Root, window.FontFamily);
            TextElement.SetFontSize(Root, window.FontSize);
            TextElement.SetFontWeight(Root, window.FontWeight);
            TextElement.SetFontStyle(Root, window.FontStyle);
            TextElement.SetFontStretch(Root, window.FontStretch);
            TextElement.SetForeground(Root, window.Foreground);
            Root.Child = _content;
            VisualTreeHelper.SetRootDpi(Root, new DpiScale(scale, scale));
        }

        internal void Layout()
        {
            Require(PresentationSource.FromVisual(Root) is null, "Offscreen tree was attached to a presentation source.");
            Root.Measure(new Size(_width, _height));
            Root.Arrange(new Rect(0, 0, _width, _height));
            Root.UpdateLayout();
            Require(Near(Root.ActualWidth, _width, 1 / _scale) && Near(Root.ActualHeight, _height, 1 / _scale),
                $"Unexpected DIP area: {Root.ActualWidth}x{Root.ActualHeight}.");
            Require(Near(VisualTreeHelper.GetDpi(_content).DpiScaleX, _scale) && Near(VisualTreeHelper.GetDpi(_content).DpiScaleY, _scale),
                "The detached product content did not inherit the requested WPF DPI.");
        }

        internal ImageResult Snapshot(string filename)
        {
            Layout();
            var pixelsX = (int)Math.Ceiling(_width * _scale);
            var pixelsY = (int)Math.Ceiling(_height * _scale);
            var dpi = 96 * _scale;
            var bitmap = new RenderTargetBitmap(pixelsX, pixelsY, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(Root);
            var path = Path.Combine(RunDirectory, filename);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Open(path, FileMode.CreateNew)) encoder.Save(output);
            using var input = File.OpenRead(path);
            var decoded = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames.Single();
            Require(decoded.PixelWidth == pixelsX && decoded.PixelHeight == pixelsY && Near(decoded.DpiX, dpi, 0.1) && Near(decoded.DpiY, dpi, 0.1),
                $"Unexpected PNG size or DPI in {filename}.");
            var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
            var stride = pixelsX * 4;
            var pixels = new byte[stride * pixelsY];
            converted.CopyPixels(pixels, stride, 0);
            var shades = new HashSet<uint>();
            for (var y = 2; y < pixelsY - 2; y += 5)
                for (var x = 2; x < pixelsX - 2; x += 5)
                    shades.Add(BitConverter.ToUInt32(pixels, y * stride + x * 4));
            Require(shades.Count > 8 && shades.Any(value => (value >> 24) == 255), $"Blank or transparent render: {filename}.");
            var contentDpi = VisualTreeHelper.GetDpi(_content);
            var result = new ImageResult(path, _width, _height, _scale, pixelsX, pixelsY, decoded.DpiX, decoded.DpiY,
                contentDpi.PixelsPerInchX, contentDpi.PixelsPerInchY);
            Images.Add(result);
            return result;
        }

        public void Dispose()
        {
            Root.Child = null;
            _window.Content = _content;
            AssertNoWindow(_window);
        }
    }

    private static string TestHost()
    {
        using var holder = new NeverShown(new Window
        {
            Background = Brushes.White,
            UseLayoutRounding = true,
            Content = new TextBlock { Text = "Detached WPF content / DPI probe", Margin = new Thickness(20) }
        });
        using var surface = new Surface(holder.Window, SmallWidth, SmallHeight, 2.35);
        var image = surface.Snapshot("self-test-235.png");
        return $"{image.WidthPx}x{image.HeightPx} PNG from detached WPF, no HWND.";
    }

    private static bool Near(double first, double second, double tolerance = .01) => Math.Abs(first - second) <= tolerance;

    private static T Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T result) return result;
        throw new InvalidOperationException($"Expected a {typeof(T).Name} visual ancestor of {element.GetType().Name}.");
    }

    private static T Descendant<T>(DependencyObject element) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T match) return match;
            if (Find<T>(child) is { } found) return found;
        }
        throw new InvalidOperationException($"Expected a {typeof(T).Name} inside {element.GetType().Name}.");
    }

    private static T? Find<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T typed) return typed;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (Find<T>(VisualTreeHelper.GetChild(element, i)) is { } match) return match;
        return null;
    }

    private static Rect InView(Surface surface, FrameworkElement element, string name)
    {
        var root = surface.Root;
        Require(element.Visibility == Visibility.Visible && element.ActualWidth > 2 && element.ActualHeight > 2 && root.IsAncestorOf(element),
            $"{name} has no laid-out visible slot ({element.ActualWidth:0.##}x{element.ActualHeight:0.##}).");
        var rect = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        var visible = rect;
        for (Visual? node = element; node is not null && node != root; node = VisualTreeHelper.GetParent(node) as Visual)
            if (VisualTreeHelper.GetClip(node) is { } clip)
                visible.Intersect(node.TransformToAncestor(root).TransformBounds(clip.Bounds));
        visible.Intersect(new Rect(root.RenderSize));
        Require(!visible.IsEmpty && rect.Width - visible.Width < 1.5 && rect.Height - visible.Height < 1.5,
            $"{name} is clipped: slot {Format(rect)}, visible {Format(visible)}.");
        return rect;
    }

    private static string Format(Rect rect) => rect.IsEmpty ? "empty" : string.Create(CultureInfo.InvariantCulture, $"({rect.X:0.#},{rect.Y:0.#}) {rect.Width:0.#}x{rect.Height:0.#}");

    private static void NoOverlap(IEnumerable<Rect> rects, string group)
    {
        var all = rects.ToArray();
        for (var i = 0; i < all.Length; i++)
            for (var j = i + 1; j < all.Length; j++)
            {
                var overlap = Rect.Intersect(all[i], all[j]);
                Require(overlap.IsEmpty || overlap.Width < 1 || overlap.Height < 1,
                    $"{group} overlap: {Format(all[i])} and {Format(all[j])}.");
            }
    }
}
