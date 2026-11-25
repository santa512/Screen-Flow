using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: IconAssets <source.svg> <assets-directory>");
        var svg = XDocument.Load(args[0]);
        XNamespace svgNs = "http://www.w3.org/2000/svg";
        var path = svg.Root!.Element(svgNs + "path") ?? throw new InvalidDataException("Expected one SVG path.");
        string data = path.Attribute("d")!.Value;
        var geometry = Geometry.Parse("F0 " + data);
        var brush = (Brush)new BrushConverter().ConvertFromString(path.Attribute("fill")!.Value)!;
        string output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        string sourceCopy = Path.Combine(output, "ScreenFlow.svg");
        if (!string.Equals(Path.GetFullPath(args[0]), sourceCopy, StringComparison.OrdinalIgnoreCase)) File.Copy(args[0], sourceCopy, true);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var frames = new List<byte[]>();
        foreach (int size in sizes) {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) {
                drawing.PushTransform(new ScaleTransform(size / 24.0, size / 24.0));
                drawing.DrawGeometry(brush, null, geometry);
                drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream(); encoder.Save(png); frames.Add(png.ToArray());
            if (size == 256) File.WriteAllBytes(Path.Combine(output, "ScreenFlow.png"), frames[^1]);
        }
        using (var stream = File.Create(Path.Combine(output, "ScreenFlow.ico")))
        using (var icon = new BinaryWriter(stream)) {
            icon.Write((ushort)0); icon.Write((ushort)1); icon.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++) {
                icon.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); icon.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                icon.Write((byte)0); icon.Write((byte)0); icon.Write((ushort)1); icon.Write((ushort)32);
                icon.Write(frames[i].Length); icon.Write(offset); offset += frames[i].Length;
            }
            foreach (var frame in frames) icon.Write(frame);
        }
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = new XElement(wpf + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x),
            new XElement(wpf + "Geometry", new XAttribute(x + "Key", "AppLogoGeometry"), "F0 " + data),
            new XElement(wpf + "DrawingImage", new XAttribute(x + "Key", "AppLogo"),
                new XElement(wpf + "DrawingImage.Drawing", new XElement(wpf + "GeometryDrawing",
                    new XAttribute("Brush", "{DynamicResource AccentTextFillColorPrimaryBrush}"),
                    new XAttribute("Geometry", "{StaticResource AppLogoGeometry}")))));
        File.WriteAllText(Path.Combine(output, "Brand.xaml"), resources.ToString());
        Console.WriteLine($"Created {sizes.Length} icon sizes, PNG preview, and scalable WPF artwork in {output}");
    }
}
