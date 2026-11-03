using System.Net;

namespace STS2Mobile.Platform;

/// <summary>The host-name checks the game and relay policies share.</summary>
internal static class HostNames
{
    /// <summary>
    /// A plain DNS name: ASCII letters, digits, dots and hyphens, at most 253 characters, every label 1 to 63 characters and not
    /// starting or ending with a hyphen. Never an IP address.
    /// </summary>
    public static bool IsPlainDnsName(string? host) =>
        !string.IsNullOrEmpty(host) && host.Length <= 253 && !IPAddress.TryParse(host, out _) &&
        host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
        host.Split('.').All(label => label.Length is > 0 and <= 63 && !label.StartsWith('-') && !label.EndsWith('-'));

    /// <summary>The host is one of <paramref name="roots"/> or a name under one, ignoring case.</summary>
    public static bool IsUnder(string host, IEnumerable<string> roots) =>
        roots.Any(root => host.Equals(root, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase));

    /// <summary>The request's Host header, when it has one, names another host than its address (with or without port 443).</summary>
    public static bool OverridesHost(HttpRequestMessage request) =>
        request.Headers.Host is { } host &&
        !host.Equals(request.RequestUri!.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        !host.Equals(request.RequestUri.IdnHost + ":443", StringComparison.OrdinalIgnoreCase);
}
