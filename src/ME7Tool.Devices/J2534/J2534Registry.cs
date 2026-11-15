using Microsoft.Win32;

namespace ME7Tool.Devices.J2534;

public sealed record J2534DeviceInfo(string Name, string FunctionLibrary);

/// <summary>Enumera os dispositivos J2534 instalados (registados em HKLM, norma 04.04).</summary>
public static class J2534Registry
{
    private static readonly string[] Roots =
    {
        @"SOFTWARE\PassThruSupport.04.04",
        @"SOFTWARE\WOW6432Node\PassThruSupport.04.04",
    };

    public static List<J2534DeviceInfo> List()
    {
        var found = new List<J2534DeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in Roots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key == null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var d = key.OpenSubKey(sub);
                var lib = d?.GetValue("FunctionLibrary") as string;
                if (string.IsNullOrWhiteSpace(lib) || !seen.Add(lib)) continue;
                var name = d?.GetValue("Name") as string ?? sub;
                found.Add(new J2534DeviceInfo(name, lib));
            }
        }
        return found;
    }
}
