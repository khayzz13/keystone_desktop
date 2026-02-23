// KeystoneConfig — app manifest loaded from keystone.config.json (or keystone.json).
// Runtime reads everything except the "build" section (build-time only, absorbed by JsonExtensionData).

using System.Text.Json;
using System.Text.Json.Serialization;
using Keystone.Core.Management.Bun;

namespace Keystone.Core;

public class KeystoneConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Keystone App";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "com.keystone.app";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("appAssembly")]
    public string? AppAssembly { get; set; }

    [JsonPropertyName("windows")]
    public List<WindowConfig> Windows { get; set; } = new();

    [JsonPropertyName("plugins")]
    public PluginConfig Plugins { get; set; } = new();

    [JsonPropertyName("scripts")]
    public ScriptConfig Scripts { get; set; } = new();

    [JsonPropertyName("bun")]
    public BunConfig? Bun { get; set; }

    [JsonPropertyName("iconDir")]
    public string IconDir { get; set; } = "icons";

    [JsonPropertyName("menus")]
    public Dictionary<string, List<MenuItemConfig>>? Menus { get; set; }

    [JsonPropertyName("processRecovery")]
    public ProcessRecoveryConfig ProcessRecovery { get; set; } = new();

    [JsonPropertyName("workers")]
    public List<BunWorkerConfig>? Workers { get; set; }

    // Legacy compat — maps to Plugins.Dir
    [JsonPropertyName("pluginDir")]
    public string? PluginDir { get; set; }

    // Legacy compat — maps to Scripts.Dir
    [JsonPropertyName("scriptDir")]
    public string? ScriptDir { get; set; }

    // Absorb unknown keys (e.g. "build") without failing deserialization.
    // The packager writes a "build" section — runtime ignores it.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Config file search order: keystone.config.json → keystone.json.</summary>
    public static readonly string[] ConfigFileNames = { "keystone.config.json", "keystone.json" };

    /// <summary>Find the config file in a directory using the standard search order.</summary>
    public static string? FindConfigFile(string directory)
    {
        foreach (var name in ConfigFileNames)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static KeystoneConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}");

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<KeystoneConfig>(json, _jsonOptions);
        if (config == null)
            throw new InvalidOperationException($"Failed to deserialize config: {path}");

        ApplyLegacyCompat(config);
        Validate(config, path);
        return config;
    }

    /// <summary>Load from a directory — searches keystone.config.json then keystone.json.</summary>
    public static KeystoneConfig LoadFromDirectory(string directory)
    {
        var path = FindConfigFile(directory);
        if (path == null)
            throw new FileNotFoundException($"No config file found in {directory}");
        return Load(path);
    }

    public static KeystoneConfig LoadOrDefault(string directory)
    {
        var path = FindConfigFile(directory);
        if (path == null) return new KeystoneConfig();
        try { return Load(path); }
        catch (UnauthorizedAccessException) { return new KeystoneConfig(); }
        catch (IOException) { return new KeystoneConfig(); }
    }

    private static void ApplyLegacyCompat(KeystoneConfig config)
    {
        if (config.PluginDir != null)
            config.Plugins.Dir = config.PluginDir;
        if (config.ScriptDir != null)
            config.Scripts.Dir = config.ScriptDir;
    }

    private static void Validate(KeystoneConfig config, string path)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            throw new InvalidOperationException($"Config: 'name' is required ({path})");
        if (string.IsNullOrWhiteSpace(config.Id))
            throw new InvalidOperationException($"Config: 'id' is required ({path})");
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };
}

// === Plugin (DyLib) configuration ===

