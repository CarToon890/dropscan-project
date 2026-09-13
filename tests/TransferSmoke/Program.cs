using DropScan.Sharing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

internal static class Program
{
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static async Task WaitUntil(Func<bool> predicate, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(20, timeout.Token);
        Check(true, reason);
    }
    static async Task<int> Main()
    {
        try
        {
            var root = Path.GetFullPath("artifacts/network-qa"); Directory.CreateDirectory(root);
            var small = Path.Combine(root, "รายงาน & ตัวอย่าง.txt");
            var empty = Path.Combine(root, "empty.txt");
            var large = Path.Combine(root, "large.bin");
            byte[] payload = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
            await File.WriteAllBytesAsync(small, payload); await File.WriteAllBytesAsync(empty, Array.Empty<byte>());
            using (var file = File.Create(large)) file.SetLength(128L * 1024 * 1024);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
            var session = new ShareSession(new[] { small, empty, large }, TimeSpan.FromMinutes(1));
            await using (var server = await LocalShareServer.StartAsync(session, IPAddress.Loopback))
            {
                string html = await client.GetStringAsync(server.Url);
                Check(html.Contains("&amp;") && !html.Contains(root), "HTML escapes names and hides local paths");
                using var page = await client.GetAsync(server.Url);
                Check(page.Headers.CacheControl?.NoStore == true && page.Headers.Contains("Content-Security-Policy"), "Private response headers");
                using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Url + "/f/0"));
                Check(head.Content.Headers.ContentLength == payload.Length && session.Snapshot().CompletedCount == 0, "HEAD does not count a download");
                using var fileResponse = await client.GetAsync(server.Url + "/f/0");
                byte[] received = await fileResponse.Content.ReadAsByteArrayAsync();
                Check(SHA256.HashData(received).SequenceEqual(SHA256.HashData(payload)), "HTTP download matches source SHA256");
                Check(fileResponse.Content.Headers.ContentDisposition?.DispositionType == "attachment", "Attachment delivery");
                await WaitUntil(() => session.Snapshot().CompletedCount == 1, "Completed after real response");
                Check(session.Snapshot().BytesSent == payload.Length, "Progress is based on transferred bytes");
                using var zero = await client.GetAsync(server.Url + "/f/1");
                Check((await zero.Content.ReadAsByteArrayAsync()).Length == 0, "Zero-byte download");
                using var wrong = await client.GetAsync(server.Url.Replace(session.Token, new string('0', 64)));
                Check(wrong.StatusCode == HttpStatusCode.NotFound, "Wrong token denied");
                using var invalidId = await client.GetAsync(server.Url + "/f/999");
                Check(invalidId.StatusCode == HttpStatusCode.NotFound, "Unselected file denied");
                using var traversal = await client.GetAsync(server.Url + "/f/../../App.xaml");
                Check(traversal.StatusCode == HttpStatusCode.NotFound, "Traversal denied");

                using var slow = await client.GetAsync(server.Url + "/f/2", HttpCompletionOption.ResponseHeadersRead);
                await WaitUntil(() => session.Snapshot().ActiveCount == 1 && session.Snapshot().BytesSent > 0, "Live progress");
                session.CancelTransfers();
                await WaitUntil(() => session.Snapshot().ActiveCount == 0, "Sender cancel aborts stream");
                Check(session.IsAvailable && session.Snapshot().Status.Contains("ยกเลิก"), "Cancel leaves link usable");
                using var retry = await client.GetAsync(server.Url + "/f/1");
                Check(retry.IsSuccessStatusCode, "Retry after cancellation");

                using (var disconnected = await client.GetAsync(server.Url + "/f/2", HttpCompletionOption.ResponseHeadersRead)) { }
                await WaitUntil(() => session.Snapshot().ActiveCount == 0, "Disconnect releases transfer");
                Check(session.Snapshot().Status.Contains("ขาด"), "Disconnected state is not complete");
                using var concurrent1 = await client.GetAsync(server.Url + "/f/2", HttpCompletionOption.ResponseHeadersRead);
                using var concurrent2 = await client.GetAsync(server.Url + "/f/2", HttpCompletionOption.ResponseHeadersRead);
                using var concurrent3 = await client.GetAsync(server.Url + "/f/2", HttpCompletionOption.ResponseHeadersRead);
                await WaitUntil(() => session.Snapshot().ActiveCount == 3, "Three simultaneous downloads");
                using var overLimit = await client.GetAsync(server.Url + "/f/2");
                Check((int)overLimit.StatusCode == 429, "Concurrency bound");
                session.Stop();
                await WaitUntil(() => session.Snapshot().ActiveCount == 0, "Stop cancels all active transfers");
                using var stopped = await client.GetAsync(server.Url + "/f/0");
                Check(stopped.StatusCode == HttpStatusCode.Gone, "Stopped link denied");
            }
            using (var checkSource = File.OpenRead(small)) Check(SHA256.HashData(checkSource).SequenceEqual(SHA256.HashData(payload)), "Source unchanged after transfers");
            var expires = new ShareSession(new[] { large }, TimeSpan.FromSeconds(2));
            await using (var server = await LocalShareServer.StartAsync(expires, IPAddress.Loopback))
            {
                using var downloading = await client.GetAsync(server.Url + "/f/0", HttpCompletionOption.ResponseHeadersRead);
                await WaitUntil(() => !expires.IsAvailable && expires.Snapshot().ActiveCount == 0, "Expiry cancels active download");
                using var expired = await client.GetAsync(server.Url);
                Check(expired.StatusCode == HttpStatusCode.Gone && expires.EndReason == "หมดอายุแล้ว", "Expiry denies receiver page");
            }
            var changed = new ShareSession(new[] { small }, TimeSpan.FromMinutes(1));
            await File.AppendAllTextAsync(small, "changed");
            await using (var server = await LocalShareServer.StartAsync(changed, IPAddress.Loopback))
            {
                using var response = await client.GetAsync(server.Url + "/f/0");
                Check(response.StatusCode == HttpStatusCode.Conflict, "Changed file rejected");
            }
            Console.WriteLine($"PASS: {checks} network assertions (real loopback HTTP). No firewall changes.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
