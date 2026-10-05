using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;

namespace RemoteSupport.Server.Api;

internal static class LanHosting
{
    public static int GetPort(IConfiguration configuration)
        => configuration.GetValue("Server:Port", 5096);

    public static void ListenOnLan(WebApplicationBuilder builder)
    {
        var port = GetPort(builder.Configuration);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
    }

    public static void LogReachableAddresses(int port)
    {
        Log.Information("Server listening on all interfaces, port {Port}", port);
        Log.Information("This PC: http://127.0.0.1:{Port}", port);
        foreach (var ip in GetLanIpv4Addresses())
            Log.Information("On the LAN use: http://{Ip}:{Port}", ip, port);
    }

    public static void TryOpenWindowsFirewall(int port)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var name = "Remote Support Server";
            var args =
                $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port}";
            var start = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            process?.WaitForExit(8000);
            if (process is { ExitCode: 0 })
            {
                Log.Information("Windows Firewall allows TCP {Port} ({Rule})", port, name);
                return;
            }

            Log.Warning(
                "Could not add a firewall rule automatically (run the server as Administrator). " +
                "Other PCs will not connect until you allow TCP {Port}. Command: netsh {Args}",
                port, args);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Windows Firewall rule was not added. Allow TCP {Port} inbound for LAN clients.", port);
        }
    }

    public static IReadOnlyList<string> GetLanIpv4Addresses()
    {
        var list = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(addr.Address))
                    continue;
                list.Add(addr.Address.ToString());
            }
        }

        return list;
    }

    public static bool IsPrivateLanOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host is "127.0.0.1" or "::1")
            return true;

        if (!IPAddress.TryParse(uri.Host, out var ip))
            return false;

        if (IPAddress.IsLoopback(ip))
            return true;

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
            return false;

        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
               || (bytes[0] == 192 && bytes[1] == 168);
    }
}
