using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace DropScan.Sharing;

public sealed record SharedFile(int Id, string Path, string Name, long Length, DateTime LastWriteUtc, string ArchivePath = "", bool IsDirectory = false) { public string ETag { get; } = "\"" + Guid.NewGuid().ToString("N") + "\""; }
public sealed record TransferSnapshot(string Status, int ActiveCount, long BytesSent, long TotalBytes, double BytesPerSecond, int CompletedCount, bool IsArchive = false)
{
    public double Percent => TotalBytes == 0 ? 0 : Math.Clamp(BytesSent * 100d / TotalBytes, 0, 100);
}

public enum PinAttemptResult { Allowed, Incorrect, Locked }

public enum ApprovalRequestState { Pending, Approved, Denied, Cancelled, Expired }

public sealed record ApprovalRequestSnapshot(Guid Id, string ClientLabel, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt, ApprovalRequestState State)
{
    public bool IsPending => State == ApprovalRequestState.Pending;
    public string StatusLabel => State switch
    {
        ApprovalRequestState.Pending => "รออนุมัติ",
        ApprovalRequestState.Approved => "อนุมัติแล้ว",
        ApprovalRequestState.Denied => "ปฏิเสธแล้ว",
        ApprovalRequestState.Cancelled => "ผู้รับยกเลิก",
        _ => "หมดอายุ"
    };
    public string TimeLabel => $"ขอเมื่อ {RequestedAt.ToLocalTime():HH:mm:ss} • หมดอายุ {ExpiresAt.ToLocalTime():HH:mm:ss}";
}

internal sealed record ApprovalRequestGrant(string Secret, DateTimeOffset ExpiresAt);

