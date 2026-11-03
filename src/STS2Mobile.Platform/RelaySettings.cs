namespace STS2Mobile.Platform;

/// <summary>
/// QuickMatch's relay host, read from the <c>QuickMatchRelay.cfg</c> built into the app. Whoever builds the app chooses it
/// (<c>QUICKMATCH_RELAY</c> or <c>.local/QuickMatchRelay.cfg</c> when running setup); it is never in a tracked file. The file uses the desktop companion's format: <c>key = value</c> lines and <c>#</c> comments,
/// with <c>relay = https://host</c> (or <c>wss://host</c>) and <c>enabled = true|false</c>. Unlike the companion there is no
/// built-in default: no file, no <c>relay</c> line, <c>enabled = false</c> or any problem in the file means no relay, and
/// QuickMatch keeps to lobby chat. The address is a host name on port 443 and nothing else.
/// </summary>
public static class RelaySettings
{
    public const string FileName = "QuickMatchRelay.cfg";

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
                problem($"{FileName} line {number}: expected key = value");
                broken = true;
                continue;
            }
            string key = line[..equals].Trim().ToLowerInvariant();
            string value = line[(equals + 1)..].Trim();
            if (!seen.Add(key))
            {
                problem($"{FileName} line {number}: {key} is set twice");
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
                    problem($"{FileName} line {number}: enabled is true or false, not \"{value}\"");
                    broken = true;
                    break;
                case "relay":
                    if (TryParseHost(value, out host, out string? why)) break;
                    problem($"{FileName} line {number}: relay address {why}");
                    broken = true;
                    break;
                default:
                    problem($"{FileName} line {number}: unknown setting \"{key}\" (known: enabled, relay)");
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
