using System.IO;
using System.Diagnostics;
using System.Security.Principal;

namespace AlienLink;

public static class HostEnv
{
    public static bool IsAdmin
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public static string? GetAdapterDescription()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.Description.Contains("ETAlienLAN", StringComparison.OrdinalIgnoreCase))
                    return AdapterBranding.AppAdapterName;
            }
        }
        catch { }
        return null;
    }

    public static string CoreVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        }
        catch { return ""; }
    }
}

