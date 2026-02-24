using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
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
    private BunProcess? _bunProcess;
    private readonly HttpRouter _httpRouter = new();
    private int _windowCounter;
    private int _memCheckCounter;
    private long _lastPurgeFootprint;
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

    // Process lifecycle events — analogous to Electron's app.on('render-process-gone') etc.
    /// <summary>Fired when the Bun subprocess exits unexpectedly. Arg is the OS exit code.</summary>
    public event Action<int>? OnBunCrash;
    /// <summary>Fired after Bun successfully restarts following a crash. Arg is the restart attempt number (1-based).</summary>
    public event Action<int>? OnBunRestart;
    /// <summary>Fired when a WKWebView content process terminates unexpectedly. Arg is the window Id.</summary>
    public event Action<string>? OnWebViewCrash;

    internal void RaiseWebViewCrash(string windowId) => OnWebViewCrash?.Invoke(windowId);

    private string? _bunHostPath;
    private string? _bunAppRoot;
    private string? _bunCompiledExe;
    private int _bunRestartAttempt;
    private volatile bool _bunRestartScheduled;

    // ICoreContext implementation
    Action<string, string>? ICoreContext.OnUnhandledAction
    {
        set => ActionRouter.OnUnhandledAction += value;
    }

    void ICoreContext.RegisterService<T>(T service) => ServiceLocator.Register(service);
    void ICoreContext.RegisterWindow(IWindowPlugin plugin) => _pluginRegistry.RegisterWindow(plugin);
    void ICoreContext.RegisterService(IServicePlugin plugin) => _pluginRegistry.RegisterService(plugin);
    void ICoreContext.RunOnMainThread(Action action) => RunOnMainThread(action);
    void ICoreContext.RunOnMainThreadAndWait(Action action) => RunOnMainThreadAndWait(action);
    IBunService ICoreContext.Bun => BunManager.Instance;
    IBunWorkerManager ICoreContext.Workers => BunWorkerManager.Instance;
    IHttpRouter ICoreContext.Http => _httpRouter;

    public ApplicationRuntime(KeystoneConfig config, string rootDir, IPlatform platform)
    {
        _config = config;
        _rootDir = rootDir;
        _platform = platform;
        _pluginRegistry = new PluginRegistry();
        _windowManager = new WindowManager(platform);
        _windowManager.OnSpawnWindow = SpawnWindow;
        _windowManager.OnSpawnWindowAt = SpawnWindowAt;
#if MACOS
        _displayLink = new DisplayLink();
#else
        _displayLink = new TimerDisplayLink();
#endif
        _actionRouter = new ActionRouter(_windowManager);
        _instance = this;

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
        // Expose app identity to child processes (Bun subprocess uses these for process.title, userData paths, etc.)
        Environment.SetEnvironmentVariable("KEYSTONE_APP_NAME", _config.Name);
        Environment.SetEnvironmentVariable("KEYSTONE_APP_ID", _config.Id);

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
                StartBun(compiledExe, appBunRoot, compiledExe: compiledExe);
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
                    StartBun(engineHostTs, appBunRoot);
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

    private void StartBun(string hostPath, string appBunRoot, string? compiledExe = null)
    {
        _bunHostPath = hostPath;
        _bunAppRoot = appBunRoot;
        _bunCompiledExe = compiledExe;

        var process = new BunProcess();
        WireBunProcess(process, hostPath, appBunRoot);

        if (process.Start(hostPath, appBunRoot, compiledExe))
        {
            _bunProcess = process;
            Console.WriteLine(compiledExe != null
                ? "[ApplicationRuntime] Bun process started (compiled)"
                : "[ApplicationRuntime] Bun process started (dev)");
        }
        else
        {
            Console.WriteLine("[ApplicationRuntime] WARNING: Bun process failed to start");
            Notifications.Warn("Bun process failed to start");
        }
    }

    private void WireBunProcess(BunProcess process, string hostPath, string appBunRoot)
    {
        process.OnLine = line =>
        {
            if (!BunManager.Instance.IsRunning &&
                BunManager.Instance.TryAttachFromReadySignal(process, line))
            {
                SpawnConfiguredWorkers();
                return;
            }
        };

        process.OnExit = exitCode =>
        {
            if (_cancellation.IsCancellationRequested) return; // clean shutdown — do not restart

            Console.WriteLine($"[ApplicationRuntime] Bun process exited (code={exitCode})");
            BunManager.Instance.Detach();
            OnBunCrash?.Invoke(exitCode);

            if (_config.ProcessRecovery.BunAutoRestart)
                ScheduleBunRestart();
            else
                Console.WriteLine("[ApplicationRuntime] Bun auto-restart disabled (processRecovery.bunAutoRestart=false)");
        };

        // Web actions (from JS via WebSocket) route through ActionRouter same as native actions
        BunManager.Instance.OnWebAction = action => _actionRouter.Execute(action, "web");

        // HMR: when Bun rebuilds a web component, hot-swap it in all windows that host it
        BunManager.Instance.OnWebComponentHmr = component =>
        {
            foreach (var window in _windowManager.GetAllWindows())
                window.HotSwapSlot(component);
        };
    }

    private void ScheduleBunRestart()
    {
        if (_bunRestartScheduled) return;
        _bunRestartScheduled = true;

        var attempt = ++_bunRestartAttempt;
        var cfg = _config.ProcessRecovery;

        if (attempt > cfg.BunMaxRestarts)
        {
            Console.WriteLine($"[ApplicationRuntime] Bun restart limit ({cfg.BunMaxRestarts}) reached — giving up");
            Notifications.Error($"Bun process failed to recover after {cfg.BunMaxRestarts} attempts.");
            _bunRestartScheduled = false;
            return;
        }

        // Exponential backoff: baseDelay * 2^(attempt-1), capped at maxDelay
        var delayMs = (int)Math.Min(cfg.BunRestartBaseDelayMs * Math.Pow(2, attempt - 1), cfg.BunRestartMaxDelayMs);
        Console.WriteLine($"[ApplicationRuntime] Restarting Bun in {delayMs}ms (attempt {attempt}/{cfg.BunMaxRestarts})");

        Task.Run(async () =>
        {
            await Task.Delay(delayMs);

            RunOnMainThread(() =>
            {
                _bunRestartScheduled = false;
                _bunProcess?.Dispose();

                var process = new BunProcess();
                WireBunProcess(process, _bunHostPath!, _bunAppRoot!);

                if (process.Start(_bunHostPath!, _bunAppRoot!, _bunCompiledExe))
                {
                    _bunProcess = process;
                    Console.WriteLine($"[ApplicationRuntime] Bun restarted (attempt {attempt})");
                    OnBunRestart?.Invoke(attempt);
                    _bunRestartAttempt = 0; // reset counter on successful start
                }
                else
                {
                    Console.WriteLine($"[ApplicationRuntime] Bun restart attempt {attempt} failed");
                    Notifications.Warn($"Bun restart attempt {attempt} failed");
                    ScheduleBunRestart(); // try again with next backoff step
                }
            });
        });
    }

    private void SpawnConfiguredWorkers()
    {
        var workers = _config.Workers;
        if (workers == null || workers.Count == 0) return;

        // Resolve compiled worker exe for package mode
        string? compiledWorkerExe = null;
        if (_config.Bun?.CompiledWorkerExe is { } workerExeName)
        {
            var assemblyDir = Path.GetDirectoryName(typeof(ApplicationRuntime).Assembly.Location) ?? "";
            var macosDir = Path.Combine(assemblyDir, "..", "MacOS");
            foreach (var candidate in new[] {
                Path.Combine(assemblyDir, workerExeName),
                Path.Combine(macosDir, workerExeName),
            })
            {
                if (File.Exists(candidate)) { compiledWorkerExe = candidate; break; }
            }

            if (compiledWorkerExe != null)
                Console.WriteLine($"[ApplicationRuntime] Worker exe: {compiledWorkerExe}");
        }

        var workerHostPath = Path.Combine(Path.GetDirectoryName(_bunHostPath!)!, "worker-host.ts");
        var readyCount = 0;
        var totalAutoStart = workers.Count(w => w.AutoStart);

        foreach (var cfg in workers.Where(w => w.AutoStart))
        {
            var worker = BunWorkerManager.Instance.Spawn(cfg, workerHostPath, _bunAppRoot!, compiledWorkerExe);
            worker.OnRestart = attempt =>
                Console.WriteLine($"[ApplicationRuntime] Worker '{cfg.Name}' recovered (attempt {attempt})");

            // Track ready state for port broadcast
            var origOnLine = worker.Config; // just need a closure trigger
            var checkReady = () =>
            {
                if (worker.IsRunning && Interlocked.Increment(ref readyCount) == totalAutoStart)
                    BunWorkerManager.Instance.BroadcastPorts();
            };

            // Hook into the worker's ready event — poll briefly since ready fires on the reader thread
            Task.Run(async () =>
            {
                for (var i = 0; i < 100; i++) // 10 second timeout
                {
                    await Task.Delay(100);
                    if (worker.IsRunning) { checkReady(); return; }
                }
                Console.WriteLine($"[ApplicationRuntime] Worker '{cfg.Name}' did not become ready in time");
            });
        }
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
            _windowManager.LoadWorkspace(activeWorkspace);
        }
        else
        {
            SpawnInitialWindows();
        }

        // Pump run loop to flush GPU operations from window creation
        _platform.PumpRunLoop(0.1);

        Console.WriteLine("[ApplicationRuntime] Main loop started");
        while (!_cancellation.Token.IsCancellationRequested)
        {
            if (!_displayLink.WaitForVsync(17))
                continue;

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

    public void Shutdown()
    {
        OnShutdown?.Invoke();

        // Auto-save current workspace
        try { _windowManager.SaveWorkspace("__autosave"); }
        catch (Exception ex) { Console.WriteLine($"[ApplicationRuntime] Autosave failed: {ex.Message}"); Notifications.Error($"Autosave failed: {ex.Message}"); }

        _cancellation.Cancel();
        BunWorkerManager.Instance.StopAll();
        BunManager.Instance.Shutdown();
        _bunProcess?.Dispose();
        _loader?.StopWatching();
        _userLoader?.StopWatching();
        _extensionLoader?.StopWatching();
        _scriptManager?.Stop();

        foreach (var window in _windowManager.GetAllWindows().ToList())
            window.Dispose();
        _windowManager.Dispose();
        _displayLink.Dispose();
        Console.WriteLine("[ApplicationRuntime] Shutdown complete");
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

        // Center window on main screen
        var (sx, sy, sw, sh) = _platform.GetMainScreenFrame();
        var x = sx + (sw - defaultW) / 2;
        var y = sy + (sh - defaultH) / 2;

        var config = new Platform.WindowConfig(x, y, defaultW, defaultH, floating, titleBarStyle, winCfg?.Renderless ?? false);
        var nativeWindow = _platform.CreateWindow(config);

        var managedWindow = CreateWindow(plugin);
        managedWindow.AlwaysOnTop = floating;
        RegisterWindow(managedWindow, nativeWindow);

        nativeWindow.Show();

        RegisterBuiltinInvokeHandlers(managedWindow);
        Console.WriteLine($"[ApplicationRuntime] Spawned {windowType} window id={managedWindow.Id} titleBarStyle={titleBarStyle}");
        return (nativeWindow, managedWindow);
    }

    // === Built-in invoke handlers ===

    /// <summary>
    /// Register all built-in invoke() handlers on a freshly spawned window.
    /// These mirror Electron's ipcMain.handle() built-ins: app, window, dialog, shell.
    /// </summary>
    private void RegisterBuiltinInvokeHandlers(ManagedWindow window)
    {
        var windowId = window.Id;

        // ── app ──────────────────────────────────────────────────────────

        window.RegisterInvokeHandler("app:getPath", args =>
        {
            var name = args.ValueKind == System.Text.Json.JsonValueKind.Object &&
                       args.TryGetProperty("name", out var n) ? n.GetString() : null;
            var path = name switch
            {
                "userData" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _config.Name),
                "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "temp" => Path.GetTempPath(),
                "appRoot" => _rootDir,
                _ => _rootDir,
            };
            return Task.FromResult<object?>(path);
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
            var tcs = new TaskCompletionSource<object?>();
            RunOnMainThread(() =>
            {
                try
                {
                    var result = _windowManager.OnSpawnWindowAt?.Invoke(type);
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
                                panel.AllowedFileTypes = exts;
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

        // ── shell ─────────────────────────────────────────────────────────

        window.RegisterInvokeHandler("shell:openPath", args =>
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

        // ── http ──────────────────────────────────────────────────────────
        // Routes all fetch("/api/...") calls from the browser through HttpRouter.
        // The reply channel is computed from the invoke id so the bridge can match responses.

        window.RegisterInvokeHandler(HttpRouter.InvokeChannel, async args =>
        {
            var replyChannel = $"window:{windowId}:__reply__:http";
            return await _httpRouter.DispatchAsync(args, windowId, replyChannel);
        });
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

    private void CheckMemoryLimit()
    {
        if (++_memCheckCounter % 600 != 0) return;
        if (_memCheckCounter < 7200) return;

        var footprint = GetPhysicalFootprint();
        if (footprint <= 0) return;

        var windows = _windowManager.GetAllWindows().ToList();

        if (_memCheckCounter % 1800 == 0)
        {
            var gcHeap = GC.GetTotalMemory(false);
            Console.WriteLine($"[MemWatch] footprint={footprint / (1024 * 1024)}mb gc={gcHeap / (1024 * 1024)}mb windows={windows.Count}");

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
                var accountedFor = gcHeap + totalCacheBytes + expectedIOSurface;
                var unaccounted = footprint - accountedFor;

                Console.WriteLine($"[MemWatch] GPU cache: {totalResources} resources, {totalCacheBytes / (1024 * 1024)}mb across {windows.Count} windows");
                Console.WriteLine($"[MemWatch] IOSurface: expected={expectedIOSurface / (1024 * 1024)}mb (3 drawables × {windows.Count} windows)");
                Console.WriteLine($"[MemWatch] accounted={accountedFor / (1024 * 1024)}mb unaccounted={unaccounted / (1024 * 1024)}mb");
                Console.WriteLine($"[MemWatch] GC gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");
            }
        }

        // IOSurface orphan detection — if unaccounted memory exceeds 200MB,
        // purge all GPU caches and force GC to reclaim stale IOSurfaces.
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
        var accounted = GC.GetTotalMemory(false) + cacheTotal + expectedTotal;
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
