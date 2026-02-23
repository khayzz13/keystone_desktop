// Window Registry - Tracks window types and their rendering metadata
// Apps register window types at startup (via ICorePlugin or keystone.json).
// PluginRegistry handles plugin instances; this stores static metadata per type.

using Keystone.Core.Plugins;

namespace Keystone.Core.Runtime;

/// <summary>
/// Describes what a window contains. Not mutually exclusive —
/// every window plugin can render native GPU content, embed web components, or both.
/// </summary>
[Flags]
public enum WindowFeatures
{
    None      = 0,
    NativeGpu = 1 << 0,   // Uses RenderContext / scene graph
    WebContent = 1 << 1,  // Embeds one or more Bun web components
    NativeChrome = 1 << 2, // Has native title bar / toolbar chrome
}

/// <summary>
/// Static metadata for a registered window type.
/// Populated by PluginRegistry when plugins register, or by config at startup.
/// </summary>
public record WindowTypeInfo(
    string WindowType,
    WindowFeatures Features,
    float DefaultWidth = 800,
    float DefaultHeight = 600
);

/// <summary>
/// Registry of known window types and their metadata.
/// Separate from PluginRegistry (which owns live plugin instances) —
/// this is the static catalog of what window types exist and how they render.
/// </summary>
public static class WindowRegistry
{
    private static readonly Dictionary<string, WindowTypeInfo> _types = new();

    /// <summary>Register or update a window type.</summary>
    public static void Register(string windowType, WindowFeatures features, float width = 800, float height = 600)
    {
        _types[windowType] = new WindowTypeInfo(windowType, features, width, height);
    }

    /// <summary>Register from a plugin's metadata.</summary>
    public static void RegisterFromPlugin(IWindowPlugin plugin)
    {
        var (w, h) = plugin.DefaultSize;
        _types[plugin.WindowType] = new WindowTypeInfo(plugin.WindowType, WindowFeatures.NativeGpu, w, h);
    }

    /// <summary>Get metadata for a window type, or null if not registered.</summary>
    public static WindowTypeInfo? Get(string windowType)
        => _types.TryGetValue(windowType, out var info) ? info : null;

    /// <summary>Check if a window type is registered.</summary>
    public static bool IsRegistered(string windowType) => _types.ContainsKey(windowType);

    /// <summary>All registered window type names.</summary>
    public static IEnumerable<string> RegisteredTypes => _types.Keys;
}
