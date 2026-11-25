using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ScreenFlow;
public partial class MainWindow : Window
{
    private Workflow flow = new();
    private Step? editing;
    private string? filePath;
    private bool dirty, loading = true, selecting;
    private CancellationTokenSource? cancellation;
    private readonly List<Action> commits = new();
    private IntPtr handle;
    private readonly DispatcherTimer stopTimer;
    private readonly DispatcherTimer browserTimer;
    private readonly BrowserTracker browserTracker = new();
    private PracticeWindow? practice;
    public MainWindow()
    {
        InitializeComponent();
        ThemePicker.SelectedValue = Appearance.Selected;
        BuildLibrary();
        LoadFlow(new Workflow(), null);
        AppendLog("Ready. Add an action, or load the URL handoff example.");
        SourceInitialized += (_, _) => {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowHook);
        };
        stopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        stopTimer.Tick += (_, _) => { if (cancellation != null && NativeInput.GetAsyncKeyState(0x77) < 0) cancellation.Cancel(); };
        browserTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        browserTimer.Tick += (_, _) => browserTracker.Observe();
        browserTimer.Start();
        Closing += OnClosing;
        StateChanged += (_, _) => {
            bool maximized = WindowState == WindowState.Maximized;
            MaximizeWindowButton.Content = maximized ? "\uE923" : "\uE922";
            MaximizeWindowButton.ToolTip = maximized ? "Restore" : "Maximize";
            System.Windows.Automation.AutomationProperties.SetName(MaximizeWindowButton, maximized ? "Restore window" : "Maximize window");
        };
        Closed += (_, _) => { NativeInput.UnregisterHotKey(handle, 1); stopTimer.Stop(); browserTimer.Stop(); practice?.Close(); };
    }
    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x312 && wParam.ToInt32() == 1) { cancellation?.Cancel(); handled = true; }
        return IntPtr.Zero;
    }
    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private void BuildLibrary()
    {
        foreach (var group in new[] {
            ("Browser", new[] { ActionKind.FocusBrowser, ActionKind.NewBrowserTab }),
            ("Mouse", new[] { ActionKind.Click, ActionKind.DoubleClick, ActionKind.RightClick, ActionKind.MoveMouse, ActionKind.Scroll }),
            ("Keyboard and timing", new[] { ActionKind.TypeText, ActionKind.Hotkey, ActionKind.Delay }),
            ("Computer vision", new[] { ActionKind.ClickImage, ActionKind.WaitForImage }),
            ("Data", new[] { ActionKind.CopyUrl, ActionKind.ReadClipboard, ActionKind.SetVariable }) }) {
            ActionLibrary.Children.Add(WindowsUi.Secondary(new TextBlock { Text = group.Item1, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 14, 0, 7) }));
            foreach (var kind in group.Item2) {
                var button = new Button { Content = WindowsUi.Label(Catalog.Icon(kind), Catalog.Label(kind)),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0), Padding = new Thickness(10, 9, 4, 9), Margin = new Thickness(0, 0, 2, 2), ToolTip = Catalog.Label(kind) };
                System.Windows.Automation.AutomationProperties.SetName(button, "Add " + Catalog.Label(kind));
                button.Click += (_, _) => AddStep(kind);
                ActionLibrary.Children.Add(button);
            }
        }
    }
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ThemePicker.SelectedValue is not string selection) return;
        bool saved = Appearance.Apply(selection);
        StatusText.Text = saved ? $"Appearance: {selection}" : $"Appearance: {selection} · preference could not be saved";
    }
    private void MarkDirty() { if (loading) return; dirty = true; UpdateSummary(); }
    private void UpdateSummary()
    {
        DirtyLabel.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        StepCount.Text = $"{flow.Steps.Count} actions · {flow.Steps.Count(s => s.Enabled)} enabled";
        EmptyState.Visibility = flow.Steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Title = "ScreenFlow — " + flow.Name + (dirty ? " *" : "");
        UpdateActionButtons();
    }
    private void UpdateActionButtons()
    {
        int index = StepsList.SelectedIndex;
        bool selected = cancellation == null && index >= 0 && index < flow.Steps.Count;
        MoveUpButton.IsEnabled = selected && index > 0;
        MoveDownButton.IsEnabled = selected && index < flow.Steps.Count - 1;
        DuplicateButton.IsEnabled = RemoveButton.IsEnabled = selected;
    }
    private void LoadFlow(Workflow workflow, string? path)
    {
        loading = selecting = true;
        editing = null;
        flow = workflow; filePath = path;
        WorkflowName.Text = flow.Name;
        RepeatInput.Text = flow.Repeat.ToString(); CountdownInput.Text = flow.StartDelaySeconds.ToString();
        StepsList.ItemsSource = flow.Steps;
        StepsList.SelectedIndex = flow.Steps.Count == 0 ? -1 : 0;
        editing = StepsList.SelectedItem as Step;
        selecting = loading = false;
        dirty = false; BuildEditor(); UpdateSummary();
        StatusText.Text = path == null ? "Ready to build" : "Opened " + Path.GetFileName(path);
    }
    private void AddStep(ActionKind kind)
    {
        if (cancellation != null || !CommitEditor()) return;
        var step = new Step { Kind = kind, Name = Catalog.Label(kind) };
        if (kind == ActionKind.Delay) step.DelayMs = 1000;
        flow.Steps.Add(step); StepsList.SelectedItem = step; StepsList.ScrollIntoView(step); MarkDirty();
    }
    private void Steps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (selecting || loading) return;
        if (!CommitEditor()) { selecting = true; StepsList.SelectedItem = editing; selecting = false; return; }
        editing = StepsList.SelectedItem as Step; BuildEditor(); UpdateActionButtons();
    }
    private TextBox Field(string label, string value, Action<string> commit, bool multiline = false)
    {
        EditorFields.Children.Add(WindowsUi.Secondary(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 12, 0, 6) }));
        var box = new TextBox { Text = value, MinHeight = multiline ? 94 : 35, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = multiline, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) => MarkDirty();
        commits.Add(() => commit(box.Text)); EditorFields.Children.Add(box); return box;
    }
    private void Note(string text) => EditorFields.Children.Add(WindowsUi.Secondary(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12,
        Margin = new Thickness(0, 8, 0, 2) }));
    private void EditorButton(string glyph, string title, RoutedEventHandler click)
    {
        var button = new Button { Content = WindowsUi.Label(glyph, title), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        button.Click += click; EditorFields.Children.Add(button);
    }
    private static int Integer(string value, string label, int min, int max)
    {
        if (!int.TryParse(value, out int number) || number < min || number > max) throw new ArgumentException($"{label}: enter a whole number from {min:N0} to {max:N0}.");
        return number;
    }
    private void BuildEditor()
    {
        EditorFields.Children.Clear(); commits.Clear(); EditorError.Text = "";
        ApplyButton.Visibility = editing == null ? Visibility.Collapsed : Visibility.Visible;
        if (editing == null) { EditorTitle.Text = "Choose an action"; EditorHint.Text = "Select a workflow step to customize it."; return; }
        var step = editing;
        EditorTitle.Text = Catalog.Label(step.Kind);
        EditorHint.Text = step.Kind switch {
            ActionKind.FocusBrowser => "Finds the foreground browser, or your most recently used browser, and brings it to the front.",
            ActionKind.NewBrowserTab => "Finds and focuses the browser, verifies focus, then sends Ctrl+T using keyboard input.",
            ActionKind.ClickImage or ActionKind.WaitForImage => "Find a captured screen region using OpenCV template matching.",
            ActionKind.CopyUrl => "Focuses the address bar with Ctrl+L, copies it, and stores the URL in a variable.",
            ActionKind.TypeText => "Sends text to the focused field using real keyboard input.",
            ActionKind.Hotkey => "Press a key or keyboard shortcut in the focused application.",
            _ => "Customize this action. Changes apply when you switch steps, save, or run."
        };
        Field("Action name", step.Name, v => step.Name = v);
        if (step.Kind is ActionKind.FocusBrowser or ActionKind.NewBrowserTab) {
            Note("Uses an open Octo browser profile (Octium), Chrome, Edge, Firefox, Brave, Vivaldi, Opera, or Chromium window. A minimized window is restored. Open the Octo profile first; the profile manager is not a browser window.");
            Note("The current browser takes priority, followed by the last-used browser, then the frontmost available browser. If Windows blocks focus, execution stops before sending Ctrl+T.");
        }
        if (step.Kind is ActionKind.Click or ActionKind.DoubleClick or ActionKind.RightClick or ActionKind.MoveMouse) {
            Field("Screen X (pixels)", step.X.ToString(), v => step.X = Integer(v, "X", -100000, 100000));
            Field("Screen Y (pixels)", step.Y.ToString(), v => step.Y = Integer(v, "Y", -100000, 100000));
            EditorButton("\uE8B0", "Pick point on screen", PickPoint_Click);
            Note("Coordinates use physical desktop pixels across all displays. Use a visual target when a button may move.");
        }
        if (step.Kind == ActionKind.Scroll) {
            Field("Wheel notches", step.Amount.ToString(), v => step.Amount = Integer(v, "Wheel notches", -1000, 1000));
            Note("Negative scrolls down; positive scrolls up. Position the mouse over the scrollable area first.");
        }
        if (step.Kind is ActionKind.TypeText or ActionKind.SetVariable) {
            Field("Text", step.Text, v => step.Text = v, true);
            Note("Insert a value with {{url}} or {{your_variable}}. Text is typed literally; newlines press Enter and tabs press Tab.");
        }
        if (step.Kind == ActionKind.Hotkey) {
            Field("Shortcut", step.Keys, v => { NativeInput.ParseKeys(v); step.Keys = v; });
            Note("Examples: Ctrl+Tab · Ctrl+Shift+Tab · Alt+Tab · Ctrl+A · Enter · Escape");
        }
        if (step.Kind is ActionKind.CopyUrl or ActionKind.ReadClipboard or ActionKind.SetVariable) {
            Field("Variable name", step.Variable, v => { if (!Variables.ValidName(v)) throw new ArgumentException("Use a variable name such as url or item_1."); step.Variable = v; });
            Note("Values remain available to later actions and repeat cycles during this run.");
        }
        if (step.Kind is ActionKind.ClickImage or ActionKind.WaitForImage) {
            if (!string.IsNullOrEmpty(step.ImageBase64)) {
                try {
                    using var stream = new MemoryStream(Convert.FromBase64String(step.ImageBase64));
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                    EditorFields.Children.Add(WindowsUi.Resource(new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(15), Margin = new Thickness(0, 10, 0, 0),
                        Child = new System.Windows.Controls.Image { Source = bitmap, MaxHeight = 115, Stretch = Stretch.Uniform } }, Border.BackgroundProperty, "ControlAltFillColorSecondaryBrush"));
                    Note($"Embedded target · {bitmap.PixelWidth} × {bitmap.PixelHeight} px");
                } catch { Note("Image could not be loaded. Capture a new target."); }
            }
            EditorButton("\uE722", "Capture visual target", CaptureImage_Click);
            Field("Match confidence (0.50–1.00)", step.Confidence.ToString("0.00", CultureInfo.InvariantCulture), v => {
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < .5 || number > 1)
                    throw new ArgumentException("Confidence must be between 0.50 and 1.00, using a decimal point.");
                step.Confidence = number;
            });
            Field("Wait timeout (ms)", step.TimeoutMs.ToString(), v => step.TimeoutMs = Integer(v, "Timeout", 100, 3600000));
            var check = new CheckBox { Content = "Search common image scales", IsChecked = step.MultiScale };
            check.Checked += (_, _) => MarkDirty(); check.Unchecked += (_, _) => MarkDirty();
            commits.Add(() => step.MultiScale = check.IsChecked == true); EditorFields.Children.Add(check);
            Note("Capture a distinctive button or icon. Keep browser zoom consistent. Image click targets the center; a timeout stops the workflow.");
        }
        Field(step.Kind == ActionKind.Delay ? "Wait duration (ms)" : "Pause after action (ms)", step.DelayMs.ToString(), v => step.DelayMs = Integer(v, "Delay", 0, 3600000));
    }
    private bool CommitEditor()
    {
        try { foreach (var commit in commits) commit(); editing?.Refresh(); EditorError.Text = ""; return true; }
        catch (Exception ex) { EditorError.Text = ex.Message; return false; }
    }
    private void Apply_Click(object sender, RoutedEventArgs e) { if (CommitEditor()) { MarkDirty(); StatusText.Text = "Action updated"; } }
    private void WorkflowName_Changed(object sender, TextChangedEventArgs e) { if (loading) return; flow.Name = WorkflowName.Text; MarkDirty(); }
    private void Settings_Changed(object sender, TextChangedEventArgs e) => MarkDirty();
    private void Enabled_Changed(object sender, RoutedEventArgs e) => MarkDirty();
    private void Up_Click(object sender, RoutedEventArgs e) => MoveStep(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => MoveStep(1);
    private void MoveStep(int delta) {
        if (cancellation != null || !CommitEditor()) return;
        int i = StepsList.SelectedIndex, next = i + delta;
        if (i < 0 || next < 0 || next >= flow.Steps.Count) return;
        selecting = true; flow.Steps.Move(i, next); StepsList.SelectedIndex = next; selecting = false; MarkDirty();
    }
    private void Duplicate_Click(object sender, RoutedEventArgs e) {
        if (cancellation != null || editing == null || !CommitEditor()) return;
        var copy = editing.Clone(); copy.Name += " (copy)";
        flow.Steps.Insert(StepsList.SelectedIndex + 1, copy); StepsList.SelectedItem = copy; MarkDirty();
    }
    private void Remove_Click(object sender, RoutedEventArgs e) {
        if (cancellation != null || editing == null) return;
        int i = StepsList.SelectedIndex; selecting = true; flow.Steps.Remove(editing);
        StepsList.SelectedIndex = Math.Min(i, flow.Steps.Count - 1); editing = StepsList.SelectedItem as Step; selecting = false;
        BuildEditor(); MarkDirty();
    }
    private bool CommitWorkflow()
    {
        if (!CommitEditor()) return false;
        try {
            flow.Repeat = Integer(RepeatInput.Text, "Repeat cycles", 1, 10000);
            flow.StartDelaySeconds = Integer(CountdownInput.Text, "Start delay", 1, 60);
            flow.Name = string.IsNullOrWhiteSpace(WorkflowName.Text) ? "Untitled workflow" : WorkflowName.Text;
            return true;
        } catch (Exception ex) { EditorError.Text = ex.Message; return false; }
    }
    private bool Save(bool choosePath = false)
    {
        if (!CommitWorkflow()) return false;
        string? destination = filePath;
        if (choosePath || destination == null) {
            var dialog = new SaveFileDialog { Title = "Save workflow", Filter = "ScreenFlow workflow (*.screenflow.json)|*.screenflow.json", DefaultExt = ".screenflow.json",
                FileName = filePath == null ? "My workflow.screenflow.json" : Path.GetFileName(filePath) };
            if (dialog.ShowDialog(this) != true) return false;
            destination = dialog.FileName;
        }
        try { flow.Save(destination); filePath = destination; dirty = false; UpdateSummary(); StatusText.Text = "Saved " + Path.GetFileName(filePath); return true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save workflow"); return false; }
    }
    private bool CanReplace() {
        if (!dirty) return true;
        var answer = MessageBox.Show(this, "Save your changes before continuing?", "Unsaved workflow", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && Save());
    }
    private void New_Click(object sender, RoutedEventArgs e) { if (CanReplace()) LoadFlow(new Workflow(), null); }
    private void Example_Click(object sender, RoutedEventArgs e) {
        if (cancellation != null || !CanReplace()) return;
        LoadFlow(Workflow.Example(), null); dirty = true; UpdateSummary();
        AppendLog("Example loaded. Pick your destination field, then enable the final two actions.");
    }
    private void NewTabExample_Click(object sender, RoutedEventArgs e) {
        if (cancellation != null || !CanReplace()) return;
        LoadFlow(Workflow.NewTabExample(), null); dirty = true; UpdateSummary();
        AppendLog("Example loaded. Run to focus an existing browser and send Ctrl+T.");
    }
    private void Open_Click(object sender, RoutedEventArgs e) {
        if (!CanReplace()) return;
        var dialog = new OpenFileDialog { Filter = "ScreenFlow workflow (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { LoadFlow(Workflow.Load(dialog.FileName), dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open workflow"); }
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Save();
    private void SaveAs_Click(object sender, RoutedEventArgs e) => Save(true);
    private async Task<CaptureWindow?> Capture(bool point)
    {
        if (!CommitEditor()) return null;
        Hide();
        try {
            await Task.Delay(250);
            using var screenshot = Vision.Capture();
            var overlay = new CaptureWindow(screenshot, point);
            return overlay.ShowDialog() == true ? overlay : null;
        } catch (Exception ex) { AppendLog("Capture failed: " + ex.Message); return null; }
        finally { Show(); Activate(); }
    }
    private async void PickPoint_Click(object sender, RoutedEventArgs e) {
        var step = editing; var capture = await Capture(true);
        if (capture == null || step == null) return;
        step.X = capture.SelectedX; step.Y = capture.SelectedY; step.Refresh(); BuildEditor(); MarkDirty();
    }
    private async void CaptureImage_Click(object sender, RoutedEventArgs e) {
        var step = editing; var capture = await Capture(false);
        if (capture?.ImageBase64 == null || step == null) return;
        step.ImageBase64 = capture.ImageBase64; step.Refresh(); BuildEditor(); MarkDirty();
    }
    private void SetRunning(bool running)
    {
        FileButtons.IsEnabled = ActionLibrary.IsEnabled = EditorFields.IsEnabled = ApplyButton.IsEnabled = StepsList.IsEnabled = !running;
        WorkflowName.IsEnabled = RepeatInput.IsEnabled = CountdownInput.IsEnabled = !running;
        ThemePicker.IsEnabled = !running;
        RunButton.IsEnabled = !running; StopButton.IsEnabled = running;
        UpdateActionButtons();
    }
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || !CommitWorkflow()) return;
        if (!flow.Steps.Any(s => s.Enabled)) { AppendLog("Add at least one enabled action to run."); return; }
        try {
            foreach (var step in flow.Steps.Where(s => s.Enabled)) WorkflowRunner.Validate(step);
        } catch (Exception ex) { AppendLog("Cannot run: " + ex.Message); EditorError.Text = ex.Message; return; }
        // Freeze a snapshot: edits cannot change the actions underneath an active run.
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<Workflow>(System.Text.Json.JsonSerializer.Serialize(flow, Workflow.Json), Workflow.Json)!;
        browserTracker.Observe();
        var browser = new BrowserAutomation(browserTracker.LastBrowser);
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        NativeInput.RegisterHotKey(handle, 1, 0x4000, 0x77);
        stopTimer.Start(); SetRunning(true);
        string result = "Completed";
        try {
            if (MinimizeOnRun.IsChecked == true) WindowState = WindowState.Minimized;
            for (int i = snapshot.StartDelaySeconds; i > 0; i--) {
                StatusText.Text = $"Starting in {i}s · focus your target app · F8 to stop";
                AppendLog($"Starting in {i}…"); await Task.Delay(1000, token);
            }
            var runner = new WorkflowRunner(Dispatcher, browser);
            runner.Log += text => Dispatcher.Invoke(() => AppendLog(text));
            runner.StepStarted += i => Dispatcher.Invoke(() => { selecting = true; StepsList.SelectedIndex = i; StepsList.ScrollIntoView(flow.Steps[i]); selecting = false; StatusText.Text = $"Running · {i + 1}/{flow.Steps.Count} · {flow.Steps[i].Name}"; });
            await Task.Run(() => runner.Run(snapshot, token), token);
            AppendLog("Workflow completed successfully.");
        } catch (OperationCanceledException) { result = "Stopped"; AppendLog("Stopped by user."); }
        catch (Exception ex) { result = "Stopped with an error"; AppendLog("ERROR · " + ex.Message); }
        finally {
            stopTimer.Stop(); NativeInput.UnregisterHotKey(handle, 1); cancellation.Dispose(); cancellation = null;
            SetRunning(false); WindowState = WindowState.Normal; Activate();
            editing = StepsList.SelectedItem as Step; BuildEditor(); StatusText.Text = result;
        }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private void AppendLog(string text) {
        if (RunLog.Text.Length > 30000) RunLog.Text = RunLog.Text[^15000..];
        RunLog.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}"); RunLog.ScrollToEnd();
    }
    private void ClearLog_Click(object sender, RoutedEventArgs e) => RunLog.Clear();
    private void Practice_Click(object sender, RoutedEventArgs e) {
        if (cancellation != null) return;
        if (practice == null || !practice.IsLoaded) { practice = new PracticeWindow(); practice.Show(); }
        else practice.Activate();
    }
    private void OnClosing(object? sender, CancelEventArgs e) {
        if (cancellation != null) { cancellation.Cancel(); e.Cancel = true; AppendLog("Stopping. Close again after the run ends."); return; }
        if (!CanReplace()) e.Cancel = true;
    }
}
