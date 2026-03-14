/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
#if MACOS
using AppKit;
using Keystone.Core.Platform.MacOS;
#endif
using Keystone.Core;
using Keystone.Core.Management;
using Keystone.Core.Rendering;
using Keystone.Core.Management.Bun;
using Keystone.Core.Platform;
using Keystone.Core.Plugins;
using Keystone.Core.Security;

namespace Keystone.Core.Runtime;

/// <summary>
/// Main application runtime coordinator.
/// Vsync-driven loop: WaitForVsync → ProcessEvents (rendering runs on per-window background threads).
/// Constructor is side-effect-free. Call Initialize() then Run().
/// </summary>
public class ApplicationRuntime : ICoreContext
{
    private static ApplicationRuntime? _instance;
    public static ApplicationRuntime? Instance => _instance;

    private readonly KeystoneConfig _config;
    private readonly string _rootDir;
    private readonly IPlatform _platform;
    private readonly WindowManager _windowManager;
    private readonly PluginRegistry _pluginRegistry;
    private readonly ActionRouter _actionRouter;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IDisplayLink _displayLink;
    private readonly ConcurrentQueue<Action> _pendingActions = new();

    private DyLibLoader? _loader;
    private DyLibLoader? _userLoader;
    private DyLibLoader? _extensionLoader;
    private ScriptManager? _scriptManager;
    private ProcessSupervisor? _processSupervisor;
    private readonly HttpRouter _httpRouter = new();
    private readonly ThreadPoolManager _threadPoolManager = new();
    private readonly IpcHub _ipcHub = new();
    private int _windowCounter;
    private int _memCheckCounter;
    private long _lastPurgeFootprint;
    private bool _postStartupGcPolicyEnabled;
    private int _postStartupGcPassesRemaining;
    private int _nextPostStartupGcFrame;
    private const int MemCheckIntervalFrames = 600; // ~10s at 60fps
    private const int PostStartupGcIntervalFrames = 3600; // ~60s at 60fps
    private const int PostStartupGcPassCount = 5;
    private const long MemoryLimitBytes = 8L * 1024 * 1024 * 1024; // 8 GB

    public WindowManager WindowManager => _windowManager;
    public IDisplayLink DisplayLink => _displayLink;
    public PluginRegistry PluginRegistry => _pluginRegistry;
    public KeystoneConfig Config => _config;
    public string RootDir => _rootDir;

    // Lifecycle events — apps subscribe in ICorePlugin.Initialize()
    public event Action? OnInitialized;
    public event Action? OnBeforeRun;
    public event Action? OnShutdown;

    // System power events — forwarded from IPlatform
    public event Action? OnSystemWillSleep;
    public event Action? OnSystemDidWake;

    // Process lifecycle events
    /// <summary>Fired when the Bun subprocess exits unexpectedly. Arg is the OS exit code.</summary>
    public event Action<int>? OnBunCrash;
    /// <summary>Fired after Bun successfully restarts following a crash. Arg is the restart attempt number (1-based).</summary>
    public event Action<int>? OnBunRestart;
    /// <summary>Fired when a WKWebView content process terminates unexpectedly. Arg is the window Id.</summary>
    public event Action<string>? OnWebViewCrash;

    // Unified crash observability — forwards from CrashReporter
    public event Action<CrashEvent>? OnCrash
    {
        add => CrashReporter.OnCrash += value;
        remove => CrashReporter.OnCrash -= value;
    }

    // App activation events
    public event Action<string[], string>? OnSecondInstance;
    public event Action<string[]>? OnOpenUrls;
    public event Action<string>? OnOpenFile;

    internal void RaiseWebViewCrash(string windowId)
    {
        CrashReporter.Report("webview_crash", null, new() { ["windowId"] = windowId });
        OnWebViewCrash?.Invoke(windowId);
    }

    private readonly DateTime _startTime = DateTime.UtcNow;

#if MACOS
    private Keystone.Core.Platform.MacOS.KeystoneSchemeHandler? _schemeHandler;
#endif

    // Web worker tracking — headless windows spawned via worker:spawn
    private readonly HashSet<string> _workerIds = new();

    // ICoreContext implementation
    Action<string, string>? ICoreContext.OnUnhandledAction
    {
        set => ActionRouter.OnUnhandledAction += value;
    }

    object? ICoreContext.BeginPreventSleep(string reason) => _platform.BeginPreventSleep(reason);
    void ICoreContext.EndPreventSleep(object? token) => _platform.EndPreventSleep(token);

    void ICoreContext.RegisterService<T>(T service) => ServiceLocator.Register(service);
    void ICoreContext.RegisterWindow(IWindowPlugin plugin) => _pluginRegistry.RegisterWindow(plugin);
    void ICoreContext.RegisterService(IServicePlugin plugin) => _pluginRegistry.RegisterService(plugin);
    void ICoreContext.RunOnMainThread(Action action) => RunOnMainThread(action);
    void ICoreContext.RunOnMainThreadAndWait(Action action) => RunOnMainThreadAndWait(action);
    IBunService ICoreContext.Bun => BunManager.Instance;
    IBunWorkerManager ICoreContext.Workers => BunWorkerManager.Instance;
    IHttpRouter ICoreContext.Http => _httpRouter;
    IThreadPoolManager ICoreContext.ThreadPools => _threadPoolManager;
    IChannelManager ICoreContext.Channels => ChannelManager.Instance;
    IIpcFacade ICoreContext.Ipc => _ipcHub;

    public ApplicationRuntime(KeystoneConfig config, string rootDir, IPlatform platform)
    {
        _config = config;
        _rootDir = rootDir;
        _platform = platform;
        _pluginRegistry = new PluginRegistry();
        _windowManager = new WindowManager(platform);
        _windowManager.OnSpawnWindow = SpawnWindow;
        _windowManager.OnSpawnWindowAt = SpawnWindowAt;
        _windowManager.OnQuitApp = () => _cancellation.Cancel();
#if MACOS
        _displayLink = new DisplayLink();
#else
        _displayLink = new TimerDisplayLink();
#endif
        _actionRouter = new ActionRouter(_windowManager);
        _processSupervisor = new ProcessSupervisor(new ProcessSupervisor.Config(
            Recovery: config.ProcessRecovery,
            Workers: config.Workers,
            CompiledWorkerExe: config.Bun?.CompiledWorkerExe,
            RunOnMainThread: RunOnMainThread,
            OnBunCrash: code => OnBunCrash?.Invoke(code),
            OnBunRestart: attempt => OnBunRestart?.Invoke(attempt),
            ExecuteAction: action => _actionRouter.Execute(action, "web"),
            HotSwapAllSlots: component => { foreach (var w in _windowManager.GetAllWindows()) w.HotSwapSlot(component); },
#if MACOS
            OnSchemePortReady: port => _schemeHandler?.SetBunPort(port),
#else
            OnSchemePortReady: null,
#endif
            OnRuntimeReady: () => { foreach (var core in _pluginRegistry.GetCorePlugins()) core.OnReady(this); },
            Cancellation: _cancellation.Token
        ));
        _instance = this;

        // Forward platform sleep/wake to runtime events
        _platform.OnSystemWillSleep += () => OnSystemWillSleep?.Invoke();
        _platform.OnSystemDidWake += () =>
        {
            OnSystemDidWake?.Invoke();
            // Restart display link if it stopped firing during sleep
            _displayLink.EnsureRunning();
            // Wake all render threads so they resume rendering
            foreach (var w in _windowManager.GetAllWindows())
                w.RequestRedraw();
        };

        // Forward open events from platform to runtime + push to browser
        _platform.OnSecondInstance += (argv, cwd) =>
        {
            OnSecondInstance?.Invoke(argv, cwd);
            BunManager.Instance.Push("__secondInstance__", new { argv, cwd });
            // Bring existing windows to front
            RunOnMainThread(() => _platform.BringAllWindowsToFront());
        };
        _platform.OnOpenUrls += urls =>
        {
            OnOpenUrls?.Invoke(urls);
            foreach (var url in urls)
                BunManager.Instance.Push("__openUrl__", new { url });
        };
        _platform.OnOpenFile += path =>
        {
            OnOpenFile?.Invoke(path);
            BunManager.Instance.Push("__openFile__", new { path });
        };

        // Wire Flex layout renderer
        Keystone.Core.UI.FlexNode.RenderImpl = FlexRenderer.Render;
        Keystone.Core.UI.FlexNode.RegisterButtonsImpl = FlexRenderer.RegisterButtons;

        // Wire web component resolver — bridges Core → Management without circular dependency
        Keystone.Core.UI.Flex.WebComponentResolver = name => BunManager.Instance.WebComponentUrl(name);
    }

