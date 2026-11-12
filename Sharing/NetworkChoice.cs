using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DropScan.Sharing;

public sealed record NetworkChoice(string Name, IPAddress Address)
{
    public bool IsTunnel { get; init; }
    public string Label => $"{Name} — {Address}";
    public static IReadOnlyList<NetworkChoice> Find()
    {
        var result = new List<NetworkChoice>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            foreach (var address in nic.GetIPProperties().UnicastAddresses.Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)))
                result.Add(new(nic.Name, address));
        }
        result.Add(new("ข้ามเครือข่าย — Cloudflare Quick Tunnel (ทดลอง)", IPAddress.Loopback) { IsTunnel = true });
        result.Add(new("ทดสอบบนเครื่องนี้เท่านั้น", IPAddress.Loopback));
        return result;
    }
}
