using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DropScan.Sharing;

public sealed class LocalShareServer : IAsyncDisposable
{
    const string AccessCookie = "dropscan_access";
    const string ApprovalCookie = "dropscan_request";
    readonly WebApplication app;
    readonly bool trustCloudflareClientIp;
    public ShareSession Session { get; }
    public string Url { get; private set; } = "";
    LocalShareServer(WebApplication app, ShareSession session, bool trustCloudflareClientIp)
    {
        this.app = app;
        Session = session;
        this.trustCloudflareClientIp = trustCloudflareClientIp;
    }

    public static async Task<LocalShareServer> StartAsync(ShareSession session, IPAddress address, int port = 0, bool trustCloudflareClientIp = false)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders(); // Do not log secret URLs or local paths.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(address, port);
            options.Limits.MaxRequestBodySize = 4096;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        var server = new LocalShareServer(builder.Build(), session, trustCloudflareClientIp);
        server.Configure();
        try
        {
            await server.app.StartAsync();
            var addresses = server.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            server.Url = addresses.Single().TrimEnd('/') + "/s/" + session.Token;
            return server;
        }
        catch { await server.app.DisposeAsync(); throw; }
    }

    void Configure()
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            await next(context);
        });
        app.MapMethods("/s/{token}", new[] { "GET", "HEAD" }, async (HttpContext context, string token) =>
        {
            if (!await AuthorizeToken(context, token)) return;
            context.Response.ContentType = "text/html; charset=utf-8";
            if (HttpMethods.IsHead(context.Request.Method)) return;
            await context.Response.WriteAsync(ReceiverLanding(context), context.RequestAborted);
        });
        app.MapPost("/s/{token}/pin", VerifyPin);
        app.MapPost("/s/{token}/request", RequestApproval);
        app.MapPost("/s/{token}/request/cancel", CancelApproval);
        app.MapMethods("/s/{token}/f/{id:int}", new[] { "GET", "HEAD" }, Download);
        app.MapMethods("/s/{token}/all.zip", new[] { "GET", "HEAD" }, DownloadZip);
        app.MapFallback(async context => await Error(context, 404, "ไม่พบรายการแชร์", "ตรวจสอบลิงก์หรือขอ QR ใหม่จากผู้ส่ง"));
    }

    async Task<bool> AuthorizeToken(HttpContext context, string token)
    {
        // Compare fixed-size random tokens without exposing path or file metadata on failure.
        if (token.Length != Session.Token.Length || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(Session.Token)))
        {
            await Error(context, 404, "ไม่พบรายการแชร์", "ตรวจสอบลิงก์หรือขอ QR ใหม่จากผู้ส่ง");
            return false;
        }
        if (!Session.IsAvailable)
        {
            await Error(context, 410, Session.EndReason!, "ขอให้ผู้ส่งสร้างรายการใหม่เพื่อรับไฟล์");
            return false;
        }
        return true;
    }

    bool IsPinAuthorized(HttpContext context) => !Session.PinEnabled || Session.ValidateAccessGrant(context.Request.Cookies[AccessCookie]);
    bool IsApprovalAuthorized(HttpContext context) => !Session.ApprovalRequired || Session.GetApprovalState(context.Request.Cookies[ApprovalCookie]) == ApprovalRequestState.Approved;
    string ReceiverLanding(HttpContext context)
    {
        if (!IsPinAuthorized(context)) return ReceiverPage.PinPrompt(Session.Token);
        if (!Session.ApprovalRequired) return ReceiverPage.Render(Session);
        return Session.GetApprovalState(context.Request.Cookies[ApprovalCookie]) switch
        {
            ApprovalRequestState.Approved => ReceiverPage.Render(Session),
            ApprovalRequestState.Pending => ReceiverPage.ApprovalWaiting(Session.Token),
            ApprovalRequestState.Denied => ReceiverPage.ApprovalEnded(Session.Token, "ผู้ส่งไม่อนุมัติคำขอ", "คุณส่งคำขอใหม่ได้ หากผู้ส่งแจ้งให้ลองอีกครั้ง"),
            ApprovalRequestState.Cancelled => ReceiverPage.ApprovalEnded(Session.Token, "ยกเลิกคำขอแล้ว", "คุณสามารถส่งคำขอใหม่ได้เมื่อพร้อม"),
            ApprovalRequestState.Expired => ReceiverPage.ApprovalEnded(Session.Token, "คำขอหมดอายุแล้ว", "คำขอมีอายุ 5 นาที กรุณาส่งคำขอใหม่"),
            _ => ReceiverPage.ApprovalPrompt(Session.Token)
        };
    }
    async Task<bool> AuthorizeDownload(HttpContext context, string token)
    {
        if (!await AuthorizeToken(context, token)) return false;
        if (!IsPinAuthorized(context))
        {
            await Error(context, 401, "ต้องยืนยัน PIN ก่อน", "เปิดหน้ารับไฟล์จาก QR แล้วกรอก PIN ที่ได้รับจากผู้ส่ง");
            return false;
        }
        if (!IsApprovalAuthorized(context))
        {
            await Error(context, 403, "ยังไม่ได้รับอนุมัติ", "เปิดหน้ารับไฟล์และส่งคำขอให้ผู้ส่งอนุมัติก่อนดาวน์โหลด");
            return false;
        }
        return true;
    }
    async Task VerifyPin(HttpContext context, string token)
    {
        if (!await AuthorizeToken(context, token)) return;
        if (!Session.PinEnabled) { context.Response.Redirect($"/s/{token}"); return; }
        string pin = "";
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            pin = form["pin"].ToString().Trim();
        }
        string clientKey = ClientIp(context)?.ToString() ?? "unknown";
        var result = Session.VerifyPin(clientKey, pin, out int remaining);
        if (result == PinAttemptResult.Allowed)
        {
            context.Response.Cookies.Append(AccessCookie, Session.AccessGrant, new CookieOptions
            {
                HttpOnly = true, Secure = IsForwardedHttps(context), SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict, Path = $"/s/{token}", Expires = Session.ExpiresAt
            });
            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location = $"/s/{token}";
            return;
        }
        context.Response.StatusCode = result == PinAttemptResult.Locked ? 429 : 401;
        if (result == PinAttemptResult.Locked) context.Response.Headers.RetryAfter = "60";
        context.Response.ContentType = "text/html; charset=utf-8";
        string message = result == PinAttemptResult.Locked ? "ลองผิดครบ 5 ครั้ง กรุณารอ 60 วินาทีแล้วลองใหม่" : $"PIN ไม่ถูกต้อง เหลือลองได้อีก {remaining} ครั้ง";
        await context.Response.WriteAsync(ReceiverPage.PinPrompt(token, message), context.RequestAborted);
    }

    async Task RequestApproval(HttpContext context, string token)
    {
        if (!await AuthorizeToken(context, token)) return;
        if (!IsPinAuthorized(context))
        {
            await Error(context, 401, "ต้องยืนยัน PIN ก่อน", "กลับไปหน้ารับไฟล์แล้วกรอก PIN ก่อนส่งคำขอ");
            return;
        }
        if (!Session.ApprovalRequired) { RedirectToShare(context, token); return; }
        var existing = Session.GetApprovalState(context.Request.Cookies[ApprovalCookie]);
        if (existing is ApprovalRequestState.Pending or ApprovalRequestState.Approved) { RedirectToShare(context, token); return; }
        string client = ClientIp(context)?.ToString() ?? "ไม่ทราบ IP";
        var grant = Session.CreateApprovalRequest(client, client.Length > 64 ? client[..64] : client);
        if (grant == null)
        {
            await Error(context, 429, "มีคำขอรออยู่มากเกินไป", "รอให้ผู้ส่งจัดการคำขอที่ค้างอยู่ แล้วลองใหม่");
            return;
        }
        context.Response.Cookies.Append(ApprovalCookie, grant.Secret, new CookieOptions
        {
            HttpOnly = true, Secure = IsForwardedHttps(context), SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict, Path = $"/s/{token}", Expires = Session.ExpiresAt
        });
        RedirectToShare(context, token);
    }

    async Task CancelApproval(HttpContext context, string token)
    {
        if (!await AuthorizeToken(context, token)) return;
        if (!IsPinAuthorized(context))
        {
            await Error(context, 401, "ต้องยืนยัน PIN ก่อน", "กลับไปหน้ารับไฟล์แล้วกรอก PIN อีกครั้ง");
            return;
        }
        Session.CancelApprovalRequest(context.Request.Cookies[ApprovalCookie]);
        context.Response.Cookies.Delete(ApprovalCookie, new CookieOptions { Path = $"/s/{token}" });
        RedirectToShare(context, token);
    }

    static void RedirectToShare(HttpContext context, string token)
    {
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = $"/s/{token}";
    }

    static bool IsForwardedHttps(HttpContext context) =>
        string.Equals(context.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.OrdinalIgnoreCase);

    IPAddress? ClientIp(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (trustCloudflareClientIp && remoteIp != null && IPAddress.IsLoopback(remoteIp) &&
            IPAddress.TryParse(context.Request.Headers["CF-Connecting-IP"].ToString(), out var forwardedIp))
            return forwardedIp;
        return remoteIp;
    }

    async Task Download(HttpContext context, string token, int id)
    {
        if (!await AuthorizeDownload(context, token)) return;
        if (id < 0 || id >= Session.Files.Count || Session.Files[id].IsDirectory) { await Error(context, 404, "ไม่พบไฟล์", "เลือกไฟล์จากหน้ารับไฟล์อีกครั้ง"); return; }
        var file = Session.Files[id];
        FileStream stream;
        try { stream = SharedFileAccess.Open(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Error(context, 409, "ไฟล์เปลี่ยนแปลงหรืออ่านไม่ได้", "ขอให้ผู้ส่งสร้างรายการใหม่ ไม่สามารถต่อไฟล์เวอร์ชันเก่าได้"); return;
        }
        await using (stream)
        {
            if (!Session.IsAvailable) { await Error(context, 410, "รายการสิ้นสุดแล้ว", "ขอรายการใหม่จากผู้ส่ง"); return; }
            context.Response.Headers.ETag = file.ETag;
            context.Response.Headers.LastModified = new DateTimeOffset(file.LastWriteUtc).ToString("R");
            context.Response.Headers.AcceptRanges = "bytes";
            string? rangeHeader = HttpMethods.IsHead(context.Request.Method) ? null : context.Request.Headers.Range.ToString();
            var ifRange = context.Request.Headers.IfRange.ToString();
            if (!string.IsNullOrEmpty(ifRange) && ifRange != file.ETag) rangeHeader = null;
            var range = DownloadRange.Parse(rangeHeader, file.Length);
            if (range == null)
            {
                context.Response.Headers.ContentRange = $"bytes */{file.Length}";
                await Error(context, 416, "ช่วงข้อมูลไม่ถูกต้อง", "เริ่มดาวน์โหลดใหม่จากหน้ารับไฟล์"); return;
            }
            if (HttpMethods.IsHead(context.Request.Method)) { SetFileHeaders(context, file.Name, file.Length); return; }
            var transfer = Session.Begin(file, context.RequestAborted, range.Length);
            if (transfer == null) { await Busy(context); return; }
            string result = "การเชื่อมต่อขาด • ผู้รับลองดาวน์โหลดต่อได้";
            bool success = false;
            try
            {
                SetFileHeaders(context, file.Name, range.Length);
                if (range.Partial)
                {
                    context.Response.StatusCode = 206;
                    context.Response.Headers.ContentRange = $"bytes {range.Start}-{range.Start + range.Length - 1}/{file.Length}";
                }
                stream.Position = range.Start;
                long remaining = range.Length;
                var buffer = new byte[65536];
                while (remaining > 0)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), transfer.Cancellation.Token);
                    if (read == 0) throw new IOException("ไฟล์สั้นกว่ารายการที่แชร์");
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), transfer.Cancellation.Token);
                    await context.Response.Body.FlushAsync(transfer.Cancellation.Token);
                    remaining -= read;
                    Session.Progress(transfer, read);
                }
                transfer.Cancellation.Token.ThrowIfCancellationRequested();
                await context.Response.CompleteAsync();
                success = true;
                result = range.Partial ? "ส่งช่วงข้อมูลครบแล้ว • ตรวจไฟล์ที่เครื่องผู้รับ" : "ส่งข้อมูลครบแล้ว • ตรวจไฟล์ที่เครื่องผู้รับ";
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { context.Abort(); }
            finally { Session.Finish(transfer, result, success); }
        }
    }

    async Task DownloadZip(HttpContext context, string token)
    {
        if (!await AuthorizeDownload(context, token)) return;
        if (HttpMethods.IsHead(context.Request.Method))
        {
            SetZipHeaders(context); return;
        }
        var aggregate = new SharedFile(-1, "", "DropScan.zip", Session.Files.Sum(f => f.Length), DateTime.UtcNow);
        var transfer = Session.Begin(aggregate, context.RequestAborted, aggregate.Length, true);
        if (transfer == null) { await Busy(context); return; }
        bool success = false;
        string result = "การส่ง ZIP ขาด • กรุณาดาวน์โหลด ZIP ใหม่";
        try
        {
            // Preflight before headers. Open again at each entry to detect changes during packaging.
            foreach (var file in Session.Files.Where(f => !f.IsDirectory))
            {
                transfer.Cancellation.Token.ThrowIfCancellationRequested();
                using var check = SharedFileAccess.Open(file);
            }
            SetZipHeaders(context);
            using var output = new ZipResponseStream(context.Response.Body, transfer.Cancellation.Token);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
            {
                var buffer = new byte[65536];
                foreach (var file in Session.Files)
                {
                    transfer.Cancellation.Token.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(file.ArchivePath, CompressionLevel.Fastest);
                    if (file.IsDirectory) continue;
                    await using var input = SharedFileAccess.Open(file);
                    await using var destination = entry.Open();
                    int read;
                    while ((read = await input.ReadAsync(buffer, transfer.Cancellation.Token)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), transfer.Cancellation.Token);
                        Session.Progress(transfer, read);
                    }
                }
            }
            await context.Response.Body.FlushAsync(transfer.Cancellation.Token);
            transfer.Cancellation.Token.ThrowIfCancellationRequested();
            await context.Response.CompleteAsync();
            success = true;
            result = "ส่ง ZIP ครบแล้ว • ตรวจไฟล์ที่เครื่องผู้รับ";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
        {
            if (!context.Response.HasStarted && !transfer.Cancellation.IsCancellationRequested)
            {
                result = "สร้าง ZIP ไม่สำเร็จ • มีไฟล์เปลี่ยนไปหรืออ่านไม่ได้";
                await Error(context, 409, "สร้าง ZIP ไม่สำเร็จ", "ขอให้ผู้ส่งตรวจไฟล์และสร้างรายการใหม่");
            }
            else context.Abort();
        }
        finally { Session.Finish(transfer, result, success); }
    }
    Task Busy(HttpContext context) => Error(context, Session.IsAvailable ? 429 : 410, "ยังรับไฟล์ไม่ได้", "มีการรับไฟล์พร้อมกันครบ 3 รายการ หรือรายการแชร์สิ้นสุดแล้ว");
    static void SetFileHeaders(HttpContext context, string name, long? length)
    {
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = length;
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(name);
        context.Response.Headers.ContentDisposition = disposition.ToString();
    }
    static void SetZipHeaders(HttpContext context)
    {
        SetFileHeaders(context, "DropScan.zip", null);
        context.Response.ContentType = "application/zip";
        context.Response.Headers.AcceptRanges = "none";
    }    static async Task Error(HttpContext context, int status, string title, string description)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.WriteAsync(ReceiverPage.Error(title, description), context.RequestAborted);
    }
    public async ValueTask DisposeAsync()
    {
        Session.Stop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(timeout.Token); }
        catch (OperationCanceledException) { }
        finally { await app.DisposeAsync(); Session.Dispose(); }
    }
}


