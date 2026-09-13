using DropScan.Sharing;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

internal static class Program
{
    static int checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    static HttpClient Client(out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = true, CookieContainer = cookies });
    }

    static async Task<int> Main(string[] args)
    {
        if (args.Contains("--preview", StringComparer.OrdinalIgnoreCase)) return await RunPreview();
        string root = Path.GetFullPath("artifacts/approval-qa");
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "ไฟล์หลังอนุมัติ.txt");
        byte[] payload = RandomNumberGenerator.GetBytes(256 * 1024 + 17);
        await File.WriteAllBytesAsync(source, payload);
        try
        {
            using (var open = new ShareSession(new[] { source }, TimeSpan.FromMinutes(1)))
            await using (var server = await LocalShareServer.StartAsync(open, IPAddress.Loopback))
            using (var client = Client(out _))
            {
                Check(!open.ApprovalRequired, "Approval remains optional");
                Check((await client.GetAsync(server.Url + "/f/0")).IsSuccessStatusCode, "Open share remains backward compatible");
            }

            using (var session = new ShareSession(new[] { source }, TimeSpan.FromMinutes(1), approvalRequired: true))
            await using (var server = await LocalShareServer.StartAsync(session, IPAddress.Loopback))
            using (var client = Client(out var cookies))
            using (var stranger = Client(out _))
            {
                string prompt = await client.GetStringAsync(server.Url);
                Check(prompt.Contains("ส่งคำขอรับไฟล์") && !prompt.Contains("ไฟล์หลังอนุมัติ.txt"), "Approval prompt hides file metadata");

                using var direct = await client.GetAsync(server.Url + "/f/0");
                Check(direct.StatusCode == HttpStatusCode.Forbidden && !(await direct.Content.ReadAsStringAsync()).Contains("ไฟล์หลังอนุมัติ.txt"), "Direct file is denied without approval and hides metadata");
                using var zipBefore = await client.GetAsync(server.Url + "/all.zip");
                Check(zipBefore.StatusCode == HttpStatusCode.Forbidden, "ZIP is denied without approval");
                using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, server.Url + "/f/0");
                rangeRequest.Headers.Range = new RangeHeaderValue(20, 39);
                using var rangeBefore = await client.SendAsync(rangeRequest);
                Check(rangeBefore.StatusCode == HttpStatusCode.Forbidden, "Range is denied without approval");
                using var headBefore = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Url + "/f/0"));
                Check(headBefore.StatusCode == HttpStatusCode.Forbidden && headBefore.Content.Headers.ContentLength != payload.Length, "HEAD cannot reveal size before approval");

                using var request = await client.PostAsync(server.Url + "/request", null);
                Check(request.StatusCode == HttpStatusCode.SeeOther, "Recipient request redirects to status page");
                string requestCookie = string.Join(";", request.Headers.GetValues("Set-Cookie"));
                Check(requestCookie.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase) && requestCookie.Contains("SameSite=Strict", StringComparison.OrdinalIgnoreCase), "Request cookie is HttpOnly and SameSite Strict");
                Check(cookies.GetCookies(new Uri(server.Url))["dropscan_request"] is { Value.Length: 64 }, "Browser receives a random request grant");
                using var duplicateRequest = await client.PostAsync(server.Url + "/request", null);
                Check(duplicateRequest.StatusCode == HttpStatusCode.SeeOther && session.SnapshotApprovalRequests().Count == 1, "Repeated submit from the same browser does not duplicate a live request");
                string waiting = await client.GetStringAsync(server.Url);
                Check(waiting.Contains("ส่งคำขอแล้ว") && waiting.Contains("ยกเลิกคำขอ") && !waiting.Contains("ไฟล์หลังอนุมัติ.txt"), "Waiting page supports cancel and hides metadata");
                Check(waiting.Contains("http-equiv='refresh' content='3'"), "Waiting page refreshes approval state automatically");

                var pending = session.SnapshotApprovalRequests();
                Check(pending.Count == 1 && pending[0].IsPending && pending[0].ClientLabel == "127.0.0.1", "Sender sees one pending request with client IP");
                Check(session.ApproveRequest(pending[0].Id), "Sender can approve pending request");
                Check(!session.ApproveRequest(pending[0].Id) && !session.DenyRequest(pending[0].Id), "Terminal request cannot be decided twice");
                string page = await client.GetStringAsync(server.Url);
                Check(page.Contains("ไฟล์หลังอนุมัติ.txt") && page.Contains("ผู้ส่งอนุมัติคำขอนี้แล้ว"), "Approved browser sees file list");
                Check((await stranger.GetAsync(server.Url + "/f/0")).StatusCode == HttpStatusCode.Forbidden, "Approval is scoped to the requesting browser");
                using var file = await client.GetAsync(server.Url + "/f/0");
                Check(file.IsSuccessStatusCode && SHA256.HashData(await file.Content.ReadAsByteArrayAsync()).SequenceEqual(SHA256.HashData(payload)), "Approved file matches source hash");
                using var rangeAfterRequest = new HttpRequestMessage(HttpMethod.Get, server.Url + "/f/0");
                rangeAfterRequest.Headers.Range = new RangeHeaderValue(100, 199);
                using var rangeAfter = await client.SendAsync(rangeAfterRequest);
                Check(rangeAfter.StatusCode == HttpStatusCode.PartialContent && (await rangeAfter.Content.ReadAsByteArrayAsync()).SequenceEqual(payload[100..200]), "Approved Range returns exact bytes");
                using var zipAfter = await client.GetAsync(server.Url + "/all.zip");
                await using var zipStream = await zipAfter.Content.ReadAsStreamAsync();
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                Check(zipAfter.IsSuccessStatusCode && archive.Entries.Single().FullName == "ไฟล์หลังอนุมัติ.txt", "Approved ZIP is available");
                session.Stop();
                Check((await client.GetAsync(server.Url + "/f/0")).StatusCode == HttpStatusCode.Gone, "Stopping share invalidates approved request");
            }

            using (var limited = new ShareSession(new[] { source }, TimeSpan.FromMinutes(1), approvalRequired: true))
            await using (var server = await LocalShareServer.StartAsync(limited, IPAddress.Loopback))
            {
                bool accepted = true;
                for (int i = 0; i < 32; i++)
                {
                    using var client = Client(out _);
                    using var response = await client.PostAsync(server.Url + "/request", null);
                    accepted &= response.StatusCode == HttpStatusCode.SeeOther;
                }
                Check(accepted && limited.SnapshotApprovalRequests().Count == 32, "Up to 32 pending browser requests are accepted");
                using var overflowClient = Client(out _);
                using var overflow = await overflowClient.PostAsync(server.Url + "/request", null);
                Check((int)overflow.StatusCode == 429, "Thirty-third pending request is rate limited");
            }

            using (var deniedSession = new ShareSession(new[] { source }, TimeSpan.FromMinutes(1), approvalRequired: true))
            await using (var server = await LocalShareServer.StartAsync(deniedSession, IPAddress.Loopback))
            using (var client = Client(out _))
            {
                await client.PostAsync(server.Url + "/request", null);
                var pending = deniedSession.SnapshotApprovalRequests().Single();
                Check(deniedSession.DenyRequest(pending.Id), "Sender can deny request");
                string page = await client.GetStringAsync(server.Url);
                Check(page.Contains("ผู้ส่งไม่อนุมัติคำขอ") && page.Contains("ส่งคำขอใหม่"), "Denied recipient can request again without file metadata");
                Check((await client.GetAsync(server.Url + "/f/0")).StatusCode == HttpStatusCode.Forbidden, "Denied request cannot download");
            }

            using (var cancelSession = new ShareSession(new[] { source }, TimeSpan.FromMinutes(1), approvalRequired: true))
            await using (var server = await LocalShareServer.StartAsync(cancelSession, IPAddress.Loopback))
            using (var client = Client(out _))
            {
                await client.PostAsync(server.Url + "/request", null);
                using var cancel = await client.PostAsync(server.Url + "/request/cancel", null);
                Check(cancel.StatusCode == HttpStatusCode.SeeOther, "Recipient can cancel request");
                Check(cancelSession.SnapshotApprovalRequests().Single().State == ApprovalRequestState.Cancelled, "Sender state records recipient cancellation");
                Check((await client.GetStringAsync(server.Url)).Contains("ส่งคำขอรับไฟล์"), "Cancelled browser returns to request prompt");
            }

            using (var expiring = new ShareSession(new[] { source }, TimeSpan.FromMinutes(1), approvalRequired: true, approvalRequestLifetime: TimeSpan.FromMilliseconds(250)))
            await using (var server = await LocalShareServer.StartAsync(expiring, IPAddress.Loopback))
            using (var client = Client(out _))
            {
                await client.PostAsync(server.Url + "/request", null);
                await Task.Delay(350);
                Check(expiring.SnapshotApprovalRequests().Single().State == ApprovalRequestState.Expired, "Pending request expires at configured lifetime");
                string page = await client.GetStringAsync(server.Url);
                Check(page.Contains("คำขอหมดอายุแล้ว") && !page.Contains("ไฟล์หลังอนุมัติ.txt"), "Expired page permits a new request and hides metadata");
                Check((await client.GetAsync(server.Url + "/f/0")).StatusCode == HttpStatusCode.Forbidden, "Expired request cannot download");
            }

            using (var combined = new ShareSession(new[] { source }, TimeSpan.FromMinutes(1), "915204", approvalRequired: true))
            await using (var server = await LocalShareServer.StartAsync(combined, IPAddress.Loopback))
            using (var client = Client(out _))
            {
                Check((await client.PostAsync(server.Url + "/request", null)).StatusCode == HttpStatusCode.Unauthorized, "PIN must be completed before requesting approval");
                Check(combined.SnapshotApprovalRequests().Count == 0, "Unauthorized request does not reach sender");
                await client.PostAsync(server.Url + "/pin", new FormUrlEncodedContent(new Dictionary<string, string> { ["pin"] = "915204" }));
                Check((await client.GetStringAsync(server.Url)).Contains("ส่งคำขอรับไฟล์"), "Correct PIN advances to approval prompt");
                await client.PostAsync(server.Url + "/request", null);
                var pending = combined.SnapshotApprovalRequests().Single();
                combined.ApproveRequest(pending.Id);
                Check((await client.GetStringAsync(server.Url)).Contains("ไฟล์หลังอนุมัติ.txt"), "PIN and sender approval compose successfully");
            }

            Console.WriteLine($"PASS: {checks} sender-approval checks (real loopback HTTP).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static async Task<int> RunPreview()
    {
        string source = Path.GetFullPath("artifacts/approval-preview.txt");
        await File.WriteAllTextAsync(source, "DropScan approval browser preview");
        using var session = new ShareSession(new[] { source }, TimeSpan.FromMinutes(10), approvalRequired: true);
        await using var server = await LocalShareServer.StartAsync(session, IPAddress.Loopback);
        Console.WriteLine("PREVIEW_URL=" + server.Url);
        Console.WriteLine("Preview host will approve the first pending request after 8 seconds. Press Ctrl+C to stop.");
        while (session.IsAvailable)
        {
            var pending = session.SnapshotApprovalRequests().FirstOrDefault(r => r.IsPending);
            if (pending != null)
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                session.ApproveRequest(pending.Id);
            }
            await Task.Delay(200);
        }
        return 0;
    }
}
