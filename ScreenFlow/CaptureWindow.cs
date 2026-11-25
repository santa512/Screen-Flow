using System;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ScreenFlow;
public sealed class CaptureWindow : Window
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    public int SelectedX { get; private set; }
    public int SelectedY { get; private set; }
    public string? ImageBase64 { get; private set; }
    private readonly System.Drawing.Bitmap screenshot;
    private readonly Canvas canvas = new();
    private readonly Rectangle selection = new() { StrokeThickness = 2, Fill = new SolidColorBrush(SystemColors.AccentColor) { Opacity = .15 } };
    private System.Windows.Point start;
    private bool dragging;
    public CaptureWindow(System.Drawing.Bitmap screenshot, bool pointMode)
    {
        this.screenshot = screenshot;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false;
        Cursor = Cursors.Cross; Background = Brushes.Black;
        selection.SetResourceReference(Rectangle.StrokeProperty, "AccentFillColorDefaultBrush");
        var grid = new Grid(); Content = grid;
        using var stream = new MemoryStream(Vision.Png(screenshot));
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        grid.Children.Add(new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.Fill });
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(65, 0, 0, 0)), IsHitTestVisible = false });
        grid.Children.Add(canvas); canvas.Children.Add(selection);
        grid.Children.Add(WindowsUi.Resource(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(22, 14, 22, 14),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 0, 0), IsHitTestVisible = false,
            Child = new TextBlock { Text = pointMode ? "Click a target point  ·  Esc to cancel" : "Drag around a button or icon  ·  Esc to cancel", FontSize = 18 } }, Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush"));
        SourceInitialized += (_, _) => {
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x40);
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
        MouseLeftButtonDown += (_, e) => {
            start = e.GetPosition(canvas);
            if (pointMode) { SetPoint(start); DialogResult = true; return; }
            dragging = true; CaptureMouse(); Canvas.SetLeft(selection, start.X); Canvas.SetTop(selection, start.Y); selection.Width = selection.Height = 0;
        };
        MouseMove += (_, e) => {
            if (!dragging) return;
            var end = e.GetPosition(canvas);
            Canvas.SetLeft(selection, Math.Min(start.X, end.X)); Canvas.SetTop(selection, Math.Min(start.Y, end.Y));
            selection.Width = Math.Abs(end.X - start.X); selection.Height = Math.Abs(end.Y - start.Y);
        };
        MouseLeftButtonUp += (_, e) => {
            if (!dragging) return;
            dragging = false; ReleaseMouseCapture();
            var end = e.GetPosition(canvas);
            int x = Math.Clamp((int)(Math.Min(start.X, end.X) * screenshot.Width / canvas.ActualWidth), 0, screenshot.Width - 1);
            int y = Math.Clamp((int)(Math.Min(start.Y, end.Y) * screenshot.Height / canvas.ActualHeight), 0, screenshot.Height - 1);
            int w = Math.Min(screenshot.Width - x, (int)(Math.Abs(end.X - start.X) * screenshot.Width / canvas.ActualWidth));
            int h = Math.Min(screenshot.Height - y, (int)(Math.Abs(end.Y - start.Y) * screenshot.Height / canvas.ActualHeight));
            if (w < 8 || h < 8) { selection.Width = selection.Height = 0; return; }
            using var crop = screenshot.Clone(new System.Drawing.Rectangle(x, y, w, h), System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            ImageBase64 = Convert.ToBase64String(Vision.Png(crop)); DialogResult = true;
        };
    }
    private void SetPoint(System.Windows.Point point) {
        var origin = System.Windows.Forms.SystemInformation.VirtualScreen;
        SelectedX = origin.Left + Math.Clamp((int)(point.X * screenshot.Width / canvas.ActualWidth), 0, screenshot.Width - 1);
        SelectedY = origin.Top + Math.Clamp((int)(point.Y * screenshot.Height / canvas.ActualHeight), 0, screenshot.Height - 1);
    }
}
