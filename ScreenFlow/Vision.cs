using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using OpenCvSharp;

namespace ScreenFlow;
public sealed record MatchResult(double Score, int X, int Y, int Width, int Height);
public static class Vision
{
    public static Bitmap Capture()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        try { using var g = Graphics.FromImage(bitmap); g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size); return bitmap; }
        catch { bitmap.Dispose(); throw; }
    }
    public static byte[] Png(Bitmap bitmap) { using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray(); }
    public static MatchResult? Find(Mat screen, Mat template, bool multiScale, CancellationToken token = default)
    {
        if (template.Empty() || template.Width < 8 || template.Height < 8) throw new ArgumentException("Capture an image at least 8 × 8 pixels.");
        // Flat templates can produce perfect but meaningless normalized correlation scores.
        Cv2.MeanStdDev(template, out _, out var deviation);
        if (deviation.Val0 + deviation.Val1 + deviation.Val2 < 3) throw new ArgumentException("The target is too plain. Include text, an icon, or an edge in the capture.");
        MatchResult? best = null;
        foreach (double scale in multiScale ? new[] { 1.0, .8, .9, 1.1, 1.25, 1.5 } : new[] { 1.0 }) {
            token.ThrowIfCancellationRequested();
            using var resized = new Mat();
            Cv2.Resize(template, resized, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Linear);
            if (resized.Width > screen.Width || resized.Height > screen.Height) continue;
            using var result = new Mat();
            Cv2.MatchTemplate(screen, resized, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double score, out _, out var point);
            if (double.IsFinite(score) && (best == null || score > best.Score)) best = new(score, point.X, point.Y, resized.Width, resized.Height);
        }
        return best;
    }
    public static MatchResult? FindOnScreen(string base64, bool multiScale, CancellationToken token)
    {
        using var bitmap = Capture();
        using var screen = Cv2.ImDecode(Png(bitmap), ImreadModes.Color);
        using var template = Cv2.ImDecode(Convert.FromBase64String(base64), ImreadModes.Color);
        var found = Find(screen, template, multiScale, token);
        var origin = System.Windows.Forms.SystemInformation.VirtualScreen;
        return found == null ? null : found with { X = found.X + origin.Left, Y = found.Y + origin.Top };
    }
}
