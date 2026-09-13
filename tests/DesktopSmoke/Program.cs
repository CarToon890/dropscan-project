using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DropScan;
using DropScan.Sharing;
using DropScan.Desktop;
using System.Windows.Threading;

internal static class Program
{
    static int assertions;
    static MainWindow window = null!;
    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
    static T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    static void Call(string method, params object?[] args) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, args);
    static void Event(string method) => Call(method, window, new RoutedEventArgs());
    static void AsyncCall(string name)
    {
        var task = (Task)typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, null)!;
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); assertions++; }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/qa");
            Directory.CreateDirectory(output);
            var app = new Application();
            app.Resources = (ResourceDictionary)Application.LoadComponent(new Uri("/DropScan;component/Theme.xaml", UriKind.Relative));

            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            // Instantiate the actual production WPF tree, without launching a second interactive app.
            var historyPath = Path.Combine(output, "history-" + Guid.NewGuid().ToString("N") + ".json");
            window = new MainWindow(new HistoryStore(historyPath));
            Call("ShowStep", 0);
            Control<ComboBox>("NetworkSelect").SelectedItem = ((IReadOnlyList<NetworkChoice>)Control<ComboBox>("NetworkSelect").ItemsSource).Last();
            var files = Field<ObservableCollection<SelectedFile>>("files");
            var history = Field<ObservableCollection<ShareRecord>>("history");
            Check(!Control<Button>("NextButton").IsEnabled, "Empty selection must not continue");
            Event("OpenHistory");
            Check(Control<StackPanel>("HistoryEmpty").Visibility == Visibility.Visible, "Empty history state");
            RenderAll(output, "history-empty");
            Event("CloseHistory");
            RenderAll(output, "select-empty");

            string first = Path.Combine(output, "ทดสอบไฟล์ภาษาไทย-รายงานประจำเดือน.txt");
            string second = Path.Combine(output, "notes.txt");
            File.WriteAllText(first, "DropScan test fixture — no networking.");
            File.WriteAllText(second, "Second test fixture.");
            byte[] hash = SHA256.HashData(File.ReadAllBytes(first));
            Call("AddFiles", (object)new[] { first, second, first, Path.Combine(output, "missing-selection") });
            Check(files.Count == 2, "Duplicate must be ignored and missing entry skipped");
            Check(Control<TextBlock>("SelectionError").Visibility == Visibility.Visible, "Skipped entry needs inline feedback");
            Check(Control<Button>("NextButton").IsEnabled, "Selected files enable next");
            Call("RemoveFile", new Button { Tag = files[1] }, new RoutedEventArgs());
            Check(files.Count == 1 && File.Exists(second), "Remove selection must preserve original");
            Control<RadioButton>("ExpiryWeek").IsChecked = true;
            Event("NextStep");
            Event("PreviousStep");
            Check(files.Count == 1 && Control<RadioButton>("ExpiryWeek").IsChecked == true, "Back preserves selection and expiry");

            // Exercise long file names and a scrollable list using local fixtures.
            for (int i = 0; i < 18; i++)
            {
                var path = Path.Combine(output, $"{i:D2}-รายงานการส่งไฟล์สำหรับทดสอบการแสดงชื่อที่ยาวมากในหน้าต่างขนาดเล็ก.txt");
                File.WriteAllText(path, "fixture");
                Call("AddFiles", (object)new[] { path });
            }
            RenderAll(output, "selected-many");
            Event("NextStep");
            Check(Control<Button>("CreateButton").IsDefault && Control<CheckBox>("ApprovalRequired").Focusable, "Prepare step supports Enter and keyboard focus for approval option");
            RenderAll(output, "prepare");
            Control<CheckBox>("PinEnabled").IsChecked = true;
            Check(Control<Border>("PinOptions").Visibility == Visibility.Visible && Control<TextBox>("PinText").Text.Length == 6, "Enabling PIN reveals a generated six-digit value");
            Control<CheckBox>("ApprovalRequired").IsChecked = true;
            Control<TextBox>("PinText").Text = "12";
            AsyncCall("CreateShareAsync");
            Check(history.Count == 0 && Control<TextBlock>("PrepareError").Text.Contains("6 หลัก"), "Invalid PIN blocks share creation with inline feedback");
            Control<TextBox>("PinText").Text = "482913";
            RenderAll(output, "prepare-pin");
            // A stale reference must not create a share. No fixture is deleted for this test.
            var original = files[0];
            files[0] = original with { Path = Path.Combine(output, "does-not-exist.bin") };
            AsyncCall("CreateShareAsync");
            Check(history.Count == 0 && Control<TextBlock>("PrepareError").Visibility == Visibility.Visible, "Missing file blocks creation");
            files[0] = original;
            AsyncCall("CreateShareAsync");
            Check(history.Count == 1 && history[0].FileCount == 19 && history[0].Expiry == "7 วัน", $"History captures share snapshot: count={history.Count}, files={files.Count}, actual={history.FirstOrDefault()?.FileCount}, expiry={history.FirstOrDefault()?.Expiry}, error={Control<TextBlock>("PrepareError").Text}");
            Check(history[0].Session?.PinEnabled == true && Control<TextBox>("SharePin").Text == "482913" && Control<StackPanel>("PinResultPanel").Visibility == Visibility.Visible, "Protected share shows sender PIN separately");
            Check(history[0].Session?.ApprovalRequired == true && Control<StackPanel>("ApprovalPanel").Visibility == Visibility.Visible, "Approval share shows sender request panel");
            Check(Control<Grid>("ResultPanel").Visibility == Visibility.Visible, "Create opens results");
            var qr = new FormatConvertedBitmap((BitmapSource)Control<Image>("QrImage").Source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[qr.PixelWidth * qr.PixelHeight * 4];
            qr.CopyPixels(pixels, qr.PixelWidth * 4, 0);
            var decoded = new ZXing.BarcodeReaderGeneric().Decode(new ZXing.RGBLuminanceSource(pixels, qr.PixelWidth, qr.PixelHeight, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32));
            Check(decoded?.Text == history[0].Code && !decoded.Text.Contains("482913"), "Real QR contains exact receiver URL without PIN");
            var session = history[0].Session!;
            var createRequest = typeof(ShareSession).GetMethod("CreateApprovalRequest", BindingFlags.NonPublic | BindingFlags.Instance)!;
            createRequest.Invoke(session, new object[] { "desktop-smoke", "192.168.1.44" });
            Call("RefreshApprovalRequests");
            var requests = Field<ObservableCollection<ApprovalRequestSnapshot>>("approvalRequests");
            Check(requests.Count == 1 && requests[0].IsPending && Control<TextBlock>("ApprovalEmpty").Visibility == Visibility.Collapsed, "Pending request appears with sender actions");
            Call("ApproveRecipient", new Button { Tag = requests[0] }, new RoutedEventArgs());
            Check(session.SnapshotApprovalRequests().Single().State == ApprovalRequestState.Approved && Control<TextBlock>("ResultStatus").Text.Contains("อนุมัติ"), "Sender approval updates request state");
            createRequest.Invoke(session, new object[] { "desktop-smoke-2", "192.168.1.45" });
            Call("RefreshApprovalRequests");
            var secondRequest = requests.Single(r => r.ClientLabel == "192.168.1.45");
            Call("DenyRecipient", new Button { Tag = secondRequest }, new RoutedEventArgs());
            Check(session.SnapshotApprovalRequests().Single(r => r.ClientLabel == "192.168.1.45").State == ApprovalRequestState.Denied && Control<TextBlock>("ResultStatus").Text.Contains("ปฏิเสธ"), "Sender denial updates request state");
            RenderAll(output, "result");
            Event("OpenHistory");
            RenderAll(output, "history-active");
            Event("CloseHistory");
            Check(Control<Grid>("ResultPanel").Visibility == Visibility.Visible, "History returns to same step");
            bool notified = false;
            history[0].PropertyChanged += (_, e) => notified |= e.PropertyName == "Status";
            Event("StopShare");
            Check(history[0].IsStopped && notified, "Stop updates bound history status");
            Check(Control<StackPanel>("PinResultPanel").Visibility == Visibility.Collapsed && Control<TextBox>("SharePin").Text == "", "Stopping share removes PIN from the result screen");
            Check(Control<StackPanel>("ApprovalPanel").Visibility == Visibility.Collapsed, "Stopping share hides recipient actions");
            Check(Control<Button>("RestartButton").Visibility == Visibility.Visible && Control<Button>("CopyButton").Visibility == Visibility.Collapsed, "Stopped actions");
            Check(Control<Button>("RestartButton").IsDefault, "Enter starts a new list after stopping");
            RenderAll(output, "stopped");
            AsyncCall("RestartAsync");
            Check(files.Count == 0 && Control<RadioButton>("ExpiryDay").IsChecked == true && Control<CheckBox>("PinEnabled").IsChecked == false && Control<CheckBox>("ApprovalRequired").IsChecked == false && Control<TextBox>("PinText").Text == "", "Restart resets selection, expiry, PIN, and approval");
            Check(history.Count == 1 && history[0].FileCount == 19, "Reset preserves historical snapshot");
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(first))), "Original remains byte-identical");
            AsyncCall("SaveHistoryAsync");
            Check(!File.ReadAllText(historyPath).Contains("482913"), "Persisted history never stores PIN");
            Check(!File.ReadAllText(historyPath).Contains("192.168.1.44"), "Persisted history never stores recipient requests");
            window.Close();
            window = new MainWindow(new HistoryStore(historyPath));
            var restored = Field<ObservableCollection<ShareRecord>>("history");
            Check(restored.Count == 1 && restored[0].FileCount == 19, "Reopening retains persisted history summary");
            Check(restored[0].Session == null && restored[0].Code == "" && restored[0].IsStopped, "Reopening cannot revive a share or token");
            window.Close();
            Console.WriteLine($"PASS: {assertions} assertions; 32 WPF layout renders at 960x720 / 800x600, 125% / 150% DPI. Output: {output}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void RenderAll(string output, string name)
    {
        foreach (var size in new[] { (960, 720), (800, 600) })
        foreach (var scale in new[] { 1.25, 1.5 })
        {
            var root = (FrameworkElement)window.Content;
            var available = new Size(size.Item1 - 16, size.Item2 - 39);
            VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
            root.Measure(available);
            root.Arrange(new Rect(available));
            root.UpdateLayout();
            Call("UpdateQrSize", root.ActualHeight);
            root.UpdateLayout();
            root.UpdateLayout();
            Check(root.ActualWidth <= available.Width, "Root exceeds window width");
            if (Control<Grid>("StepsBar").Visibility == Visibility.Visible)
            {
                var first = Control<Border>("StepOne");
                var second = Control<Border>("StepTwo");
                Check(first.TranslatePoint(new Point(first.ActualWidth, 0), root).X <= second.TranslatePoint(new Point(), root).X, "Step labels overlap");
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(available.Width * scale), (int)Math.Ceiling(available.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var dc = background.RenderOpen()) dc.DrawRectangle(window.Background, null, new Rect(available));
            bitmap.Render(background);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, $"{name}-{size.Item1}x{size.Item2}-{scale * 100:0}.png"));
            encoder.Save(stream);
        }
    }
}




