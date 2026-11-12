using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace DropScan.Sharing;

public sealed class CloudflareQuickTunnel : IDisposable
{
    static readonly Regex UrlPattern = new(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    readonly Process process;
    public string Url { get; }
    public bool IsRunning => !process.HasExited;

    CloudflareQuickTunnel(Process process, string url)
    {
        this.process = process;
        Url = url;
    }

    public static async Task<CloudflareQuickTunnel> StartAsync(string localOrigin, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo
        {
            FileName = "cloudflared",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("tunnel");
        start.ArgumentList.Add("--no-autoupdate");
        start.ArgumentList.Add("--url");
        start.ArgumentList.Add(localOrigin);

        Process child;
        try { child = Process.Start(start) ?? throw new InvalidOperationException("Cloudflare Tunnel could not be started."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new FileNotFoundException("cloudflared was not found. Install Cloudflare Tunnel and restart DropScan.", ex); }

        var output = System.Threading.Channels.Channel.CreateUnbounded<string>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        child.OutputDataReceived += (_, e) => { if (e.Data != null) output.Writer.TryWrite(e.Data); };
        child.ErrorDataReceived += (_, e) => { if (e.Data != null) output.Writer.TryWrite(e.Data); };
        child.Exited += (_, _) => output.Writer.TryComplete();
        child.EnableRaisingEvents = true;
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(35));
        try
        {
            while (await output.Reader.WaitToReadAsync(deadline.Token))
            {
                while (output.Reader.TryRead(out var line))
                {
                    var match = UrlPattern.Match(line);
                    if (match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) && uri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase))
                        return new(child, uri.GetLeftPart(UriPartial.Authority));
                }
            }
            throw new InvalidOperationException("cloudflared stopped before it provided a tunnel URL.");
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException("Cloudflare Quick Tunnel did not become ready within the allowed time.", ex);
        }
        catch
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            child.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        process.Dispose();
    }
}
