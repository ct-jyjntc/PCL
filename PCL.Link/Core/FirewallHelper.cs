using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AlienLink;

public static class FirewallHelper
{
    private static int _done;
    public static void AllowProgram(string exe)
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        Add(exe, "Kernel", null);
        if (Environment.ProcessPath is { } app) Add(app, "Discovery", "4445,45888,45889");
    }

    private static void Add(string exe, string role, string? ports)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exe)))[..12];
        var name = $"PCL Link {role} {id}";
        // Rules belong to this installation; do not replace unrelated rules.
        if (Run("advfirewall", "firewall", "show", "rule", $"name={name}") == 0) return;
        var args = new List<string> { "advfirewall", "firewall", "add", "rule", $"name={name}",
            "dir=in", "action=allow", $"program={exe}", "enable=yes", "profile=any" };
        if (ports is not null) args.AddRange(["protocol=UDP", $"localport={ports}", "remoteip=LocalSubnet,127.0.0.1"]);
        Run(args.ToArray());
    }

    private static int Run(params string[] args)
    {
        var start = new ProcessStartInfo("netsh.exe") { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Windows Firewall configuration.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(8000)) { process.Kill(); throw new TimeoutException("Firewall configuration timed out."); }
        return process.ExitCode;
    }
}
