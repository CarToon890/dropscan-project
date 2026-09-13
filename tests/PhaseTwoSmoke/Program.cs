using DropScan;
using DropScan.Sharing;
using DropScan.Desktop;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

internal static class Program
{
    static int checks;
    static void Check(bool ok, string description) { if (!ok) throw new Exception(description); checks++; }
    static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    static async Task<int> Main()
    {
        try
        {
            string root = Path.GetFullPath("artifacts/phase2-qa");
            Directory.CreateDirectory(root);
            string folder = Path.Combine(root, "โฟลเดอร์ทดสอบ");
            Directory.CreateDirectory(Path.Combine(folder, "ย่อย"));
            Directory.CreateDirectory(Path.Combine(folder, "ว่าง"));
            string source = Path.Combine(folder, "ย่อย", "ข้อมูล.txt");
            byte[] content = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
            await File.WriteAllBytesAsync(source, content);
            string duplicate = Path.Combine(root, "ข้อมูล.txt");
            await File.WriteAllTextAsync(duplicate, "second");
            string duplicate2 = Path.Combine(folder, "ข้อมูล.txt");
            await File.WriteAllTextAsync(duplicate2, "third");
            var scanned = SelectionScanner.Scan(new[] { folder }, Array.Empty<SelectedFile>());
            Check(scanned.Entries.Any(f => f.IsDirectory && f.ArchivePath.EndsWith("ว่าง/")), "Empty folders preserved");
            Check(scanned.Entries.Any(f => f.ArchivePath.Contains("ย่อย/ข้อมูล.txt")), "Folder hierarchy preserved");
            Check(SelectionScanner.Scan(new[] { folder }, scanned.Entries).Entries.Count == 0, "Repeated folder selection deduplicates");
            string junction = Path.Combine(root, "junction-root");
            if (Directory.Exists(junction)) Check(SelectionScanner.Scan(new[] { junction }, Array.Empty<SelectedFile>()).Skipped == 1, "Junction root rejected");
            else throw new Exception("Prepare junction-root test fixture before running");
            var selected = scanned.Entries.Concat(new[] { new SelectedFile(duplicate, "ข้อมูล.txt", 6), new SelectedFile(duplicate2, "ข้อมูล.txt", 5) }).ToArray();
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var session = new ShareSession(selected, TimeSpan.FromMinutes(1));
            var zipArtifactsBefore = Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await using (var server = await LocalShareServer.StartAsync(session, IPAddress.Loopback))
            {
                using var zipResponse = await client.GetAsync(server.Url + "/all.zip", HttpCompletionOption.ResponseHeadersRead);
                Check(zipResponse.Content.Headers.ContentType?.MediaType == "application/zip", "ZIP content type");
                Check(zipResponse.Content.Headers.ContentLength == null, "ZIP streamed without precomputed length");
                using var bytes = new MemoryStream(await zipResponse.Content.ReadAsByteArrayAsync());
                using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
                Check(archive.Entries.Count == session.Files.Count, "ZIP includes all selected entries");
                Check(archive.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == archive.Entries.Count, "ZIP entry names unique");
                Check(archive.GetEntry("โฟลเดอร์ทดสอบ/ว่าง/") != null, "Empty folder archive entry");
                using var received = new MemoryStream();
                await archive.GetEntry("โฟลเดอร์ทดสอบ/ย่อย/ข้อมูล.txt")!.Open().CopyToAsync(received);
                Check(SHA256.HashData(received.ToArray()).SequenceEqual(SHA256.HashData(content)), "Extracted nested file hash matches");
                Check(zipArtifactsBefore.SetEquals(Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories)), "No sender ZIP artifact created");
                var id = session.Files.Single(f => f.Path == source).Id;
                using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Url + $"/f/{id}"));
                string etag = head.Headers.ETag!.ToString();
                Check(head.Headers.AcceptRanges.Contains("bytes"), "Range advertised");
                var first = await GetRange(server.Url + $"/f/{id}", "bytes=0-1999", etag);
                var rest = await GetRange(server.Url + $"/f/{id}", "bytes=2000-", etag);
                Check(first.StatusCode == HttpStatusCode.PartialContent && rest.StatusCode == HttpStatusCode.PartialContent, "Resume returns 206");
                Check(first.Content.Headers.ContentRange!.From == 0 && rest.Content.Headers.ContentRange!.From == 2000, "Content-Range offsets");
                byte[] combined = (await first.Content.ReadAsByteArrayAsync()).Concat(await rest.Content.ReadAsByteArrayAsync()).ToArray();
                Check(SHA256.HashData(combined).SequenceEqual(SHA256.HashData(content)), "Resumed file hash matches");
                using var suffix = await GetRange(server.Url + $"/f/{id}", "bytes=-10");
                Check((await suffix.Content.ReadAsByteArrayAsync()).SequenceEqual(content.TakeLast(10)), "Suffix range");
                using var invalid = await GetRange(server.Url + $"/f/{id}", "bytes=999999999-");
                Check((int)invalid.StatusCode == 416 && invalid.Content.Headers.ContentRange!.Length == content.Length, "Unsatisfiable range 416");
                using var stale = await GetRange(server.Url + $"/f/{id}", "bytes=2000-", "\"wrong-version\"");
                Check(stale.StatusCode == HttpStatusCode.OK && (await stale.Content.ReadAsByteArrayAsync()).SequenceEqual(content), "Stale If-Range returns full response");
                using var multi = await GetRange(server.Url + $"/f/{id}", "bytes=0-1,3-4");
                Check(multi.StatusCode == HttpStatusCode.OK, "Unsupported multi-range returns full response");
                using var headRange = new HttpRequestMessage(HttpMethod.Head, server.Url + $"/f/{id}");
                headRange.Headers.Range = new RangeHeaderValue(0, 1);
                using var headResult = await client.SendAsync(headRange);
                Check(headResult.StatusCode == HttpStatusCode.OK && headResult.Content.Headers.ContentLength == content.Length, "HEAD ignores Range");
                session.Stop();
                using var stopped = await GetRange(server.Url + $"/f/{id}", "bytes=0-10", etag);
                using var stoppedZip = await client.GetAsync(server.Url + "/all.zip");
                Check(stopped.StatusCode == HttpStatusCode.Gone && stoppedZip.StatusCode == HttpStatusCode.Gone, "Stop denies both ZIP and range");
                first.Dispose(); rest.Dispose();
            }
            var duplicateSession = new ShareSession(new[] { duplicate, duplicate2 }, TimeSpan.FromMinutes(1));
            Check(duplicateSession.Files.Select(f => f.ArchivePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "Duplicate base names renamed");
            duplicateSession.Dispose();

            // Incompressible data forces a streamed ZIP to remain active without consuming the body.
            string large = Path.Combine(root, "large-random.bin");
            await using (var f = File.Create(large)) for (int i = 0; i < 64; i++) await f.WriteAsync(content);
            var cancelSession = new ShareSession(new[] { large }, TimeSpan.FromMinutes(1));
            await using (var server = await LocalShareServer.StartAsync(cancelSession, IPAddress.Loopback))
            {
                using var response = await client.GetAsync(server.Url + "/all.zip", HttpCompletionOption.ResponseHeadersRead);
                await WaitUntil(() => cancelSession.Snapshot().BytesSent > 0);
                Check(cancelSession.Snapshot().IsArchive, "ZIP progress uses source bytes");
                cancelSession.CancelTransfers();
                await WaitUntil(() => cancelSession.Snapshot().ActiveCount == 0);
                Check(cancelSession.IsAvailable && cancelSession.Snapshot().CompletedCount == 0, "Cancel ZIP does not complete or revoke link");
                using var retry = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Url + "/all.zip"));
                Check(retry.IsSuccessStatusCode, "ZIP retry available");
            }
            var expires = new ShareSession(new[] { large }, TimeSpan.FromSeconds(2));
            await using (var server = await LocalShareServer.StartAsync(expires, IPAddress.Loopback))
            {
                using var response = await client.GetAsync(server.Url + "/all.zip", HttpCompletionOption.ResponseHeadersRead);
                await WaitUntil(() => !expires.IsAvailable && expires.Snapshot().ActiveCount == 0);
                Check(expires.Snapshot().CompletedCount == 0, "Expiry cancels ZIP");
            }
            var changed = new ShareSession(new[] { source }, TimeSpan.FromMinutes(1));
            await File.AppendAllTextAsync(source, "modified");
            await using (var server = await LocalShareServer.StartAsync(changed, IPAddress.Loopback))
            {
                using var range = await GetRange(server.Url + "/f/0", "bytes=10-");
                using var zip = await client.GetAsync(server.Url + "/all.zip");
                Check(range.StatusCode == HttpStatusCode.Conflict && zip.StatusCode == HttpStatusCode.Conflict, "Changed file blocks ZIP and resume");
            }
            string historyPath = Path.Combine(root, "history-" + Guid.NewGuid().ToString("N") + ".json");
            var store = new HistoryStore(historyPath);
            var record = new ShareRecord { Code = "http://secret-host/s/secret-token", Session = null, FileCount = 3, TotalBytes = 999, Expiry = "24 ชั่วโมง" };
            Check(await store.SaveAsync(new[] { record.ToEntry() }), "History saves");
            var json = await File.ReadAllTextAsync(historyPath);
            Check(!json.Contains("secret") && !json.Contains("Code") && !json.Contains("Path"), "History excludes tokens URLs and paths");
            var loaded = store.Load();
            Check(loaded.Count == 1 && loaded[0].FileCount == 3, "History survives new store");
            var restored = ShareRecord.FromEntry(loaded[0]);
            Check(restored.Session == null && restored.Code == "" && restored.IsStopped, "History cannot reopen a share");
            var many = Enumerable.Range(0, 250).Select(i => new HistoryEntry(Guid.NewGuid(), DateTime.Now.AddMinutes(i), DateTime.UtcNow, 1, i, "1 ชั่วโมง", "ส่งข้อมูลครบ"));
            Check(await store.SaveAsync(many) && store.Load().Count == 200, "History limit 200");
            await File.WriteAllTextAsync(historyPath, "broken json");
            Check(store.Load().Count == 0 && store.Error != null, "Corrupt history degrades gracefully");
            Check(await store.SaveAsync(new[] { record.ToEntry() }) && Directory.GetFiles(root, Path.GetFileName(historyPath) + ".invalid-*").Length == 1, "Corrupt history preserved before recovery");
            Console.WriteLine($"PASS: {checks} phase-two checks: ZIP/folders/junction/range/cancellation/expiry/history.");
            return 0;

            async Task<HttpResponseMessage> GetRange(string url, string range, string? etag = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Range", range);
                if (etag != null) request.Headers.TryAddWithoutValidation("If-Range", etag);
                return await client.SendAsync(request);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

