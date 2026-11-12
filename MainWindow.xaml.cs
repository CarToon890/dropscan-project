using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropScan.Sharing;
using QRCoder;
using DropScan.Desktop;

namespace DropScan;

public record SelectedFile(string Path, string Name, long Size)
{
    public string ArchivePath { get; init; } = Name;
    public bool IsDirectory { get; init; }
    public string SizeLabel => IsDirectory ? "โฟลเดอร์ว่าง" : FormatSize(Size);
    public static string FormatSize(long size) => size >= 1073741824 ? $"{size / 1073741824d:N1} GB" : size >= 1048576 ? $"{size / 1048576d:N1} MB" : size >= 1024 ? $"{size / 1024d:N1} KB" : $"{size:N0} B";
}

public sealed class ShareRecord : INotifyPropertyChanged
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Code { get; init; } = "";
    public required int FileCount { get; init; }
    public required long TotalBytes { get; init; }
    public required string Expiry { get; init; }
    public ShareSession? Session { get; init; }
    public string ArchivedStatus { get; init; } = "สิ้นสุดจากรอบก่อน";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public DateTime UpdatedAt { get; private set; } = DateTime.UtcNow;
    string? previousStatus;
    public bool IsStopped => Session == null || !Session.IsAvailable;
    public string Status => Session?.Snapshot().Status ?? ArchivedStatus;
    public string Summary => $"{CreatedAt:dd/MM HH:mm}  •  {FileCount} ไฟล์  •  {SelectedFile.FormatSize(TotalBytes)}  •  {Expiry}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Stop() { Session?.Stop(); Refresh(); }
    public bool Refresh()
    {
        string current = Status;
        if (current == previousStatus) return false;
        previousStatus = current; UpdatedAt = DateTime.UtcNow;
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        return true;
    }
    public HistoryEntry ToEntry() => new(Id, CreatedAt, UpdatedAt, FileCount, TotalBytes, Expiry, Status);
    public static ShareRecord FromEntry(HistoryEntry entry) => new()
    {
        Id = entry.Id, CreatedAt = entry.CreatedAt, UpdatedAt = entry.UpdatedAt,
        FileCount = entry.FileCount, TotalBytes = entry.TotalBytes, Expiry = entry.Expiry,
        ArchivedStatus = "สิ้นสุดจากรอบก่อน • " + (entry.Status.Contains("หมดอายุ") ? "หมดอายุ" : entry.Status.Contains("ครบ") ? "ส่งข้อมูลครบ" : "ปิดรายการแล้ว")
    };
}
public partial class MainWindow : Window
{
    readonly ObservableCollection<SelectedFile> files = new();
    readonly ObservableCollection<ShareRecord> history = new();
    readonly ObservableCollection<ApprovalRequestSnapshot> approvalRequests = new();
    readonly HashSet<Guid> notifiedApprovalRequests = new();
    readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    LocalShareServer? server;
    CloudflareQuickTunnel? tunnel;
    DateTime tunnelStartedAt;
    bool tunnelProbeActive;
    static readonly HttpClient tunnelHealthClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    ShareRecord? activeShare;
    int step;
    bool showingHistory;
    bool creating;
    bool closing;
    bool scanning;
    bool initialized;
    readonly HistoryStore historyStore;
    TrayService? tray;
    int notifiedCompleted;
    Task historySave = Task.CompletedTask;