public class PluginConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("dir")]
    public string Dir { get; set; } = "dylib";

    [JsonPropertyName("hotReload")]
    public bool HotReload { get; set; } = true;

    /// <summary>Debounce interval in ms for file watcher events.</summary>
    [JsonPropertyName("debounceMs")]
    public int DebounceMs { get; set; } = 200;

    /// <summary>File patterns to watch. Default: *.dll</summary>
    [JsonPropertyName("watchPatterns")]
    public List<string>? WatchPatterns { get; set; }

    /// <summary>
    /// Publisher-managed plugin directory — loaded in addition to dir.
    /// For signed plugin updates pushed outside the app bundle. Same signing team.
    /// Hot-reloadable in production if hotReload is true.
    /// Supports ~ expansion, $APP_SUPPORT, absolute, or relative to app root.
    ///
    /// Dev: "dylib" (relative to appRoot — hot-reload against source build)
    /// Prod: "~/Library/Application Support/MyApp/plugins" or "$APP_SUPPORT/plugins"
    /// </summary>
    [JsonPropertyName("userDir")]
    public string? UserDir { get; set; }

    /// <summary>
    /// Community/third-party extension directory — loaded after dir and userDir.
    /// For plugins signed by other teams or unsigned community extensions.
    /// Requires allowExternalSignatures = true to load DLLs not signed by the app developer.
    /// Hot-reloadable if hotReload is true.
    /// Supports ~ expansion, $APP_SUPPORT, absolute, or relative to app root.
    ///
    /// Typical: "$APP_SUPPORT/extensions" or "~/Library/Application Support/MyApp/extensions"
    /// </summary>
    [JsonPropertyName("extensionDir")]
    public string? ExtensionDir { get; set; }

    /// <summary>
    /// Native dylib directory for app-specific native libraries (Go, Rust, C, etc.).
    /// Registered as a NativeLibrary search path at startup. Relative to app root.
    /// Framework native libs (libkeystone_layout) are always found in the exe directory.
    /// null = no app-specific native libs.
    /// </summary>
    [JsonPropertyName("nativeDir")]
    public string? NativeDir { get; set; }

    /// <summary>
    /// When true, disables macOS library validation — allows loading plugin DLLs signed
    /// by any team, not just the app's own Developer ID. Required for extensionDir to
    /// load community plugins. Incompatible with Mac App Store.
    /// </summary>
    [JsonPropertyName("allowExternalSignatures")]
    public bool AllowExternalSignatures { get; set; } = false;
}

// === Script (CSX) configuration ===

public class ScriptConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("dir")]
    public string Dir { get; set; } = "scripts";

    [JsonPropertyName("hotReload")]
    public bool HotReload { get; set; } = true;

    /// <summary>Web scripts subdirectory (relative to root, not script dir).</summary>
    [JsonPropertyName("webDir")]
    public string? WebDir { get; set; }

    /// <summary>Auto-create script directory if it doesn't exist.</summary>
    [JsonPropertyName("autoCreateDir")]
    public bool AutoCreateDir { get; set; } = true;
}

// === Bun runtime configuration ===
// The Bun process has its own config file (keystone.config.ts) in the app's bun root.
// C# only controls whether to start the process and where to find it.

public class BunConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("root")]
    public string Root { get; set; } = "bun";

    /// <summary>
    /// Name of the compiled single-file executable (produced by bun build --compile) to spawn
    /// instead of using the system bun binary. Set by the packaging build step. Resolved relative
    /// to the host binary directory.
    /// </summary>
    [JsonPropertyName("compiledExe")]
    public string? CompiledExe { get; set; }
}

// === Window, toolbar, and menu configs (unchanged) ===

public class WindowConfig
{
    [JsonPropertyName("component")]
    public string Component { get; set; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("width")]
    public float Width { get; set; } = 800;

    [JsonPropertyName("height")]
    public float Height { get; set; } = 600;

    [JsonPropertyName("spawn")]
    public bool Spawn { get; set; } = true;

    [JsonPropertyName("toolbar")]
    public ToolbarConfig? Toolbar { get; set; }
}

public class ToolbarConfig
{
    [JsonPropertyName("items")]
    public List<ToolbarItemConfig> Items { get; set; } = new();
}

public class ToolbarItemConfig
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class MenuItemConfig
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("shortcut")]
    public string? Shortcut { get; set; }
}

// === Process recovery configuration ===
// Controls automatic restart behavior for the Bun subprocess and WebKit content process.
// Mirror of Electron's BrowserWindow crashRecovery / app.on('render-process-gone') model.

public class ProcessRecoveryConfig
{
    /// <summary>Whether to automatically restart Bun when it crashes. Default: true.</summary>
    [JsonPropertyName("bunAutoRestart")]
    public bool BunAutoRestart { get; set; } = true;

    /// <summary>Maximum number of Bun restart attempts before giving up. Default: 5.</summary>
    [JsonPropertyName("bunMaxRestarts")]
    public int BunMaxRestarts { get; set; } = 5;

    /// <summary>Base delay in ms before the first restart attempt (doubles each attempt). Default: 500.</summary>
    [JsonPropertyName("bunRestartBaseDelayMs")]
    public int BunRestartBaseDelayMs { get; set; } = 500;

    /// <summary>Maximum delay cap in ms between restart attempts. Default: 30000 (30s).</summary>
    [JsonPropertyName("bunRestartMaxDelayMs")]
    public int BunRestartMaxDelayMs { get; set; } = 30_000;

    /// <summary>Whether to automatically reload a WKWebView when its content process terminates. Default: true.</summary>
    [JsonPropertyName("webViewAutoReload")]
    public bool WebViewAutoReload { get; set; } = true;

    /// <summary>Delay in ms before reloading after a WebView content process crash. Default: 200.</summary>
    [JsonPropertyName("webViewReloadDelayMs")]
    public int WebViewReloadDelayMs { get; set; } = 200;
}
