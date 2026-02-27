// INetworkDeclarer — optional interface for plugins that need network access beyond the app allow-list.
// Implement on IServicePlugin, ICorePlugin, or any plugin interface.
// Declared endpoints are merged into NetworkPolicy during plugin loading.

using System;
using System.Collections.Generic;

namespace Keystone.Core.Security;

public interface INetworkDeclarer
{
    /// <summary>Additional endpoints this plugin needs (merged with the app allow-list).</summary>
    IEnumerable<string> NetworkEndpoints => Array.Empty<string>();

    /// <summary>If true, bypass the allow-list entirely for this plugin.</summary>
    bool NetworkUnrestricted => false;
}