public sealed class ShareSession : IDisposable
{
    readonly object gate = new();
    readonly CancellationTokenSource lifetime = new();
    readonly Dictionary<Guid, Transfer> active = new();
    readonly Timer expiryTimer;
    readonly byte[]? pinSalt;
    readonly byte[]? pinHash;
    readonly byte[] accessGrant = RandomNumberGenerator.GetBytes(32);
    readonly Dictionary<string, PinFailures> pinFailures = new(StringComparer.Ordinal);
    readonly List<ApprovalRequest> approvalRequests = new();
    readonly TimeSpan approvalRequestLifetime;
    string? terminal;
    string lastStatus = "รอผู้รับ";
    int completed;
    long lastSent, lastTotal;
    double lastSpeed;
    bool lastArchive;
    bool disposed;
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    internal string AccessGrant => Convert.ToHexString(accessGrant).ToLowerInvariant();
    public bool PinEnabled => pinHash != null;
    public bool ApprovalRequired { get; }
    public IReadOnlyList<SharedFile> Files { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string? EndReason { get { lock (gate) { CheckExpiry(); return terminal; } } }
    public bool IsAvailable => EndReason == null;

    public ShareSession(IEnumerable<string> paths, TimeSpan duration, string? pin = null, bool approvalRequired = false, TimeSpan? approvalRequestLifetime = null) : this(paths.Select(p => new SelectedFile(p, System.IO.Path.GetFileName(p), 0)), duration, pin, approvalRequired, approvalRequestLifetime) { }
    public ShareSession(IEnumerable<SelectedFile> entries, TimeSpan duration, string? pin = null, bool approvalRequired = false, TimeSpan? approvalRequestLifetime = null)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(duration));
        this.approvalRequestLifetime = approvalRequestLifetime ?? TimeSpan.FromMinutes(5);
        if (this.approvalRequestLifetime < TimeSpan.FromMilliseconds(100) || this.approvalRequestLifetime > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(approvalRequestLifetime));
        ApprovalRequired = approvalRequired;
        if (pin != null)
        {
            if (pin.Length != 6 || pin.Any(c => c is < '0' or > '9')) throw new ArgumentException("PIN ต้องเป็นตัวเลข 6 หลัก", nameof(pin));
            pinSalt = RandomNumberGenerator.GetBytes(16);
            pinHash = HashPin(pin, pinSalt);
        }
        var selected = entries.DistinctBy(f => System.IO.Path.GetFullPath(f.Path), StringComparer.OrdinalIgnoreCase).ToArray();
        var archiveNames = ArchiveNames.Allocate(selected);
        Files = selected.Select((entry, index) =>
        {
            if (SelectionScanner.HasReparsePoint(entry.Path)) throw new IOException("ไม่แชร์ symbolic link หรือ junction");
            var info = new FileInfo(entry.Path);
            if (entry.IsDirectory)
            {
                if (!Directory.Exists(entry.Path)) throw new DirectoryNotFoundException();
                return new SharedFile(index, System.IO.Path.GetFullPath(entry.Path), entry.Name, 0, Directory.GetLastWriteTimeUtc(entry.Path), archiveNames[index], true);
            }
            using var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new SharedFile(index, System.IO.Path.GetFullPath(entry.Path), entry.Name, stream.Length, info.LastWriteTimeUtc, archiveNames[index]);
        }).ToArray();
        if (Files.Count == 0) throw new ArgumentException("ต้องเลือกอย่างน้อยหนึ่งรายการ", nameof(entries));
        ExpiresAt = DateTimeOffset.UtcNow.Add(duration);
        expiryTimer = new Timer(_ => End("หมดอายุแล้ว"), null, duration, Timeout.InfiniteTimeSpan);
    }
    static byte[] HashPin(string pin, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(pin, salt, 100_000, HashAlgorithmName.SHA256, 32);
    public bool ValidateAccessGrant(string? value) => PinEnabled && value != null &&
        CryptographicOperations.FixedTimeEquals(EncodingBytes(value), EncodingBytes(AccessGrant));
    static byte[] EncodingBytes(string value) => System.Text.Encoding.ASCII.GetBytes(value);
    public PinAttemptResult VerifyPin(string clientKey, string pin, out int attemptsRemaining)
    {
        lock (gate)
        {
            CheckExpiry();
            if (terminal != null || pinHash == null || pinSalt == null) { attemptsRemaining = 0; return PinAttemptResult.Locked; }
            var now = DateTimeOffset.UtcNow;
            foreach (var stale in pinFailures.Where(p => now - p.Value.LastAttempt > TimeSpan.FromMinutes(10)).Select(p => p.Key).ToArray()) pinFailures.Remove(stale);
            if (pinFailures.TryGetValue(clientKey, out var state) && state.LockedUntil > now)
            {
                attemptsRemaining = 0; return PinAttemptResult.Locked;
            }
            bool valid = pin.Length == 6 && pin.All(c => c is >= '0' and <= '9') &&
                CryptographicOperations.FixedTimeEquals(HashPin(pin, pinSalt), pinHash);
            if (valid) { pinFailures.Remove(clientKey); attemptsRemaining = 5; return PinAttemptResult.Allowed; }
            int failures = state?.Failures + 1 ?? 1;
            if (failures >= 5)
            {
                pinFailures[clientKey] = new(0, now, now.AddSeconds(60));
                attemptsRemaining = 0; return PinAttemptResult.Locked;
            }
            pinFailures[clientKey] = new(failures, now, DateTimeOffset.MinValue);
            attemptsRemaining = 5 - failures;
            return PinAttemptResult.Incorrect;
        }
    }

    internal ApprovalRequestGrant? CreateApprovalRequest(string clientKey, string clientLabel)
    {
        lock (gate)
        {
            CheckExpiry();
            if (terminal != null || !ApprovalRequired) return null;
            var now = DateTimeOffset.UtcNow;
            RefreshApprovalRequests(now);
            if (approvalRequests.Count(r => r.State == ApprovalRequestState.Pending) >= 32) return null;
            PruneApprovalRequests(now);
            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var request = new ApprovalRequest(Guid.NewGuid(), secret, clientKey, clientLabel, now, now.Add(approvalRequestLifetime));
            approvalRequests.Add(request);
            return new(secret, request.ExpiresAt);
        }
    }

    internal ApprovalRequestState? GetApprovalState(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;
        lock (gate)
        {
            CheckExpiry();
            RefreshApprovalRequests(DateTimeOffset.UtcNow);
            var encoded = EncodingBytes(secret);
            var request = approvalRequests.FirstOrDefault(r => r.Secret.Length == secret.Length && CryptographicOperations.FixedTimeEquals(EncodingBytes(r.Secret), encoded));
            return request?.State;
        }
    }

    public IReadOnlyList<ApprovalRequestSnapshot> SnapshotApprovalRequests()
    {
        lock (gate)
        {
            CheckExpiry();
            var now = DateTimeOffset.UtcNow;
            RefreshApprovalRequests(now);
            PruneApprovalRequests(now);
            return approvalRequests.OrderByDescending(r => r.RequestedAt).Select(r => r.Snapshot()).ToArray();
        }
    }

    public bool ApproveRequest(Guid id) => SetApprovalState(id, ApprovalRequestState.Approved);
    public bool DenyRequest(Guid id) => SetApprovalState(id, ApprovalRequestState.Denied);
    internal bool CancelApprovalRequest(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return false;
        lock (gate)
        {
            CheckExpiry();
            RefreshApprovalRequests(DateTimeOffset.UtcNow);
            var encoded = EncodingBytes(secret);
            var request = approvalRequests.FirstOrDefault(r => r.Secret.Length == secret.Length && CryptographicOperations.FixedTimeEquals(EncodingBytes(r.Secret), encoded));
            if (request?.State != ApprovalRequestState.Pending) return false;
            request.State = ApprovalRequestState.Cancelled;
            return true;
        }
    }

    bool SetApprovalState(Guid id, ApprovalRequestState state)
    {
        lock (gate)
        {
            CheckExpiry();
            RefreshApprovalRequests(DateTimeOffset.UtcNow);
            var request = approvalRequests.FirstOrDefault(r => r.Id == id);
            if (terminal != null || request?.State != ApprovalRequestState.Pending) return false;
            request.State = state;
            return true;
        }
    }

    void RefreshApprovalRequests(DateTimeOffset now)
    {
        foreach (var request in approvalRequests.Where(r => r.State == ApprovalRequestState.Pending && now >= r.ExpiresAt)) request.State = ApprovalRequestState.Expired;
    }

    void PruneApprovalRequests(DateTimeOffset now)
    {
        approvalRequests.RemoveAll(r => r.State != ApprovalRequestState.Pending && now - r.ExpiresAt > TimeSpan.FromMinutes(10));
        while (approvalRequests.Count > 100)
        {
            int index = approvalRequests.FindIndex(r => r.State != ApprovalRequestState.Pending);
            if (index < 0) break;
            approvalRequests.RemoveAt(index);
        }
    }
    void CheckExpiry() { if (terminal == null && DateTimeOffset.UtcNow >= ExpiresAt) End("หมดอายุแล้ว"); }
    public void Stop() => End("หยุดแชร์แล้ว");
    void End(string reason)
    {
        lock (gate)
        {
            if (terminal != null) return;
            terminal = reason;
            foreach (var request in approvalRequests.Where(r => r.State == ApprovalRequestState.Pending)) request.State = ApprovalRequestState.Cancelled;
            lifetime.Cancel();
        }
    }

    public Transfer? Begin(SharedFile file, CancellationToken requestAborted, long? totalBytes = null, bool isArchive = false)
    {
        lock (gate)
        {
            CheckExpiry();
            if (terminal != null || active.Count >= 3) return null;
            var transfer = new Transfer(file, lifetime.Token, requestAborted, totalBytes ?? file.Length, isArchive);
            active.Add(transfer.Id, transfer);
            return transfer;
        }
    }
    public void Progress(Transfer transfer, int bytes) { lock (gate) transfer.Sent += bytes; }
    public void Finish(Transfer transfer, string result, bool success)
    {
        lock (gate)
        {
            active.Remove(transfer.Id);
            if (success) completed++;
            lastStatus = transfer.CancelledBySender ? "ยกเลิกการส่งแล้ว • ลิงก์ยังใช้ได้" : result;
            lastSent = transfer.Sent;
            lastTotal = transfer.TotalBytes;
            lastArchive = transfer.IsArchive;
            lastSpeed = transfer.Sent / Math.Max(transfer.Clock.Elapsed.TotalSeconds, 0.001);
        }
        transfer.Dispose();
    }
    public void CancelTransfers()
    {
        lock (gate) foreach (var transfer in active.Values) { transfer.CancelledBySender = true; transfer.Cancellation.Cancel(); }
    }
    public TransferSnapshot Snapshot()
    {
        lock (gate)
        {
            CheckExpiry();
            return new(terminal ?? (active.Count > 0 ? $"กำลังส่ง {active.Count} รายการ" : lastStatus), active.Count,
                active.Count > 0 ? active.Values.Sum(t => t.Sent) : lastSent,
                active.Count > 0 ? active.Values.Sum(t => t.TotalBytes) : lastTotal,
                active.Count > 0 ? active.Values.Sum(t => t.Sent / Math.Max(t.Clock.Elapsed.TotalSeconds, 0.001)) : lastSpeed, completed, active.Count > 0 ? active.Values.Any(t => t.IsArchive) : lastArchive);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Stop();
            expiryTimer.Dispose();
            lifetime.Dispose();
        }
    }
}

