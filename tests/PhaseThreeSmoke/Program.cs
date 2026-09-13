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

    static async Task<int> Main()
    {
        try
        {
            string root = Path.GetFullPath("artifacts/phase3-qa");
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "ข้อมูลลับ.txt");
            byte[] payload = RandomNumberGenerator.GetBytes(1024 * 1024 + 19);
            await File.WriteAllBytesAsync(source, payload);

            CheckThrows(() => new ShareSession(new[] { source }, TimeSpan.FromMinutes(1), "12345"), "PIN shorter than six digits rejected");
            CheckThrows(() => new ShareSession(new[] { source }, TimeSpan.FromMinutes(1), "12345x"), "Non-numeric PIN rejected");

            using var plainClient = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
            using (var protectedSession = new ShareSession(new[] { source }, TimeSpan.FromMinutes(1), "482913"))
            await using (var server = await LocalShareServer.StartAsync(protectedSession, IPAddress.Loopback))
            {
                Check(protectedSession.PinEnabled, "Protected session marks PIN enabled");
                string prompt = await plainClient.GetStringAsync(server.Url);
                Check(prompt.Contains("กรอก PIN") && !prompt.Contains("ข้อมูลลับ.txt"), "Locked page asks for PIN and hides file metadata");
                Check(!prompt.Contains("482913"), "PIN is absent from receiver HTML");

                using var direct = await plainClient.GetAsync(server.Url + "/f/0");
                Check(direct.StatusCode == HttpStatusCode.Unauthorized, "Direct file URL requires PIN authorization");
                Check(!(await direct.Content.ReadAsStringAsync()).Contains("ข้อมูลลับ.txt"), "Unauthorized file response hides metadata");
                using var zip = await plainClient.GetAsync(server.Url + "/all.zip");
                Check(zip.StatusCode == HttpStatusCode.Unauthorized, "ZIP requires PIN authorization");
                using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, server.Url + "/f/0");
                rangeRequest.Headers.Range = new RangeHeaderValue(20, 39);
                using var range = await plainClient.SendAsync(rangeRequest);
                Check(range.StatusCode == HttpStatusCode.Unauthorized, "Range request requires PIN authorization");
                using var head = await plainClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Url + "/f/0"));
                Check(head.StatusCode == HttpStatusCode.Unauthorized && head.Content.Headers.ContentLength != payload.Length, "HEAD cannot reveal protected file size");
                using var badToken = await plainClient.GetAsync(server.Url.Replace(protectedSession.Token, new string('0', 64)) + "/f/0");
                Check(badToken.StatusCode == HttpStatusCode.NotFound, "Token is checked before PIN authorization");

                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    using var wrong = await plainClient.PostAsync(server.Url + "/pin", new FormUrlEncodedContent(new Dictionary<string, string> { ["pin"] = "111111" }));
                    Check(wrong.StatusCode == HttpStatusCode.Unauthorized, $"Wrong PIN attempt {attempt} rejected");
                }
                using var fifth = await plainClient.PostAsync(server.Url + "/pin", new FormUrlEncodedContent(new Dictionary<string, string> { ["pin"] = "111111" }));
                Check((int)fifth.StatusCode == 429 && fifth.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(60), "Fifth wrong PIN locks attempts for 60 seconds");
                using var lockedCorrect = await plainClient.PostAsync(server.Url + "/pin", new FormUrlEncodedContent(new Dictionary<string, string> { ["pin"] = "482913" }));
                Check((int)lockedCorrect.StatusCode == 429, "Correct PIN cannot bypass an active lockout");

                Check(protectedSession.VerifyPin("separate-client", "482913", out _) == PinAttemptResult.Allowed, "Rate limit is isolated by client key");
                Check(!protectedSession.ValidateAccessGrant("wrong-cookie"), "Wrong access grant rejected");
            }

            var cookies = new CookieContainer();
            using var authorizedClient = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = true, CookieContainer = cookies });
            using (var session = new ShareSession(new[] { source }, TimeSpan.FromMinutes(1), "640275"))
            await using (var server = await LocalShareServer.StartAsync(session, IPAddress.Loopback))
            {
                using var login = await authorizedClient.PostAsync(server.Url + "/pin", new FormUrlEncodedContent(new Dictionary<string, string> { ["pin"] = "640275" }));
                Check(login.StatusCode == HttpStatusCode.SeeOther && login.Headers.Location?.ToString() == $"/s/{session.Token}", "Correct PIN redirects to receiver page");
                string setCookie = string.Join(";", login.Headers.GetValues("Set-Cookie"));
                Check(setCookie.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase) && setCookie.Contains("SameSite=Strict", StringComparison.OrdinalIgnoreCase), "Access cookie is HttpOnly and SameSite Strict");
                Check(!setCookie.Contains("640275"), "Access cookie does not contain PIN");
                Check(cookies.GetCookies(new Uri(server.Url))["dropscan_access"] is { Value.Length: 64 }, "Browser receives scoped random access grant");

                string page = await authorizedClient.GetStringAsync(server.Url);
                Check(page.Contains("ข้อมูลลับ.txt") && page.Contains("ดาวน์โหลดทั้งหมดเป็น ZIP"), "Authorized page reveals downloads");
                using var file = await authorizedClient.GetAsync(server.Url + "/f/0");
                Check(file.IsSuccessStatusCode && SHA256.HashData(await file.Content.ReadAsByteArrayAsync()).SequenceEqual(SHA256.HashData(payload)), "Authorized file matches source hash");

                using var partialRequest = new HttpRequestMessage(HttpMethod.Get, server.Url + "/f/0");
                partialRequest.Headers.Range = new RangeHeaderValue(100, 199);
                using var partial = await authorizedClient.SendAsync(partialRequest);
                Check(partial.StatusCode == HttpStatusCode.PartialContent && (await partial.Content.ReadAsByteArrayAsync()).SequenceEqual(payload[100..200]), "Authorized Range returns exact bytes");

                using var zipResponse = await authorizedClient.GetAsync(server.Url + "/all.zip");
                await using var zipStream = await zipResponse.Content.ReadAsStreamAsync();
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                var entry = archive.Entries.Single();
                await using var entryStream = entry.Open();
                using var memory = new MemoryStream();
                await entryStream.CopyToAsync(memory);
                Check(zipResponse.IsSuccessStatusCode && SHA256.HashData(memory.ToArray()).SequenceEqual(SHA256.HashData(payload)), "Authorized ZIP contains exact source file");

                session.Stop();
                using var afterStop = await authorizedClient.GetAsync(server.Url + "/f/0");
                Check(afterStop.StatusCode == HttpStatusCode.Gone, "Stopping share invalidates authorized access");
            }

            using var openSession = new ShareSession(new[] { source }, TimeSpan.FromMinutes(1));
            await using (var server = await LocalShareServer.StartAsync(openSession, IPAddress.Loopback))
            {
                Check(!openSession.PinEnabled, "PIN remains optional");
                using var response = await plainClient.GetAsync(server.Url + "/f/0");
                Check(response.IsSuccessStatusCode, "Unprotected shares remain backward compatible");
            }

            Console.WriteLine($"PASS: {checks} phase-three PIN checks (real loopback HTTP).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void CheckThrows(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { Check(true, message); return; }
        throw new Exception(message);
    }
}