    /// <summary>
    /// Full bootstrap sequence. Call once after construction, before Run().
    /// Order: persistence → icons → dock/menu → app assembly → plugins → scripts → bun
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "App assembly loading requires dynamic assembly loading")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "App assembly loading requires dynamic type instantiation")]
    public void Initialize()
    {
        // Single-instance lock — if another instance is running, forward argv and exit
        if (!_platform.TryAcquireSingleInstanceLock(_config.Id))
        {
            Console.WriteLine("[ApplicationRuntime] Another instance is running — forwarded argv and exiting");
            Environment.Exit(0);
        }

        // Expose app identity to child processes (Bun subprocess uses these for process.title, userData paths, etc.)
        Environment.SetEnvironmentVariable("KEYSTONE_APP_NAME", _config.Name);
        Environment.SetEnvironmentVariable("KEYSTONE_APP_ID", _config.Id);

        // Network security policy — initialize from config (plugins may merge endpoints via INetworkDeclarer)
        NetworkPolicy.Initialize(_config.Security, _config.Bun?.CompiledExe != null);

        // Push crash events to WebSocket for browser-side diagnostics subscriptions
        CrashReporter.OnCrash += evt =>
        {
            if (BunManager.Instance.IsRunning)
                BunManager.Instance.Push("diagnostics:crash", evt);
        };

        // Custom URL scheme — {schemeName}:// provides stable origin, request interception, SW support
#if MACOS
        if (_config.CustomScheme)
        {
            _schemeHandler = new Keystone.Core.Platform.MacOS.KeystoneSchemeHandler(_config.ResolvedSchemeName);
            Console.WriteLine($"[ApplicationRuntime] Custom scheme enabled — WebViews will use {_config.ResolvedSchemeName}://app/");
        }
#endif

        var pluginDir = Path.Combine(_rootDir, _config.Plugins.Dir);
        var userPluginDir = ResolveUserPluginDir(_config.Plugins.UserDir, _rootDir, _config.Name);
        var extensionPluginDir = ResolveUserPluginDir(_config.Plugins.ExtensionDir, _rootDir, _config.Name);
        var scriptDir = Path.Combine(_rootDir, _config.Scripts.Dir);
        var iconDir = Path.Combine(_rootDir, _config.IconDir);

        // 1. Persistence — app-scoped database
        KeystoneDb.Init(_config.Id);

        // 2. Icons
        if (Directory.Exists(iconDir))
            Icons.Load(iconDir);

        // 3. Dock + menu bar — actions route through ActionRouter
#if MACOS
        if (_platform is MacOSPlatform macPlatform)
            macPlatform.SetAppDelegate(() => _platform.BringAllWindowsToFront());
#endif
        _platform.SetWindowListProvider(() => _windowManager.GetWindowsForDockMenu());
        _platform.InitializeMenu(action => _actionRouter.Execute(action, "menu"), _config);

        // 3b. Global shortcuts
#if MACOS
        GlobalShortcutManager.Initialize(new Keystone.Core.Platform.MacOS.MacOSGlobalShortcut());
#elif WINDOWS
        GlobalShortcutManager.Initialize(new Keystone.Core.Platform.Windows.WindowsGlobalShortcut());
#else
        GlobalShortcutManager.Initialize(new Keystone.Core.Platform.Linux.LinuxGlobalShortcut());
#endif

        // 4. App assembly — loaded into default ALC before plugins for type visibility
        if (_config.AppAssembly != null)
            LoadAppAssembly(Path.Combine(_rootDir, _config.AppAssembly));

        // 5. Hot-reloadable plugins (bundled)
        if (_config.Plugins.Enabled && Directory.Exists(pluginDir))
        {
            _loader = new DyLibLoader(pluginDir, _pluginRegistry);
            _loader.AllowExternalSignatures = _config.Plugins.AllowExternalSignatures;
            _loader.CoreContext = this;
            _loader.LoadAll();
            if (_config.Plugins.HotReload)
                _loader.StartWatching();
        }

        // 5b. User plugin directory — publisher-managed updates external to bundle
        if (_config.Plugins.Enabled && userPluginDir != null && Directory.Exists(userPluginDir))
        {
            _userLoader = new DyLibLoader(userPluginDir, _pluginRegistry);
            _userLoader.AllowExternalSignatures = _config.Plugins.AllowExternalSignatures;
            _userLoader.CoreContext = this;
            _userLoader.LoadAll();
            if (_config.Plugins.HotReload)
                _userLoader.StartWatching();
            Console.WriteLine($"[ApplicationRuntime] User plugins: {userPluginDir}");
        }

        // 5c. Extension directory — community/third-party plugins
        if (_config.Plugins.Enabled && extensionPluginDir != null && Directory.Exists(extensionPluginDir))
        {
            _extensionLoader = new DyLibLoader(extensionPluginDir, _pluginRegistry);
            _extensionLoader.AllowExternalSignatures = _config.Plugins.AllowExternalSignatures;
            _extensionLoader.CoreContext = this;
            _extensionLoader.LoadAll();
            if (_config.Plugins.HotReload)
                _extensionLoader.StartWatching();
            Console.WriteLine($"[ApplicationRuntime] Extensions: {extensionPluginDir}");
        }

        // 6. Scripts
        if (_config.Scripts.Enabled)
        {
            if (_config.Scripts.AutoCreateDir && !Directory.Exists(scriptDir))
                Directory.CreateDirectory(scriptDir);

            if (Directory.Exists(scriptDir))
            {
                _scriptManager = new ScriptManager(scriptDir, _pluginRegistry, _config.Scripts);
                _scriptManager.Start();
            }
        }

        // 7. Populate tools menu from scripts
        var toolScripts = ScriptManager.GetToolScripts();
        if (toolScripts.Length > 0)
            _platform.AddToolScripts(toolScripts);

        // 8. Wire hot-reload coordination
        _windowManager.SubscribeToRegistry(_pluginRegistry);

        // 9. Register WebWindowPlugins from config — before Bun and before SpawnInitialWindows
        foreach (var winCfg in _config.Windows)
        {
            if (_pluginRegistry.GetWindow(winCfg.Component) == null)
                _pluginRegistry.RegisterWindow(new WebWindowPlugin(winCfg));
        }

        // 9b. Forward resolved network policy to Bun subprocess via env
        //     Done after plugin loading so INetworkDeclarer endpoints are merged in.
        Environment.SetEnvironmentVariable("KEYSTONE_NETWORK_MODE", NetworkPolicy.Enforcing ? "allowlist" : "open");
        Environment.SetEnvironmentVariable("KEYSTONE_NETWORK_ENDPOINTS", NetworkPolicy.Serialize());

        // 10. Bun process — engine runtime + app bun root
        var bunConfig = _config.Bun;
        if (bunConfig is { Enabled: true })
        {
            var appBunRoot = Path.Combine(_rootDir, bunConfig.Root);
            var assemblyDir = Path.GetDirectoryName(typeof(ApplicationRuntime).Assembly.Location) ?? _rootDir;

            // Package mode: compiled single-file executable next to the host binary.
            // Dev mode: probe for host.ts and spawn via system bun.
            var exeDir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? assemblyDir;
            string? compiledExe = null;
            if (bunConfig.CompiledExe is { } exeName)
            {
                var macosDir = Path.Combine(assemblyDir, "..", "MacOS");
                foreach (var candidate in new[] {
                    Path.Combine(exeDir, exeName),
                    Path.Combine(assemblyDir, exeName),
                    Path.Combine(macosDir, exeName),
                })
                {
                    if (File.Exists(candidate)) { compiledExe = candidate; break; }
                }
            }

            if (compiledExe != null)
            {
                _processSupervisor!.Start(compiledExe, appBunRoot, compiledExe: compiledExe);
            }
            else
            {
                var resourcesBunDir = Path.Combine(assemblyDir, "..", "Resources", "bun");
                string? engineHostTs = null;
                foreach (var candidate in new[] {
                    Path.Combine(assemblyDir, "bun", "host.ts"),
                    Path.Combine(resourcesBunDir, "host.ts"),
                    Path.Combine(appBunRoot, "node_modules", "keystone-desktop", "host.ts"),
                })
                {
                    if (File.Exists(candidate)) { engineHostTs = candidate; break; }
                }

                if (engineHostTs != null)
                    _processSupervisor!.Start(engineHostTs, appBunRoot);
                else
                    Console.WriteLine("[ApplicationRuntime] WARNING: host.ts not found and no compiled exe configured — Bun runtime disabled");
            }
        }

        OnInitialized?.Invoke();
        Console.WriteLine("[ApplicationRuntime] Initialized");
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "App assembly loading requires dynamic assembly loading")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "App assembly loading requires dynamic type instantiation")]
    private void LoadAppAssembly(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"[ApplicationRuntime] WARNING: App assembly not found: {path}");
            Notifications.Warn($"App assembly not found: {path}");
            return;
        }

        var asm = Assembly.LoadFrom(path);
        Console.WriteLine($"[ApplicationRuntime] Loaded app assembly: {asm.GetName().Name}");

        foreach (var type in asm.GetTypes())
        {
            if (typeof(ICorePlugin).IsAssignableFrom(type) && !type.IsAbstract)
            {
                var core = (ICorePlugin)Activator.CreateInstance(type)!;
                core.Initialize(this);
                _pluginRegistry.RegisterCore(core);
                Console.WriteLine($"[ApplicationRuntime] Initialized core plugin: {core.CoreName}");
            }
        }
    }

    /// <summary>
    /// Resolve plugins.userDir to an absolute path.
    /// Supports ~ expansion, absolute paths, and paths relative to appRoot.
    /// Returns null if userDir is null/empty.
    /// </summary>
    private static string? ResolveUserPluginDir(string? userDir, string appRoot, string appName)
    {
        if (string.IsNullOrEmpty(userDir)) return null;

        // Expand ~ to home directory
        if (userDir.StartsWith("~/") || userDir == "~")
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                userDir.Length > 2 ? userDir[2..] : "");

        // Expand $APP_SUPPORT shorthand
        if (userDir.StartsWith("$APP_SUPPORT"))
        {
            var support = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");
            return Path.Combine(support, userDir.Length > 12 ? userDir[12..].TrimStart('/') : appName);
        }

        // Absolute path — use as-is
        if (Path.IsPathRooted(userDir)) return userDir;

        // Relative — resolve against appRoot
        return Path.Combine(appRoot, userDir);
    }

    public ManagedWindow CreateWindow(IWindowPlugin plugin)
    {
        var id = (++_windowCounter).ToString();
        return _windowManager.CreateWindow(id, plugin);
    }

    public void RegisterWindow(ManagedWindow window, INativeWindow nativeWindow)
    {
        window.OnCreated(nativeWindow);
        _windowManager.RegisterWindow(window);
    }

    public void Run()
    {
        _displayLink.Start();

        // Pump run loop to allow Metal/GPU initialization to complete
        _platform.PumpRunLoop();
        _windowManager.ProcessEvents();

        OnBeforeRun?.Invoke();

        // Restore last workspace or spawn initial windows
        var activeWorkspace = KeystoneDb.GetActiveWorkspaceId();
        if (activeWorkspace != null)
        {
            Console.WriteLine($"[ApplicationRuntime] Restoring workspace: {activeWorkspace}");
            // Spawn excluded windows first (e.g. ribbon — always spawned fresh, never from workspace)
            SpawnExcludedWindows();
            _windowManager.LoadWorkspace(activeWorkspace);
        }
        else
        {
            SpawnInitialWindows();
        }

        // Pump run loop to flush GPU operations from window creation
        _platform.PumpRunLoop(0.1);

        // If no Bun configured, fire OnReady now (with Bun, OnReady fires from ProcessSupervisor after ready signal)
        if (_config.Bun is not { Enabled: true })
        {
            foreach (var core in _pluginRegistry.GetCorePlugins())
                core.OnReady(this);
        }

        Console.WriteLine("[ApplicationRuntime] Main loop started");
        while (!_cancellation.Token.IsCancellationRequested)
        {
            _displayLink.WaitForVsync(17);

#if MACOS
            var pool = objc_autoreleasePoolPush();
#endif
            _platform.PumpRunLoop(0.001);
            _windowManager.ProcessEvents();
            _windowManager.CheckTabDragState();
            _loader?.ProcessPendingReloads();
            _userLoader?.ProcessPendingReloads();
            _extensionLoader?.ProcessPendingReloads();
            while (_pendingActions.TryDequeue(out var action))
                try { action(); } catch (Exception ex) { Console.WriteLine($"[Runtime] {ex.Message}"); }
            CheckMemoryLimit();
#if MACOS
            objc_autoreleasePoolPop(pool);
#endif
        }
        _displayLink.Stop();
    }

    private void SpawnInitialWindows()
    {
        // Config-declared windows
        foreach (var winCfg in _config.Windows)
        {
            if (!winCfg.Spawn) continue;
            var plugin = _pluginRegistry.GetWindow(winCfg.Component);
            if (plugin != null)
                _windowManager.SpawnWindow(winCfg.Component);
            else
            {
                Console.WriteLine($"[ApplicationRuntime] No plugin for window type: {winCfg.Component}");
                Notifications.Warn($"No plugin for window type: {winCfg.Component}");
            }
        }

        // If no config windows, spawn the first registered window type
        if (_config.Windows.Count == 0)
        {
            var allWindows = _pluginRegistry.RegisteredWindowTypes.ToList();
            if (allWindows.Count > 0)
            {
                _windowManager.SpawnWindow(allWindows[0]);
                Console.WriteLine($"[ApplicationRuntime] Auto-spawned: {allWindows[0]}");
            }
        }
    }

    /// <summary>
    /// Spawn config windows that have spawn=true but ExcludeFromWorkspace=true.
    /// Called before workspace restore so these windows exist independently of any saved workspace.
    /// </summary>
    private void SpawnExcludedWindows()
    {
        foreach (var winCfg in _config.Windows)
        {
            if (!winCfg.Spawn) continue;
            var plugin = _pluginRegistry.GetWindow(winCfg.Component);
            if (plugin != null && plugin.ExcludeFromWorkspace)
                _windowManager.SpawnWindow(winCfg.Component);
        }
    }

    public void Shutdown()
    {
        // Notify core plugins before shutdown
        foreach (var core in _pluginRegistry.GetCorePlugins())
            try { core.OnShutdown(this); } catch (Exception ex) { Console.WriteLine($"[Runtime] CorePlugin.OnShutdown: {ex.Message}"); }

        OnShutdown?.Invoke();

        // Auto-save current workspace
        try { _windowManager.SaveWorkspace("__autosave"); }
        catch (Exception ex) { Console.WriteLine($"[ApplicationRuntime] Autosave failed: {ex.Message}"); Notifications.Error($"Autosave failed: {ex.Message}"); }

        GlobalShortcutManager.Shutdown();
        _cancellation.Cancel();
        BunWorkerManager.Instance.StopAll();
        BunManager.Instance.Shutdown();
        _processSupervisor?.Shutdown();
        _loader?.StopWatching();
        _userLoader?.StopWatching();
        _extensionLoader?.StopWatching();
        _scriptManager?.Stop();

        foreach (var window in _windowManager.GetAllWindows().ToList())
            window.Dispose();
        _windowManager.Dispose();
        _displayLink.Dispose();
        _threadPoolManager.Dispose();
        Console.WriteLine("[ApplicationRuntime] Shutdown complete");
        _platform.Quit();
    }

    /// <summary>Schedule an action to run on the main thread (next loop iteration).</summary>
    public void RunOnMainThread(Action action) => _pendingActions.Enqueue(action);

    /// <summary>Schedule an action on the main thread and block until it completes.</summary>
    public void RunOnMainThreadAndWait(Action action)
    {
        using var done = new ManualResetEventSlim(false);
        _pendingActions.Enqueue(() => { action(); done.Set(); });
        done.Wait();
    }

    // === Window spawning ===

    private void SpawnWindow(string windowType)
    {
        SpawnWindowAt(windowType);
    }

    private (INativeWindow nativeWindow, ManagedWindow managed)? SpawnWindowAt(string windowType)
    {
        var plugin = _pluginRegistry.GetWindow(windowType);
        if (plugin == null)
        {
            Console.WriteLine($"[ApplicationRuntime] Unknown window type: {windowType}");
            return null;
        }

        var winCfg = _config.Windows.FirstOrDefault(w => w.Component == windowType);
        var titleBarStyle = winCfg?.TitleBarStyle ?? "hidden";
        var floating = winCfg?.Floating ?? false;

        var (defaultW, defaultH) = plugin.DefaultSize;

        var (sx, sy, sw, sh) = _platform.GetMainScreenFrame();
        double x, y, w, h;

        if (winCfg?.Docked == "top")
        {
            x = sx;
            y = sy;
            w = sw;
            h = defaultH;
        }
        else
        {
            w = defaultW;
            h = defaultH;
            x = sx + (sw - w) / 2;
            y = sy + (sh - h) / 2;
        }

        var headless = winCfg?.Headless ?? false;
        var renderless = headless || (winCfg?.Renderless ?? false);
        var config = new Platform.WindowConfig(x, y, w, h, floating, titleBarStyle, renderless, headless,
            MinWidth: winCfg?.MinWidth, MinHeight: winCfg?.MinHeight,
            MaxWidth: winCfg?.MaxWidth, MaxHeight: winCfg?.MaxHeight,
            AspectRatio: winCfg?.AspectRatio, Opacity: winCfg?.Opacity,
            Fullscreen: winCfg?.Fullscreen ?? false, Resizable: winCfg?.Resizable ?? true);
        var nativeWindow = _platform.CreateWindow(config);
#if MACOS
        if (_schemeHandler != null && nativeWindow is Keystone.Core.Platform.MacOS.MacOSNativeWindow macNW)
            macNW.SchemeHandler = _schemeHandler;
#endif

        var managedWindow = CreateWindow(plugin);
        managedWindow.AlwaysOnTop = floating;
        managedWindow.HasNativeControls = titleBarStyle is "hidden" or "toolkit-native";
        RegisterWindow(managedWindow, nativeWindow);

        // Headless windows are never shown — they run their WebView silently.
        if (!headless)
            nativeWindow.Show();

        if (winCfg?.Fullscreen ?? false)
            nativeWindow.EnterFullscreen();

        RegisterBuiltinInvokeHandlers(managedWindow);

        // Web-only windows (renderless: true) bypass BuildScene/Flex/slot machinery.
        // Load the component URL directly into a full-window WebView.
        if (renderless && plugin is WebWindowPlugin)
        {
            var port = BunManager.Instance.BunPort;
            if (port > 0)
            {
                if (!string.IsNullOrEmpty(winCfg?.ExternalUrl))
                {
                    var url = winCfg.ExternalUrl.Replace("{bunPort}", port.ToString());
                    managedWindow.LoadExternalUrl(url, port);
                }
                else
                {
                    managedWindow.LoadWebComponent(windowType, port);
                }
            }
            else
                Console.WriteLine($"[ApplicationRuntime] Warning: Bun not ready when spawning web-only window '{windowType}'");
        }

        Console.WriteLine($"[ApplicationRuntime] Spawned {windowType} window id={managedWindow.Id} titleBarStyle={titleBarStyle}");
        return (nativeWindow, managedWindow);
    }

    // === Built-in invoke handlers ===

    /// <summary>
    /// Register all built-in invoke() handlers on a freshly spawned window.
    /// Built-in invoke handlers: app, window, dialog, external, clipboard, screen, darkMode, battery, hotkey.
    /// </summary>
    private void RegisterBuiltinInvokeHandlers(ManagedWindow window)
    {
        var windowId = window.Id;

        // ── app ──────────────────────────────────────────────────────────

        window.RegisterInvokeHandler("app:paths", _ =>
        {
            var paths = new
            {
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _config.Name),
                documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                temp = Path.GetTempPath(),
                root = _rootDir,
            };
            return Task.FromResult<object?>(paths);
        });

        window.RegisterInvokeHandler("app:getVersion", _ =>
            Task.FromResult<object?>(_config.Version));

        window.RegisterInvokeHandler("app:getName", _ =>
            Task.FromResult<object?>(_config.Name));

        // ── window ───────────────────────────────────────────────────────

        window.RegisterInvokeHandler("window:setTitle", args =>
        {
            var title = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        args.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (title != null)
                RunOnMainThread(() =>
                {
                    if (window.NativeWindow != null)
                        window.NativeWindow.Title = title;
                });
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:open", args =>
        {
            var type = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                       args.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == null) return Task.FromResult<object?>(null);
            var parentId = args.TryGetProperty("parent", out var pid) ? pid.GetString() : null;
            var tcs = new TaskCompletionSource<object?>();
            RunOnMainThread(() =>
            {
                try
                {
                    var result = _windowManager.OnSpawnWindowAt?.Invoke(type);
                    if (result != null && parentId != null)
                    {
                        result.Value.managed.ParentWindowId = parentId;
                        var parentWindow = _windowManager.GetWindow(parentId);
                        if (parentWindow?.NativeWindow != null)
                            result.Value.nativeWindow.SetParent(parentWindow.NativeWindow);
                    }
                    tcs.TrySetResult(result?.managed.Id);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        });

        window.RegisterInvokeHandler("window:setFloating", args =>
        {
            var floating = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                           args.TryGetProperty("floating", out var f) && f.GetBoolean();
            RunOnMainThread(() =>
            {
                window.AlwaysOnTop = floating;
                window.NativeWindow?.SetFloating(floating);
            });
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:isFloating", _ =>
            Task.FromResult<object?>(window.AlwaysOnTop));

        window.RegisterInvokeHandler("window:getBounds", _ =>
        {
            var tcs = new TaskCompletionSource<object?>();
            RunOnMainThread(() =>
            {
                if (window.NativeWindow != null)
                {
                    var (fx, fy, fw, fh) = window.NativeWindow.Frame;
                    tcs.TrySetResult(new { x = fx, y = fy, width = fw, height = fh });
                }
                else
                    tcs.TrySetResult(null);
            });
            return tcs.Task;
        });

        window.RegisterInvokeHandler("window:setBounds", args =>
        {
            RunOnMainThread(() =>
            {
                if (window.NativeWindow == null) return;
                var (fx, fy, fw, fh) = window.NativeWindow.Frame;
                var x = args.TryGetProperty("x", out var xv) ? xv.GetDouble() : fx;
                var y = args.TryGetProperty("y", out var yv) ? yv.GetDouble() : fy;
                var w = args.TryGetProperty("width", out var wv) ? wv.GetDouble() : fw;
                var h = args.TryGetProperty("height", out var hv) ? hv.GetDouble() : fh;
                window.NativeWindow.SetFrame(x, y, w, h);
            });
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:center", _ =>
        {
            RunOnMainThread(() =>
            {
                if (window.NativeWindow == null) return;
                var (_, _, fw, fh) = window.NativeWindow.Frame;
                var (sx, sy, sw, sh) = _platform.GetMainScreenFrame();
                window.NativeWindow.SetFrame(sx + (sw - fw) / 2, sy + (sh - fh) / 2, fw, fh);
            });
            return Task.FromResult<object?>(null);
        });

        window.RegisterMainThreadInvokeHandler("window:startDrag", _ =>
        {
            window.NativeWindow?.StartDrag();
            return null;
        });

        window.RegisterInvokeHandler("window:getId", _ =>
            Task.FromResult<object?>(windowId));

        window.RegisterInvokeHandler("window:getTitle", _ =>
        {
            var tcs = new TaskCompletionSource<object?>();
            RunOnMainThread(() => tcs.TrySetResult(window.NativeWindow?.Title));
            return tcs.Task;
        });

        window.RegisterInvokeHandler("window:getParentId", _ =>
            Task.FromResult<object?>(window.ParentWindowId));

        window.RegisterInvokeHandler("window:isFullscreen", _ =>
            Task.FromResult<object?>(window.IsFullscreen));

        window.RegisterInvokeHandler("window:isMinimized", _ =>
            Task.FromResult<object?>(window.IsMinimized));

        window.RegisterInvokeHandler("window:isFocused", _ =>
            Task.FromResult<object?>(window.IsFocused));

        window.RegisterInvokeHandler("window:enterFullscreen", _ =>
        {
            RunOnMainThread(() => window.NativeWindow?.EnterFullscreen());
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:exitFullscreen", _ =>
        {
            RunOnMainThread(() => window.NativeWindow?.ExitFullscreen());
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:focus", _ =>
        {
            RunOnMainThread(() => window.NativeWindow?.Show());
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:hide", _ =>
        {
            RunOnMainThread(() => window.NativeWindow?.Hide());
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:show", _ =>
        {
            RunOnMainThread(() => window.NativeWindow?.Show());
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:setMinSize", args =>
        {
            var w = args.TryGetProperty("width", out var wv) ? wv.GetDouble() : 0;
            var h = args.TryGetProperty("height", out var hv) ? hv.GetDouble() : 0;
            RunOnMainThread(() => window.NativeWindow?.SetMinSize(w, h));
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:setMaxSize", args =>
        {
            var w = args.TryGetProperty("width", out var wv) ? wv.GetDouble() : double.MaxValue;
            var h = args.TryGetProperty("height", out var hv) ? hv.GetDouble() : double.MaxValue;
            RunOnMainThread(() => window.NativeWindow?.SetMaxSize(w, h));
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:setAspectRatio", args =>
        {
            var ratio = args.TryGetProperty("ratio", out var rv) ? rv.GetDouble() : 0;
            RunOnMainThread(() => window.NativeWindow?.SetAspectRatio(ratio));
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:setOpacity", args =>
        {
            var val = args.TryGetProperty("opacity", out var ov) ? ov.GetDouble() : 1.0;
            RunOnMainThread(() => window.NativeWindow?.SetOpacity(val));
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:setResizable", args =>
        {
            var val = args.TryGetProperty("resizable", out var rv) && rv.GetBoolean();
            RunOnMainThread(() => window.NativeWindow?.SetResizable(val));
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:setContentProtection", args =>
        {
            var val = args.TryGetProperty("enabled", out var ev) && ev.GetBoolean();
            RunOnMainThread(() => window.NativeWindow?.SetContentProtection(val));
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("window:setIgnoreMouseEvents", args =>
        {
            var val = args.TryGetProperty("ignore", out var iv) && iv.GetBoolean();
            RunOnMainThread(() => window.NativeWindow?.SetIgnoreMouseEvents(val));
            return Task.FromResult<object?>(null);
        });

        // ── dialog ───────────────────────────────────────────────────────

#if MACOS
        window.RegisterInvokeHandler("dialog:openFile", args =>
        {
            var tcs = new TaskCompletionSource<object?>();
            NSApplication.SharedApplication.InvokeOnMainThread(() =>
            {
                try
                {
                    var panel = NSOpenPanel.OpenPanel;
                    if (args.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (args.TryGetProperty("title", out var title))
                            panel.Message = title.GetString() ?? "";
                        if (args.TryGetProperty("multiple", out var multiple))
                            panel.AllowsMultipleSelection = multiple.GetBoolean();
                        if (args.TryGetProperty("filters", out var filters))
                        {
                            var exts = filters.EnumerateArray()
                                .Select(e => e.GetString()?.TrimStart('.') ?? "")
                                .Where(e => !string.IsNullOrEmpty(e))
                                .ToArray();
                            if (exts.Length > 0)
                                panel.AllowedContentTypes = exts
                                    .Select(e => UniformTypeIdentifiers.UTType.CreateFromExtension(e))
                                    .Where(t => t != null)
                                    .ToArray()!;
                        }
                    }
                    var response = (int)panel.RunModal();
                    if (response == (int)AppKit.NSModalResponse.OK)
                    {
                        var paths = panel.Urls.Select(u => u.Path ?? "").Where(p => !string.IsNullOrEmpty(p)).ToArray();
                        tcs.TrySetResult(paths.Length > 0 ? (object?)paths : null);
                    }
                    else
                        tcs.TrySetResult(null);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        });

        window.RegisterInvokeHandler("dialog:saveFile", args =>
        {
            var tcs = new TaskCompletionSource<object?>();
            NSApplication.SharedApplication.InvokeOnMainThread(() =>
            {
                try
                {
                    var panel = NSSavePanel.SavePanel;
                    if (args.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (args.TryGetProperty("title", out var title))
                            panel.Message = title.GetString() ?? "";
                        if (args.TryGetProperty("defaultName", out var dn))
                            panel.NameFieldStringValue = dn.GetString() ?? "";
                    }
                    var response = (int)panel.RunModal();
                    var path = response == (int)AppKit.NSModalResponse.OK ? panel.Url?.Path : null;
                    tcs.TrySetResult(path);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        });

        window.RegisterInvokeHandler("dialog:showMessage", args =>
        {
            var tcs = new TaskCompletionSource<object?>();
            NSApplication.SharedApplication.InvokeOnMainThread(() =>
            {
                try
                {
                    var alert = new NSAlert();
                    if (args.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (args.TryGetProperty("title", out var title))
                            alert.MessageText = title.GetString() ?? "";
                        if (args.TryGetProperty("message", out var message))
                            alert.InformativeText = message.GetString() ?? "";
                        if (args.TryGetProperty("buttons", out var buttons))
                        {
                            foreach (var btn in buttons.EnumerateArray())
                                alert.AddButton(btn.GetString() ?? "OK");
                        }
                    }
                    if (alert.Buttons.Length == 0)
                        alert.AddButton("OK");
                    var response = (int)alert.RunModal();
                    // NSAlertFirstButtonReturn = 1000, second = 1001, etc.
                    var index = response - 1000;
                    tcs.TrySetResult(index < 0 ? 0 : index);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        });
#else
        // Linux/Windows: use IPlatform dialog abstractions
        window.RegisterInvokeHandler("dialog:openFile", async args =>
        {
            var title = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        args.TryGetProperty("title", out var t) ? t.GetString() : null;
            var multiple = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                           args.TryGetProperty("multiple", out var m) && m.GetBoolean();
            var filters = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                          args.TryGetProperty("filters", out var f) ? f.EnumerateArray()
                              .Select(e => e.GetString()?.TrimStart('.') ?? "")
                              .Where(e => !string.IsNullOrEmpty(e)).ToArray() : null;
            return await _platform.ShowOpenDialogAsync(new OpenDialogOptions(title, multiple, filters));
        });

        window.RegisterInvokeHandler("dialog:saveFile", async args =>
        {
            var title = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        args.TryGetProperty("title", out var t) ? t.GetString() : null;
            var defaultName = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                              args.TryGetProperty("defaultName", out var dn) ? dn.GetString() : null;
            return await _platform.ShowSaveDialogAsync(new SaveDialogOptions(title, defaultName));
        });

        window.RegisterInvokeHandler("dialog:showMessage", async args =>
        {
            var title = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        args.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var message = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                          args.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            string[]? buttons = null;
            if (args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                args.TryGetProperty("buttons", out var b))
                buttons = b.EnumerateArray().Select(e => e.GetString() ?? "OK").ToArray();
            return await _platform.ShowMessageBoxAsync(new MessageBoxOptions(title, message, buttons));
        });
#endif

        // ── external ──────────────────────────────────────────────────────

        window.RegisterInvokeHandler("external:path", args =>
        {
            var path = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                       args.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(path)) return Task.FromResult<object?>(false);
            try
            {
                _platform.OpenPath(path);
                return Task.FromResult<object?>(true);
            }
            catch
            {
                return Task.FromResult<object?>(false);
            }
        });

        // ── clipboard ─────────────────────────────────────────────────────────

        window.RegisterInvokeHandler("clipboard:readText", _ =>
            Task.FromResult<object?>(_platform.ClipboardReadText()));

        window.RegisterInvokeHandler("clipboard:writeText", args =>
        {
            var text = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                       args.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            _platform.ClipboardWriteText(text);
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("clipboard:clear", _ =>
        {
            _platform.ClipboardClear();
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("clipboard:hasText", _ =>
            Task.FromResult<object?>(_platform.ClipboardHasText()));

        // ── screen ────────────────────────────────────────────────────────────

        window.RegisterInvokeHandler("screen:getAllDisplays", _ =>
        {
            var displays = _platform.GetAllDisplays().Select(d => new
            {
                x = d.X, y = d.Y, width = d.Width, height = d.Height,
                scaleFactor = d.ScaleFactor, primary = d.IsPrimary
            });
            return Task.FromResult<object?>(displays);
        });

        window.RegisterInvokeHandler("screen:getPrimaryDisplay", _ =>
        {
            var d = _platform.GetAllDisplays().FirstOrDefault(x => x.IsPrimary)
                    ?? _platform.GetAllDisplays().FirstOrDefault();
            if (d == null) return Task.FromResult<object?>(null);
            return Task.FromResult<object?>(new
            {
                x = d.X, y = d.Y, width = d.Width, height = d.Height,
                scaleFactor = d.ScaleFactor, primary = d.IsPrimary
            });
        });

        window.RegisterInvokeHandler("screen:getCursorScreenPoint", _ =>
        {
            var (mx, my) = _platform.GetMouseLocation();
            return Task.FromResult<object?>(new { x = mx, y = my });
        });

        // ── darkMode ──────────────────────────────────────────────────────────

        window.RegisterInvokeHandler("darkMode:isDark", _ =>
            Task.FromResult<object?>(_platform.IsDarkMode()));

        // ── battery ───────────────────────────────────────────────────────────

        window.RegisterInvokeHandler("battery:status", _ =>
        {
            var s = _platform.GetPowerStatus();
            return Task.FromResult<object?>(new { onBattery = s.OnBattery, batteryPercent = s.BatteryPercent });
        });

        // ── notification ──────────────────────────────────────────────────────

        window.RegisterInvokeHandler("notification:show", async args =>
        {
            var title = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        args.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var body = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                       args.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            await _platform.ShowOsNotification(title, body);
            return (object?)null;
        });

        // ── hotkey ────────────────────────────────────────────────────────────

        window.RegisterInvokeHandler("hotkey:register", args =>
        {
            var acc = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                      args.TryGetProperty("accelerator", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(acc)) return Task.FromResult<object?>(false);
            return Task.FromResult<object?>(GlobalShortcutManager.Register(acc, windowId));
        });

        window.RegisterInvokeHandler("hotkey:unregister", args =>
        {
            var acc = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                      args.TryGetProperty("accelerator", out var a) ? a.GetString() : null;
            if (!string.IsNullOrEmpty(acc)) GlobalShortcutManager.Unregister(acc);
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("hotkey:isRegistered", args =>
        {
            var acc = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                      args.TryGetProperty("accelerator", out var a) ? a.GetString() : null;
            return Task.FromResult<object?>(
                !string.IsNullOrEmpty(acc) && GlobalShortcutManager.IsRegistered(acc));
        });

        // ── headless ──────────────────────────────────────────────────────────

        window.RegisterInvokeHandler("headless:list", _ =>
        {
            var ids = _windowManager.GetAllWindows()
                .Where(w => _config.Windows.FirstOrDefault(c => c.Component == w.WindowType)?.Headless ?? false)
                .Select(w => w.Id)
                .ToArray();
            return Task.FromResult<object?>(ids);
        });

        window.RegisterInvokeHandler("headless:evaluate", args =>
        {
            var targetId = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                           args.TryGetProperty("windowId", out var wid) ? wid.GetString() : null;
            var js = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                     args.TryGetProperty("js", out var j) ? j.GetString() : null;
            if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(js))
                return Task.FromResult<object?>(null);

            var target = _windowManager.GetWindow(targetId);
            if (target == null) return Task.FromResult<object?>(null);

            // Fire-and-forget: EvaluateJavaScript on IWebView is void.
            // Use the void overload; callers that need return values should
            // have the headless window push results via BunManager channel.
            RunOnMainThread(() => target.EvaluateJavaScript(js));
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("headless:close", args =>
        {
            var targetId = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                           args.TryGetProperty("windowId", out var wid) ? wid.GetString() : null;
            if (!string.IsNullOrEmpty(targetId))
                RunOnMainThread(() => _windowManager.CloseWindow(targetId));
            return Task.FromResult<object?>(null);
        });

        // ── web workers ──────────────────────────────────────────────────────
        // Headless window workers with postMessage/onMessage semantics.

        window.RegisterInvokeHandler("worker:spawn", args =>
        {
            var component = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                            args.TryGetProperty("component", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(component))
                return Task.FromResult<object?>(null);

            var tcs = new TaskCompletionSource<object?>();
            RunOnMainThread(() =>
            {
                try
                {
                    var result = _windowManager.OnSpawnWindowAt?.Invoke(component);
                    if (result != null)
                    {
                        _workerIds.Add(result.Value.managed.Id);
                        tcs.TrySetResult(result.Value.managed.Id);
                    }
                    else tcs.TrySetResult(null);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        });

        window.RegisterInvokeHandler("worker:evaluate", args =>
        {
            var workerId = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                           args.TryGetProperty("workerId", out var wid) ? wid.GetString() : null;
            var js = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                     args.TryGetProperty("js", out var j) ? j.GetString() : null;
            if (string.IsNullOrEmpty(workerId) || string.IsNullOrEmpty(js))
                return Task.FromResult<object?>(null);

            var target = _windowManager.GetWindow(workerId);
            if (target == null) return Task.FromResult<object?>(null);

            var tcs = new TaskCompletionSource<object?>();
            RunOnMainThread(() => target.EvaluateJavaScriptWithResult(js, result => tcs.TrySetResult(result)));
            return tcs.Task;
        });

        window.RegisterInvokeHandler("worker:terminate", args =>
        {
            var workerId = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                           args.TryGetProperty("workerId", out var wid) ? wid.GetString() : null;
            if (!string.IsNullOrEmpty(workerId))
            {
                _workerIds.Remove(workerId);
                RunOnMainThread(() => _windowManager.CloseWindow(workerId));
            }
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("worker:list", _ =>
        {
            // Lazy cleanup — remove IDs for windows that no longer exist
            _workerIds.RemoveWhere(id => _windowManager.GetWindow(id) == null);
            return Task.FromResult<object?>(_workerIds.ToArray());
        });

        // ── http ──────────────────────────────────────────────────────────
        // Routes all fetch("/api/...") calls from the browser through HttpRouter.
        window.RegisterInvokeHandler(HttpRouter.InvokeChannel, async args =>
        {
            return await _httpRouter.DispatchAsync(args, windowId);
        });

        // ── protocol ────────────────────────────────────────────────────

        window.RegisterInvokeHandler("app:setAsDefaultProtocolClient", args =>
        {
            var scheme = args.TryGetProperty("scheme", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(scheme)) return Task.FromResult<object?>(false);
            return Task.FromResult<object?>(_platform.SetAsDefaultProtocolClient(scheme));
        });

        window.RegisterInvokeHandler("app:removeAsDefaultProtocolClient", args =>
        {
            var scheme = args.TryGetProperty("scheme", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(scheme)) return Task.FromResult<object?>(false);
            return Task.FromResult<object?>(_platform.RemoveAsDefaultProtocolClient(scheme));
        });

        window.RegisterInvokeHandler("app:isDefaultProtocolClient", args =>
        {
            var scheme = args.TryGetProperty("scheme", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(scheme)) return Task.FromResult<object?>(false);
            return Task.FromResult<object?>(_platform.IsDefaultProtocolClient(scheme));
        });

        // ── webview ──────────────────────────────────────────────────────

        window.RegisterInvokeHandler("webview:setInspectable", args =>
        {
            var enabled = args.TryGetProperty("enabled", out var ev) && ev.GetBoolean();
            RunOnMainThread(() => window.SetWebViewInspectable(enabled));
            return Task.FromResult<object?>(null);
        });

        // ── context menu (native NSMenu) ─────────────────────────────────

#if MACOS
        window.RegisterInvokeHandler("window:showContextMenu", args =>
        {
            var tcs = new TaskCompletionSource<object?>();
            RunOnMainThread(() =>
            {
                var menu = new NSMenu();
                foreach (var item in args.GetProperty("items").EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String && item.GetString() == "separator")
                    { menu.AddItem(NSMenuItem.SeparatorItem); continue; }

                    var label = item.GetProperty("label").GetString() ?? "";
                    var action = item.GetProperty("action").GetString() ?? "";
                    var act = action;
                    menu.AddItem(new NSMenuItem(label, (s, e) =>
                        _actionRouter.Execute(act, windowId)));
                }
                var view = window.NativeWindow?.GetContentView() as NSView;
                if (view != null)
                    NSMenu.PopUpContextMenu(menu, NSApplication.SharedApplication.CurrentEvent!, view);
                tcs.SetResult(null);
            });
            return tcs.Task;
        });
#endif

        // ── diagnostics / observability ──────────────────────────────────

        window.RegisterInvokeHandler("diagnostics:crashes", _ =>
            Task.FromResult<object?>(CrashReporter.Recent));

        window.RegisterInvokeHandler("diagnostics:health", _ =>
        {
            var footprint = GetPhysicalFootprint();
            var result = new
            {
                uptimeMs = (long)(DateTime.UtcNow - _startTime).TotalMilliseconds,
                memoryBytes = footprint,
                bunRunning = BunManager.Instance.IsRunning,
                windowCount = _windowManager.GetAllWindows().Count(),
                recentCrashes = CrashReporter.Recent.Count,
            };
            return Task.FromResult<object?>(result);
        });

        // ── service worker control ──────────────────────────────────────

        window.RegisterInvokeHandler("sw:status", async _ =>
            await window.GetServiceWorkerStatus());

        window.RegisterInvokeHandler("sw:unregister", _ =>
        {
            window.UnregisterServiceWorkers();
            return Task.FromResult<object?>(null);
        });

        window.RegisterInvokeHandler("sw:clearCaches", _ =>
        {
            window.ClearServiceWorkerCaches();
            return Task.FromResult<object?>(null);
        });

        // ── request interception / navigation policy ────────────────────

        window.RegisterInvokeHandler("webview:setNavigationPolicy", args =>
        {
            // Accept a list of blocked URL patterns (simple prefix/contains matching)
            var blocked = new List<string>();
            if (args.TryGetProperty("blocked", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.GetString() is { } s) blocked.Add(s);

            window.SetNavigationPolicy(blocked.Count > 0
                ? url => !blocked.Any(b => url.Contains(b))
                : null);
            return Task.FromResult<object?>(null);
        });

#if MACOS
        window.RegisterInvokeHandler("webview:setRequestInterceptor", args =>
        {
            if (_schemeHandler == null) return Task.FromResult<object?>(new { error = "customScheme not enabled" });

            var rules = new List<(string pattern, string action, string? target)>();
            if (args.TryGetProperty("rules", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var rule in arr.EnumerateArray())
                {
                    var pattern = rule.GetProperty("pattern").GetString() ?? "";
                    var act = rule.GetProperty("action").GetString() ?? "allow";
                    var target = rule.TryGetProperty("target", out var t) ? t.GetString() : null;
                    rules.Add((pattern, act, target));
                }
            }

            if (rules.Count == 0)
            {
                _schemeHandler.OnIntercept = null;
            }
            else
            {
                _schemeHandler.OnIntercept = (url, method) =>
                {
                    foreach (var (pattern, action, target) in rules)
                    {
                        if (!url.Contains(pattern)) continue;
                        return action switch
                        {
                            "block" => Keystone.Core.Platform.MacOS.SchemeResponse.Blocked(),
                            "redirect" when target != null => Keystone.Core.Platform.MacOS.SchemeResponse.Redirect(target),
                            _ => null,
                        };
                    }
                    return null;
                };
            }
            return Task.FromResult<object?>(null);
        });
#endif
    }

    // === Memory monitoring (platform-specific) ===

    public static long GetPhysicalFootprint()
    {
#if MACOS
        var info = new TaskVmInfo();
        int count = Marshal.SizeOf<TaskVmInfo>() / sizeof(int);
        return task_info(mach_task_self(), 22 /* TASK_VM_INFO */, ref info, ref count) == 0
            ? info.phys_footprint : -1;
#else
        // Linux: read VmRSS from /proc/self/status
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmRSS:"))
                {
                    var kb = long.Parse(line.Split(':')[1].Trim().Split(' ')[0]);
                    return kb * 1024;
                }
            }
        }
        catch { }
        return -1;
#endif
    }

    private void RunPostStartupMemoryMaintenance()
    {
        if (!_postStartupGcPolicyEnabled && _memCheckCounter >= MemCheckIntervalFrames)
        {
            _postStartupGcPolicyEnabled = true;
            _postStartupGcPassesRemaining = PostStartupGcPassCount;
            _nextPostStartupGcFrame = _memCheckCounter;

            try
            {
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"[MemWatch] failed to enable sustained low latency: {ex.Message}");
            }

            Console.WriteLine(
                $"[MemWatch] post-startup GC policy enabled: latency={GCSettings.LatencyMode} background_gen2_passes={PostStartupGcPassCount}");
        }

        if (_postStartupGcPassesRemaining == 0 || _memCheckCounter < _nextPostStartupGcFrame)
            return;

        var pass = PostStartupGcPassCount - _postStartupGcPassesRemaining + 1;
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
        _postStartupGcPassesRemaining--;
        _nextPostStartupGcFrame = _memCheckCounter + PostStartupGcIntervalFrames;
        Console.WriteLine($"[MemWatch] scheduled background gen2 pass {pass}/{PostStartupGcPassCount}");
    }

    private void CheckMemoryLimit()
    {
        if (++_memCheckCounter % MemCheckIntervalFrames != 0) return;

        RunPostStartupMemoryMaintenance();

        if (_memCheckCounter < 7200) return;

        var footprint = GetPhysicalFootprint();
        if (footprint <= 0) return;

        var windows = _windowManager.GetAllWindows().ToList();

        if (_memCheckCounter % 1800 == 0)
        {
            var gcInfo = GC.GetGCMemoryInfo();
            var gcHeap = GC.GetTotalMemory(false);
            var gcCommitted = gcInfo.TotalCommittedBytes;
            Console.WriteLine($"[MemWatch] footprint={footprint / (1024 * 1024)}mb gc_live={gcHeap / (1024 * 1024)}mb gc_committed={gcCommitted / (1024 * 1024)}mb windows={windows.Count}");

            if (_memCheckCounter % 3600 == 0)
            {
                int totalResources = 0; long totalCacheBytes = 0;
                long expectedIOSurface = 0;
                foreach (var w in windows)
                {
                    expectedIOSurface += w.ExpectedIOSurfaceBytes;
                    if (w.GetGpuContext() is IWindowGpuContext wgpu)
                    {
                        var (count, bytes) = wgpu.GetCacheStats();
                        totalResources += count;
                        totalCacheBytes += bytes;
                    }
                }
                var accountedFor = gcCommitted + totalCacheBytes + expectedIOSurface;
                var unaccounted = footprint - accountedFor;

                Console.WriteLine($"[MemWatch] GPU cache: {totalResources} resources, {totalCacheBytes / (1024 * 1024)}mb across {windows.Count} windows");
                Console.WriteLine($"[MemWatch] IOSurface: expected={expectedIOSurface / (1024 * 1024)}mb (3 drawables × {windows.Count} windows)");
                Console.WriteLine($"[MemWatch] accounted={accountedFor / (1024 * 1024)}mb unaccounted={unaccounted / (1024 * 1024)}mb");
                Console.WriteLine($"[MemWatch] GC gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");
            }
        }

        // IOSurface orphan detection — if unaccounted memory exceeds 200MB,
        // purge all GPU caches and force GC to reclaim stale IOSurfaces
        long expectedTotal = 0;
        long cacheTotal = 0;
        foreach (var w in windows)
        {
            expectedTotal += w.ExpectedIOSurfaceBytes;
            if (w.GetGpuContext() is IWindowGpuContext wgpu)
            {
                var (_, bytes) = wgpu.GetCacheStats();
                cacheTotal += bytes;
            }
        }
        var gcCommittedNow = GC.GetGCMemoryInfo().TotalCommittedBytes;
        var accounted = gcCommittedNow + cacheTotal + expectedTotal;
        var orphaned = footprint - accounted;

        if (orphaned > 200 * 1024 * 1024 && footprint > _lastPurgeFootprint + 50 * 1024 * 1024)
        {
            _lastPurgeFootprint = footprint;
            Console.WriteLine($"[MemWatch] {orphaned / (1024 * 1024)}mb unaccounted — purging GPU caches");
            foreach (var w in windows)
                w.RequestGpuPurge();
            GC.Collect(2, GCCollectionMode.Aggressive, true);
            GC.WaitForPendingFinalizers();
        }

        if (footprint > MemoryLimitBytes)
        {
            Console.WriteLine($"[MEMORY GUARD] FATAL: {footprint / (1024 * 1024)}MB exceeds {MemoryLimitBytes / (1024 * 1024)}MB limit");
            Console.WriteLine("[MEMORY GUARD] Crashing to prevent swap thrash");
            Environment.Exit(99);
        }
    }

    // === Native interop (platform-specific) ===

#if MACOS
    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "task_info")]
    private static extern int task_info(int target_task, uint flavor, ref TaskVmInfo info, ref int count);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mach_task_self")]
    private static extern int mach_task_self();

    [StructLayout(LayoutKind.Sequential)]
    private struct TaskVmInfo
    {
        public long virtual_size;
        public int region_count;
        public int page_size;
        public long resident_size;
        public long resident_size_peak;
        public long device, device_peak;
        public long @internal, internal_peak;
        public long external, external_peak;
        public long reusable, reusable_peak;
        public long purgeable_volatile_pmap, purgeable_volatile_resident, purgeable_volatile_virtual;
        public long compressed, compressed_peak, compressed_lifetime;
        public long phys_footprint;
    }
#endif
}