sealed record PinFailures(int Failures, DateTimeOffset LastAttempt, DateTimeOffset LockedUntil);

sealed class ApprovalRequest(Guid id, string secret, string clientKey, string clientLabel, DateTimeOffset requestedAt, DateTimeOffset expiresAt)
{
    public Guid Id { get; } = id;
    public string Secret { get; } = secret;
    public string ClientKey { get; } = clientKey;
    public string ClientLabel { get; } = clientLabel;
    public DateTimeOffset RequestedAt { get; } = requestedAt;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public ApprovalRequestState State { get; set; } = ApprovalRequestState.Pending;
    public ApprovalRequestSnapshot Snapshot() => new(Id, ClientLabel, RequestedAt, ExpiresAt, State);
}

public sealed class Transfer : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public SharedFile File { get; }
    public long Sent { get; internal set; }
    public long TotalBytes { get; }
    public bool IsArchive { get; }
    public bool CancelledBySender { get; internal set; }
    public Stopwatch Clock { get; } = Stopwatch.StartNew();
    public CancellationTokenSource Cancellation { get; }
    internal Transfer(SharedFile file, CancellationToken lifetime, CancellationToken request, long totalBytes, bool isArchive)
    {
        File = file;
        TotalBytes = totalBytes; IsArchive = isArchive;
        Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, request);
    }
    public void Dispose() => Cancellation.Dispose();
}