    public MainWindow() : this(HistoryStore.Default(), true) { }
    public MainWindow(HistoryStore store, bool enableTray = false)
    {
        historyStore = store;
        InitializeComponent();
        foreach (var entry in store.Load()) history.Add(ShareRecord.FromEntry(entry));
        HistoryNotice.Text = store.Error ?? "เก็บข้อมูลสรุปล่าสุด 200 รายการ • ลิงก์เก่าจะไม่เปิดใช้งานใหม่";
        FileList.ItemsSource = files;
        HistoryList.ItemsSource = history;
        ApprovalList.ItemsSource = approvalRequests;
        RefreshFiles();
        RefreshNetworks(this, new RoutedEventArgs());
        Loaded += (_, _) =>
        {
            if (initialized) return;
            initialized = true;
            ShowStep(0);
            if (enableTray) tray = new TrayService(RestoreFromTray, Close);
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && tray != null) HideToTray(this, new RoutedEventArgs()); };
        refreshTimer.Tick += (_, _) => RefreshTransfer();
        refreshTimer.Start();
        Closing += OnClosing;
    }

    void ShowStep(int value)
    {
        step = value;
        showingHistory = false;
        HistoryPanel.Visibility = Visibility.Collapsed;
        StepsBar.Visibility = Visibility.Visible;
        HistoryButton.IsEnabled = true;
        SelectPanel.Visibility = value == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreparePanel.Visibility = value == 1 ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = value == 2 ? Visibility.Visible : Visibility.Collapsed;
        var tabs = new[] { StepOne, StepTwo, StepThree };
        var labels = new[] { StepOneLabel, StepTwoLabel, StepThreeLabel };
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i].Background = i == value ? new SolidColorBrush(Color.FromRgb(232, 239, 255)) : Brushes.Transparent;
            labels[i].Foreground = (Brush)FindResource(i == value ? "Blue" : "Muted");
            labels[i].FontWeight = i == value ? FontWeights.SemiBold : FontWeights.Normal;
        }
        NextButton.IsDefault = value == 0;
        CreateButton.IsDefault = value == 1;
        // Stopping an active transfer must be an explicit focused action, not a default Enter action.
        StopButton.IsDefault = false;
        RestartButton.IsDefault = value == 2 && activeShare?.IsStopped == true;
        FocusLater(value == 0 ? (files.Count == 0 ? PickButton : NextButton) : value == 1 ? NetworkSelect : activeShare?.IsStopped == true ? RestartButton : CopyButton);
    }

    void FocusLater(Control control) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (!closing) control.Focus(); }));
    void LayoutSizeChanged(object sender, SizeChangedEventArgs e) => UpdateQrSize(e.NewSize.Height);
    void UpdateQrSize(double height)
    {
        if (QrImage != null) QrImage.Width = QrImage.Height = height < 570 ? 156 : 190;
    }
    static void ShowError(TextBlock target, string message) { target.Text = message; target.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible; }

    void RefreshNetworks(object sender, RoutedEventArgs e)
    {
        var selected = NetworkSelect.SelectedItem as NetworkChoice;
        var networks = NetworkChoice.Find();
        NetworkSelect.ItemsSource = networks;
        NetworkSelect.SelectedItem = networks.FirstOrDefault(n => selected != null && n.IsTunnel == selected.IsTunnel && n.Address.Equals(selected.Address)) ?? networks.First();
        UpdateNetworkHint();
    }

    void NetworkSelect_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateNetworkHint();
    void UpdateNetworkHint()
    {
        if (NetworkHint == null || NetworkSelect?.SelectedItem is not NetworkChoice choice) return;
        NetworkHint.Text = choice.IsTunnel
            ? "Cloudflare Quick Tunnel: URL ชั่วคราวสาธารณะ ใช้ทดสอบเท่านั้น • ต้องติดตั้ง cloudflared"
            : "โหมด LAN ใช้ HTTP ภายในเครือข่ายเดียวกัน";
    }

    async void PickFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "เลือกไฟล์สำหรับ DropScan", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await AddFilesAsync(dialog.FileNames);
    }
    void FilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    async void FilesDropped(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddFilesAsync(paths);
    }
    async void PickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "เลือกโฟลเดอร์ที่ต้องการส่ง", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await AddFilesAsync(dialog.FolderNames);
    }
    async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (scanning || creating) return;
        scanning = true;
        SelectPanel.IsEnabled = false;
        ShowError(SelectionError, "กำลังอ่านรายการไฟล์และโฟลเดอร์…");
        var existing = files.ToArray();
        var requested = paths.ToArray();
        try
        {
            var batch = await Task.Run(() => SelectionScanner.Scan(requested, existing));
            foreach (var entry in batch.Entries) files.Add(entry);
            RefreshFiles();
            ShowError(SelectionError, batch.Skipped > 0 ? $"ข้าม {batch.Skipped} รายการที่อ่านไม่ได้หรือเป็นลิงก์ • ตรวจโครงสร้างก่อนแชร์" : "");
        }
        finally { scanning = false; SelectPanel.IsEnabled = true; }
    }
    // Synchronous entry point is used by deterministic layout tests only.
    void AddFiles(IEnumerable<string> paths)
    {
        var batch = SelectionScanner.Scan(paths, files);
        foreach (var entry in batch.Entries) files.Add(entry);
        RefreshFiles();
        ShowError(SelectionError, batch.Skipped > 0 ? $"ข้าม {batch.Skipped} รายการที่อ่านไม่ได้หรือเป็นลิงก์" : "");
    }    void RemoveFile(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SelectedFile file }) files.Remove(file);
        RefreshFiles();
        ShowError(SelectionError, "");
        FocusLater(files.Count == 0 ? PickButton : NextButton);
    }
    void RefreshFiles()
    {
        var names = ArchiveNames.Allocate(files);
        for (int i = 0; i < files.Count; i++) if (files[i].ArchivePath != names[i]) files[i] = files[i] with { ArchivePath = names[i] };
        FileSummary.Text = files.Count == 0 ? "ยังไม่ได้เลือกไฟล์" : $"{files.Count(f => !f.IsDirectory)} ไฟล์ / {files.Count(f => f.IsDirectory)} โฟลเดอร์ว่าง  •  {SelectedFile.FormatSize(files.Sum(f => f.Size))}";
        NextButton.IsEnabled = files.Count > 0;
        EmptyDrop.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedArea.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PrepareSummary.Text = FileSummary.Text;
        PrepareNames.Text = string.Join("\n", files.Take(2).Select(f => f.ArchivePath)) + (files.Count > 2 ? $"\nและอีก {files.Count - 2} ไฟล์" : "");
    }
    void NextStep(object sender, RoutedEventArgs e)
    {
        if (files.Count == 0 || scanning) return;
        ShowError(PrepareError, "");
        RefreshFiles();
        ShowStep(1);
    }
    void PreviousStep(object sender, RoutedEventArgs e) { if (!creating) ShowStep(0); }
    void TogglePin(object sender, RoutedEventArgs e)
    {
        if (PinOptions == null || PinText == null) return;
        bool enabled = PinEnabled.IsChecked == true;
        PinOptions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled && string.IsNullOrWhiteSpace(PinText.Text)) PinText.Text = NewPin();
        ShowError(PrepareError, "");
    }
    void GeneratePin(object sender, RoutedEventArgs e) { PinText.Text = NewPin(); PinText.Focus(); PinText.SelectAll(); }
    static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    async void CreateShare(object sender, RoutedEventArgs e) => await CreateShareAsync();
    async Task CreateShareAsync()
    {
        if (files.Count == 0 || step != 1 || showingHistory || creating) return;
        if (NetworkSelect.SelectedItem is not NetworkChoice network) return;
        string? selectedPin = PinEnabled.IsChecked == true ? PinText.Text.Trim() : null;
        bool approvalRequired = ApprovalRequired.IsChecked == true;
        if (selectedPin != null && (selectedPin.Length != 6 || selectedPin.Any(c => c is < '0' or > '9')))
        {
            ShowError(PrepareError, "PIN ต้องเป็นตัวเลข 6 หลัก");
            PinText.Focus(); PinText.SelectAll(); return;
        }
        creating = true;
        PreparePanel.IsEnabled = false;
        HistoryButton.IsEnabled = false;
        ShowError(PrepareError, "");
        ShareSession? session = null;
        try
        {
            if (server != null) { await server.DisposeAsync(); server = null; }
            tunnel?.Dispose(); tunnel = null;
            var duration = ExpiryHour.IsChecked == true ? TimeSpan.FromHours(1) : ExpiryWeek.IsChecked == true ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
            var expiryLabel = duration == TimeSpan.FromHours(1) ? "1 ชั่วโมง" : duration == TimeSpan.FromDays(7) ? "7 วัน" : "24 ชั่วโมง";
            var selected = files.ToArray();
            session = await Task.Run(() => new ShareSession(selected, duration, selectedPin, approvalRequired));
            server = await LocalShareServer.StartAsync(session, network.Address);
            string shareUrl = server.Url;
            if (network.IsTunnel)
            {
                string origin = new Uri(server.Url).GetLeftPart(UriPartial.Authority);
                tunnel = await CloudflareQuickTunnel.StartAsync(origin);
                tunnelStartedAt = DateTime.UtcNow;
                shareUrl = tunnel.Url + new Uri(server.Url).AbsolutePath;
            }
            activeShare = new ShareRecord
            {
                Code = shareUrl, Session = session, FileCount = session.Files.Count(f => !f.IsDirectory),
                TotalBytes = session.Files.Sum(f => f.Length),
                Expiry = expiryLabel
            };
            using var qrData = QRCodeGenerator.GenerateQrCode(shareUrl, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(qrData);
            using var png = new MemoryStream(qr.GetGraphic(8));
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = png; bitmap.EndInit(); bitmap.Freeze();
            QrImage.Source = bitmap;
            history.Insert(0, activeShare);
            while (history.Count > 200) history.RemoveAt(history.Count - 1);
            notifiedCompleted = 0;
            notifiedApprovalRequests.Clear();
            approvalRequests.Clear();
            QueueHistorySave();
            ShareTitle.Text = "พร้อมให้รับไฟล์";
            ShareDetails.Text = $"{activeShare.FileCount} ไฟล์ • {SelectedFile.FormatSize(activeShare.TotalBytes)} • หมดอายุ {session.ExpiresAt.ToLocalTime():dd/MM HH:mm}" + (selectedPin == null ? "" : " • ป้องกันด้วย PIN") + (approvalRequired ? " • ต้องอนุมัติผู้รับ" : "");
            ShareCode.Text = shareUrl;
            SharePin.Text = selectedPin ?? "";
            PinResultPanel.Visibility = selectedPin == null ? Visibility.Collapsed : Visibility.Visible;
            ApprovalPanel.Visibility = approvalRequired ? Visibility.Visible : Visibility.Collapsed;
            ApprovalEmpty.Visibility = Visibility.Visible;
            ResultStatus.Text = network.IsTunnel ? "Tunnel HTTPS พร้อมแล้ว • URL สาธารณะชั่วคราว • ข้อมูลผ่าน Cloudflare" : System.Net.IPAddress.IsLoopback(network.Address) ? "ลิงก์นี้ใช้ทดสอบได้บนเครื่องผู้ส่งเท่านั้น" : "ผู้รับต้องอยู่ Wi-Fi/LAN เดียวกัน";
            QrFrame.Opacity = 1;
            CopyButton.Visibility = StopButton.Visibility = Visibility.Visible;
            RestartButton.Visibility = Visibility.Collapsed;
            RefreshTransfer();
            ShowStep(2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            tunnel?.Dispose(); tunnel = null;
            if (server != null) { await server.DisposeAsync(); server = null; }
            else session?.Dispose();
            activeShare = null;
            ShowError(PrepareError, ex is FileNotFoundException ? "ไม่พบ cloudflared • ติดตั้ง Cloudflare Tunnel แล้วลองใหม่" : ex is TimeoutException ? "เริ่ม Cloudflare Tunnel ไม่ทันเวลา • ตรวจอินเทอร์เน็ตแล้วลองใหม่" : "เริ่มแชร์ไม่สำเร็จ ตรวจไฟล์ เครือข่าย หรือการเชื่อมต่อ Tunnel แล้วลองใหม่");
        }
        finally { creating = false; PreparePanel.IsEnabled = true; HistoryButton.IsEnabled = true; }
    }

    void RefreshTransfer()
    {
        if (activeShare == null) return;
        if (tunnel != null && tunnel.IsRunning && !tunnelProbeActive && DateTime.UtcNow - tunnelStartedAt > TimeSpan.FromSeconds(15)) _ = ProbeTunnelAsync();
        if (tunnel != null && activeShare.IsStopped)
        {
            tunnel.Dispose(); tunnel = null;
            ResultStatus.Text = "รายการแชร์สิ้นสุดแล้ว • หยุด Tunnel และปิด URL ภายนอกแล้ว";
        }
        if (tunnel != null && !tunnel.IsRunning && !activeShare.IsStopped)
        {
            activeShare.Stop();
            QueueHistorySave();
            ResultStatus.Text = "Cloudflare Tunnel ขาดการเชื่อมต่อ • ลิงก์ภายนอกถูกปิดแล้ว";
            tunnel.Dispose(); tunnel = null;
        }
        var snapshot = activeShare.Session!.Snapshot();
        RefreshApprovalRequests();
        if (activeShare.Refresh()) QueueHistorySave();
        tray?.SetStatus(snapshot.Status);
        if (snapshot.CompletedCount > notifiedCompleted) { tray?.Notify("ส่งข้อมูลตามคำขอครบแล้ว", "ให้ผู้รับตรวจไฟล์ในรายการดาวน์โหลดของเบราว์เซอร์"); notifiedCompleted = snapshot.CompletedCount; }
        TransferStatus.Text = snapshot.Status;
        TransferProgress.Value = snapshot.Percent;
        TransferDetails.Text = snapshot.TotalBytes == 0 ? "ยังไม่มีการส่งข้อมูล" : $"{(snapshot.IsArchive ? "อ่านไฟล์เข้า ZIP: " : "ส่ง: ")}{snapshot.Percent:N0}% • {SelectedFile.FormatSize(snapshot.BytesSent)} / {SelectedFile.FormatSize(snapshot.TotalBytes)} • {SelectedFile.FormatSize((long)snapshot.BytesPerSecond)}/s";
        CancelButton.Visibility = snapshot.ActiveCount > 0 && !activeShare.IsStopped ? Visibility.Visible : Visibility.Collapsed;
        if (activeShare.IsStopped)
        {
            ShareTitle.Text = activeShare.Session!.EndReason!;
            SharePin.Text = "";
            PinResultPanel.Visibility = Visibility.Collapsed;
            ApprovalPanel.Visibility = Visibility.Collapsed;
            QrFrame.Opacity = 0.3;
            CopyButton.Visibility = StopButton.Visibility = Visibility.Collapsed;
            RestartButton.Visibility = Visibility.Visible;
            RestartButton.IsDefault = !showingHistory && step == 2;
        }
    }
    async Task ProbeTunnelAsync()
    {
        if (tunnel == null || activeShare == null || activeShare.IsStopped || tunnelProbeActive) return;
        tunnelProbeActive = true;
        var currentTunnel = tunnel;
        try
        {
            using var response = await tunnelHealthClient.GetAsync(activeShare.Code, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Tunnel health request failed.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (ReferenceEquals(tunnel, currentTunnel) && activeShare?.IsStopped == false)
            {
                activeShare.Stop();
                QueueHistorySave();
                tunnel.Dispose(); tunnel = null;
                ResultStatus.Text = "Cloudflare Tunnel ติดต่อไม่ได้ • ลิงก์ภายนอกถูกปิดแล้ว";
                RefreshTransfer();
            }
        }
        finally { tunnelProbeActive = false; }
    }
    void RefreshApprovalRequests()
    {
        if (activeShare?.Session?.ApprovalRequired != true) return;
        var latest = activeShare.Session.SnapshotApprovalRequests();
        foreach (var request in latest.Where(r => r.IsPending && notifiedApprovalRequests.Add(r.Id)))
            tray?.Notify("มีคำขอรับไฟล์", $"อุปกรณ์ {request.ClientLabel} รอให้คุณอนุมัติ");
        if (!approvalRequests.SequenceEqual(latest))
        {
            approvalRequests.Clear();
            foreach (var request in latest) approvalRequests.Add(request);
        }
        ApprovalEmpty.Visibility = approvalRequests.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    void ApproveRecipient(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ApprovalRequestSnapshot request } && activeShare?.Session?.ApproveRequest(request.Id) == true)
            ResultStatus.Text = $"อนุมัติอุปกรณ์ {request.ClientLabel} แล้ว";
        RefreshApprovalRequests();
    }
    void DenyRecipient(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ApprovalRequestSnapshot request } && activeShare?.Session?.DenyRequest(request.Id) == true)
            ResultStatus.Text = $"ปฏิเสธคำขอจาก {request.ClientLabel} แล้ว";
        RefreshApprovalRequests();
    }
    void StopShare(object sender, RoutedEventArgs e)
    {
        if (activeShare == null || activeShare.IsStopped) return;
        activeShare.Stop();
        tunnel?.Dispose(); tunnel = null;
        QueueHistorySave();
        ResultStatus.Text = "ลิงก์ถูกยกเลิกแล้ว ไฟล์ต้นฉบับยังอยู่ครบ";
        RefreshTransfer();
        FocusLater(RestartButton);
    }
    void CancelDownloads(object sender, RoutedEventArgs e)
    {
        activeShare?.Session?.CancelTransfers();
        ResultStatus.Text = "ยกเลิกการส่งที่กำลังทำงาน ผู้รับเริ่มดาวน์โหลดใหม่ได้";
    }
    async void Restart(object sender, RoutedEventArgs e) => await RestartAsync();
    async Task RestartAsync()
    {
        if (activeShare?.IsStopped != true || creating) return;
        creating = true;
        RestartButton.IsEnabled = false;
        try
        {
            if (server != null) { await server.DisposeAsync(); server = null; }
            tunnel?.Dispose(); tunnel = null;
            activeShare = null;
            files.Clear();
            ExpiryDay.IsChecked = true;
            PinEnabled.IsChecked = false;
            ApprovalRequired.IsChecked = false;
            PinText.Text = "";
            SharePin.Text = "";
            PinResultPanel.Visibility = Visibility.Collapsed;
            ApprovalPanel.Visibility = Visibility.Collapsed;
            approvalRequests.Clear();
            notifiedApprovalRequests.Clear();
            ShowError(SelectionError, ""); ShowError(PrepareError, "");
            RefreshFiles(); ShowStep(0);
        }
        finally { creating = false; RestartButton.IsEnabled = true; }
    }
    void CopyCode(object sender, RoutedEventArgs e)
    {
        if (activeShare == null || activeShare.IsStopped) return;
        try { Clipboard.SetText(activeShare.Code); ResultStatus.Text = "คัดลอกลิงก์รับไฟล์แล้ว"; }
        catch (System.Runtime.InteropServices.COMException) { ResultStatus.Text = "คลิปบอร์ดไม่พร้อม กรุณาลองอีกครั้ง"; }
    }
    void CopyPin(object sender, RoutedEventArgs e)
    {
        if (activeShare == null || activeShare.IsStopped || string.IsNullOrEmpty(SharePin.Text)) return;
        try { Clipboard.SetText(SharePin.Text); ResultStatus.Text = "คัดลอก PIN แล้ว • ส่งให้ผู้รับแยกจากลิงก์หรือ QR"; }
        catch (System.Runtime.InteropServices.COMException) { ResultStatus.Text = "คลิปบอร์ดไม่พร้อม กรุณาลองอีกครั้ง"; }
    }
    void OpenHistory(object sender, RoutedEventArgs e)
    {
        if (creating || scanning) return;
        showingHistory = true;
        foreach (var record in history) record.Refresh();
        SelectPanel.Visibility = PreparePanel.Visibility = ResultPanel.Visibility = StepsBar.Visibility = Visibility.Collapsed;
        HistoryPanel.Visibility = Visibility.Visible;
        HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryButton.IsEnabled = false;
        NextButton.IsDefault = CreateButton.IsDefault = StopButton.IsDefault = RestartButton.IsDefault = false;
        FocusLater(ReturnButton);
    }
    void CloseHistory(object sender, RoutedEventArgs e) => ShowStep(step);
    void QueueHistorySave() => historySave = SaveHistoryAsync();
    async Task SaveHistoryAsync()
    {
        var snapshot = history.Select(h => h.ToEntry()).ToArray();
        await historyStore.SaveAsync(snapshot);
        HistoryNotice.Text = historyStore.Error ?? "เก็บข้อมูลสรุปล่าสุด 200 รายการ • ลิงก์เก่าจะไม่เปิดใช้งานใหม่";
    }
    void HideToTray(object sender, RoutedEventArgs e)
    {
        if (tray == null) return;
        Hide();
        tray.Notify("DropScan ยังทำงานอยู่", "ดับเบิลคลิกไอคอนเพื่อเปิด หรือคลิกขวาแล้วเลือกหยุดแชร์และออก");
    }
    void RestoreFromTray()
    {
        Show(); WindowState = WindowState.Normal; Activate();
    }
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (creating || scanning) return;
        if (activeShare?.Session?.Snapshot().ActiveCount > 0 && MessageBox.Show(this, "มีผู้รับกำลังดาวน์โหลด การปิดโปรแกรมจะหยุดส่งไฟล์ ต้องการปิดหรือไม่?", "หยุดส่งและปิด DropScan", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        closing = true;
        activeShare?.Stop();
        QueueHistorySave();
        refreshTimer.Stop();
        IsEnabled = false;
        try { tunnel?.Dispose(); tunnel = null; if (server != null) await server.DisposeAsync(); await historySave; }
        finally { tray?.Dispose(); tray = null; Close(); }
    }
}



