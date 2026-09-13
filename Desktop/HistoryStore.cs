using System.IO;
using System.Text.Json;

namespace DropScan.Desktop;

// Deliberately excludes URLs, tokens, file names and local paths. Saved rows never restart a server.
public sealed record HistoryEntry(Guid Id, DateTime CreatedAt, DateTime UpdatedAt, int FileCount, long TotalBytes, string Expiry, string Status);

public sealed class HistoryStore(string path)
{
    readonly SemaphoreSlim gate = new(1, 1);
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public string? Error { get; private set; }
    public static HistoryStore Default() => new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropScan", "history.json"));
    public IReadOnlyList<HistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(Path)) return Array.Empty<HistoryEntry>();
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(Path)) ?? throw new JsonException();
            Error = null;
            return entries.Where(e => e.FileCount >= 0 && e.TotalBytes >= 0).OrderByDescending(e => e.CreatedAt).Take(200).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Error = "อ่านประวัติเดิมไม่ได้ รายการใหม่ยังแชร์ได้ตามปกติ";
            return Array.Empty<HistoryEntry>();
        }
    }
    public async Task<bool> SaveAsync(IEnumerable<HistoryEntry> entries)
    {
        var snapshot = entries.Take(200).ToArray();
        await gate.WaitAsync();
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            // A short file lease serializes independent DropScan processes without UI blocking.
            await using var lease = await AcquireLease();
            var previous = Load();
            if (Error != null && File.Exists(Path)) File.Copy(Path, Path + ".invalid-" + Guid.NewGuid().ToString("N"));
            var merged = previous.Concat(snapshot).GroupBy(e => e.Id).Select(g => g.OrderByDescending(e => e.UpdatedAt).First()).OrderByDescending(e => e.CreatedAt).Take(200).ToArray();
            temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, Path, true);
            temporary = null;
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Error = "บันทึกประวัติไม่ได้ ตรวจพื้นที่ว่างหรือสิทธิ์โฟลเดอร์ • การแชร์ยังทำงาน";
            return false;
        }
        finally
        {
            if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            gate.Release();
        }
    }
    async Task<FileStream> AcquireLease()
    {
        for (int i = 0; ; i++)
        {
            try { return new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException) when (i < 10) { await Task.Delay(50); }
        }
    }
}
