namespace STS2Mobile.Platform;

/// <summary>The host in an imported mod's optional &lt;id&gt;.cfg file.</summary>
public static class RelaySettings
{
    internal static string? ApprovedHost(string inputsDirectory, string id, IRelayPermissions permissions)
    {
        if (!ModFiles.IsValidId(id)) return null;
        string folder = Path.Combine(inputsDirectory, id);
        string cfg = Path.Combine(folder, id + ".cfg");
        if (!File.Exists(cfg)) return null;
        string? host = ParseHost(File.ReadAllText(cfg), _ => { });
        return host != null && permissions.Read(id) == "+" + host ? host : null;
    }

    /// <summary>The relay host the file names, or null for none; each problem goes to <paramref name="problem"/>.</summary>
    public static string? ParseHost(string? text, Action<string> problem)
    {
        if (text == null) return null;
        bool enabled = true, broken = false;
        string? host = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string[] lines = text.Split('\n');
        for (int number = 1; number <= lines.Length; number++)
        {
            string line = lines[number - 1].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                problem($"Relay config line {number}: expected key = value");
                broken = true;
                continue;
            }
            string key = line[..equals].Trim().ToLowerInvariant();
            string value = line[(equals + 1)..].Trim();
            if (!seen.Add(key))
            {
                problem($"Relay config line {number}: {key} is set twice");
                broken = true;
                continue;
            }
            switch (key)
            {
                case "enabled" when value.ToLowerInvariant() is "true" or "on" or "yes":
                    break;
                case "enabled" when value.ToLowerInvariant() is "false" or "off" or "no":
                    enabled = false;
                    break;
                case "enabled":
                    problem($"Relay config line {number}: enabled is true or false, not \"{value}\"");
                    broken = true;
                    break;
                case "relay":
                    if (TryParseHost(value, out host, out string? why)) break;
                    problem($"Relay config line {number}: relay address {why}");
                    broken = true;
                    break;
                default:
                    problem($"Relay config line {number}: unknown setting \"{key}\" (known: enabled, relay)");
                    broken = true;
                    break;
            }
        }
        return enabled && !broken ? host : null;
    }

    /// <summary>The host of <c>https://host</c> or <c>wss://host</c> (a trailing slash and :443 are fine); false with the reason otherwise.</summary>
    public static bool TryParseHost(string value, out string? host, out string? why)
    {
        host = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            why = "is not an address";
            return false;
        }
        why = uri switch
        {
            _ when uri.Scheme is not ("https" or "wss") => "must start with https:// or wss://",
            _ when uri.Port != 443 => "must use port 443",
            _ when uri.UserInfo.Length > 0 => "may not hold a user name or password",
            _ when uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 => "is a host name only, without a path or query",
            _ when uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.') || uri.IdnHost.EndsWith('.') => "must be a host name, not an IP address",
            _ => null,
        };
        if (why != null) return false;
        host = uri.IdnHost.ToLowerInvariant();
        return true;
    }
}
