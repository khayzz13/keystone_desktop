// NetworkPolicy — endpoint allow-list enforcement for outbound network access.
// Initialized from keystone.config.json security.network config at startup.
// Plugins can merge additional endpoints via INetworkDeclarer.

using System;
using System.Collections.Generic;

namespace Keystone.Core.Security;

public static class NetworkPolicy
{
    private static HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private static List<string> _wildcards = new();
    private static bool _enforcing;

    public static bool Enforcing => _enforcing;

    public static void Initialize(SecurityConfig config, bool isPackaged)
    {
        var mode = config.Network.Mode.ToLowerInvariant();
        _enforcing = mode == "allowlist" || (mode == "auto" && isPackaged);

        _allowed.Clear();
        _wildcards.Clear();

        if (!_enforcing) return;

        foreach (var ep in config.Network.AllowedEndpoints)
            AddEndpoint(ep);

        // Loopback is always allowed
        AddEndpoint("127.0.0.1");
        AddEndpoint("localhost");
    }

    public static void AddEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (trimmed.Length == 0) return;

        if (trimmed.StartsWith("*."))
            _wildcards.Add(trimmed[1..]); // store ".reddit.com"
        else
            _allowed.Add(trimmed);
    }

    public static bool IsAllowed(string hostAndPort)
    {
        if (!_enforcing) return true;
        if (_allowed.Contains(hostAndPort)) return true;

        // Strip port and check hostname-only
        var host = hostAndPort;
        var lastColon = hostAndPort.LastIndexOf(':');
        if (lastColon > 0) host = hostAndPort[..lastColon];
        if (_allowed.Contains(host)) return true;

        // Check wildcard suffixes
        foreach (var w in _wildcards)
            if (host.EndsWith(w, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    public static bool IsAllowed(Uri uri)
        => IsAllowed(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}");

    /// <summary>Serialize the resolved allow-list for passing to Bun via env var.</summary>
    public static string Serialize()
    {
        if (!_enforcing) return "";
        var all = new List<string>(_allowed);
        foreach (var w in _wildcards) all.Add("*" + w);
        return string.Join(",", all);
    }
}
