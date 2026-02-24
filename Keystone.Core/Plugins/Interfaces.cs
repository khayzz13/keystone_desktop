// Plugin interfaces for hot-reloadable window renderers and services

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Keystone.Core.Rendering;
using Keystone.Core.UI;

namespace Keystone.Core.Plugins;

public enum PluginRenderPolicy { Continuous, OnEvent }

public interface IWindowPlugin
{
    IEnumerable<string> Dependencies => Array.Empty<string>();
    string WindowType { get; }
    string WindowTitle => WindowType;
    (float Width, float Height) DefaultSize { get; }
    PluginRenderPolicy RenderPolicy => PluginRenderPolicy.Continuous;
    void Render(RenderContext ctx);

    /// <summary>
    /// Build a retained scene graph for cache-aware rendering.
    /// Return null to use immediate-mode Render() instead (opt-in).
    /// </summary>
    SceneNode? BuildScene(FrameState state) => null;

    /// <summary>
    /// Hit test at coordinates - returns action/cursor for click or hover
    /// Called on MouseDown (for actions) and MouseMove (for cursor updates)
    /// </summary>
    HitTestResult? HitTest(float x, float y, float width, float height) => null;

    /// <summary>
    /// Called on scroll events (deltaX, deltaY, mouseX, mouseY, width, height)
    /// </summary>
    Action<float, float, float, float, float, float>? OnScroll => null;

    /// <summary>
    /// Called on key down events
    /// </summary>
    Action<ushort, KeyModifiers>? OnKeyDown => null;

    /// <summary>
    /// Called on key up events
    /// </summary>
    Action<ushort, KeyModifiers>? OnKeyUp => null;

    /// <summary>Serialize window-specific config for workspace persistence.</summary>
    string? SerializeConfig() => null;

    /// <summary>Restore window config from workspace.</summary>
    void RestoreConfig(string json) { }

    /// <summary>Whether to exclude this window from workspace serialization.</summary>
    bool ExcludeFromWorkspace => false;
}

public abstract class WindowPluginBase : IWindowPlugin
{
    public abstract string WindowType { get; }
    public virtual string WindowTitle => WindowType;
    public virtual (float Width, float Height) DefaultSize => (800, 600);
    public virtual PluginRenderPolicy RenderPolicy => PluginRenderPolicy.Continuous;
    public virtual IEnumerable<string> Dependencies => Array.Empty<string>();
    public uint WindowId { get; set; }  // Set by ManagedWindow before render
    public abstract void Render(RenderContext ctx);
    public virtual SceneNode? BuildScene(FrameState state) => null;
    public virtual HitTestResult? HitTest(float x, float y, float width, float height) => null;
    public virtual string? SerializeConfig() => null;
    public virtual void RestoreConfig(string json) { }
    public virtual bool ExcludeFromWorkspace => false;

    // Overlay system — wired by ManagedWindow, positions at OverlayAnchorX
    public Action<IOverlayContent, double, double>? ShowOverlay { get; set; }
    public Action? CloseOverlay { get; set; }
}

/// <summary>
/// Content for floating overlay windows (dropdowns, panels, notifications).
/// Simpler than IWindowPlugin — just render and hit test.
/// </summary>
public interface IOverlayContent
{
    void Render(RenderContext ctx);
    HitTestResult? HitTest(float x, float y, float w, float h) => null;
}

public interface IServicePlugin
{
    string ServiceName { get; }
    bool RunOnBackgroundThread => false;
    void Initialize();
    void Shutdown();
}

public interface IWebScript
{
    string ScriptName { get; }
    void Initialize();
    void OnMessage(string message);
}

/// <summary>
/// Runtime context passed to ICorePlugin.Initialize().
/// Apps use this to register services, windows, subscribe to lifecycle events,
/// and configure the runtime — without reaching for singletons.
/// </summary>
public interface ICoreContext
{
    KeystoneConfig Config { get; }
    string RootDir { get; }

    /// <summary>Register a typed service in the ServiceLocator.</summary>
    void RegisterService<T>(T service) where T : class;

    /// <summary>Register a native window plugin type.</summary>
    void RegisterWindow(IWindowPlugin plugin);

    /// <summary>Register a service plugin.</summary>
    void RegisterService(IServicePlugin plugin);

    /// <summary>Subscribe to lifecycle events.</summary>
    event Action? OnBeforeRun;
    event Action? OnShutdown;

    /// <summary>
    /// Fired when the Bun subprocess exits unexpectedly (crash or OOM).
    /// Arg is the OS exit code. Bun will auto-restart per processRecovery config.
    /// </summary>
    event Action<int>? OnBunCrash;

    /// <summary>
    /// Fired after Bun successfully restarts following a crash.
    /// Arg is the restart attempt number (1-based).
    /// </summary>
    event Action<int>? OnBunRestart;

    /// <summary>
    /// Fired when a WKWebView content process terminates unexpectedly.
    /// Arg is the window Id. The WebView will auto-reload per processRecovery config.
    /// </summary>
    event Action<string>? OnWebViewCrash;

    /// <summary>Handle unrecognized action strings from ActionRouter.</summary>
    Action<string, string>? OnUnhandledAction { set; }

    /// <summary>Run an action on the main thread on the next loop iteration.</summary>
    void RunOnMainThread(Action action);

    /// <summary>Run an action on the main thread and block until it completes.</summary>
    void RunOnMainThreadAndWait(Action action);

    /// <summary>The Bun bridge — query services, push channels, eval JS.</summary>
    IBunService Bun { get; }

    /// <summary>Spawn and manage additional Bun worker processes.</summary>
    IBunWorkerManager Workers { get; }

    /// <summary>
    /// HTTP-style route router. Register handlers here and call them from the browser
    /// using a normal fetch("/api/...") — no real HTTP server involved.
    ///
    /// Under the hood, the browser-side SDK intercepts fetch() for paths under /api/
    /// and routes them through the existing invoke() bridge directly to C#.
    ///
    /// Example:
    ///   context.Http.Get("/api/notes", async req => {
    ///       var notes = await db.GetAllAsync();
    ///       return HttpResponse.Json(notes);
    ///   });
    ///
    /// Browser:
    ///   const notes = await fetch("/api/notes").then(r => r.json());
    /// </summary>
    IHttpRouter Http { get; }
}

/// <summary>
/// Fluent HTTP route registration. Obtain via context.Http.
/// </summary>
public interface IHttpRouter
{
    IHttpRouter Get(string path, Func<HttpRequest, Task<HttpResponse>> handler);
    IHttpRouter Post(string path, Func<HttpRequest, Task<HttpResponse>> handler);
    IHttpRouter Put(string path, Func<HttpRequest, Task<HttpResponse>> handler);
    IHttpRouter Delete(string path, Func<HttpRequest, Task<HttpResponse>> handler);
    IHttpRouter Patch(string path, Func<HttpRequest, Task<HttpResponse>> handler);
    IHttpRouter Get(string path, Func<HttpRequest, HttpResponse> handler);
    IHttpRouter Post(string path, Func<HttpRequest, HttpResponse> handler);
    IHttpRouter Put(string path, Func<HttpRequest, HttpResponse> handler);
    IHttpRouter Delete(string path, Func<HttpRequest, HttpResponse> handler);
}

public interface ICorePlugin
{
    string CoreName { get; }

    /// <summary>
    /// Called once after engine init, before windows spawn.
    /// Register services, subscribe to events, configure theme, add menus.
    /// </summary>
    void Initialize(ICoreContext context) { Initialize(); }

    /// <summary>Legacy parameterless overload — prefer Initialize(ICoreContext).</summary>
    void Initialize() { }
}

public interface ILibraryPlugin
{
    string LibraryName { get; }
    void Initialize();
}

public interface ILogicPlugin
{
    string LogicName { get; }
    void Initialize();

    /// <summary>
    /// Whether this plugin uses GPU compute shaders for rendering (Metal, etc).
    /// Plugins with GPU requirements should initialize compute resources lazily
    /// via ctx.Gpu in their Render method.
    /// </summary>
    bool RequiresGpu => false;

    /// <summary>
    /// Compositing order within the render canvas. Lower renders first (background).
    /// -100 = deep background
    ///    0 = standard content
    ///  100 = overlays
    ///  200 = HUD
    /// </summary>
    int RenderOrder => 0;

    /// <summary>
    /// Data sources this logic plugin consumes. Apps define their own dependency keys.
    /// </summary>
    IEnumerable<string> Dependencies => Array.Empty<string>();
}

public interface IScript
{
    string ScriptName { get; }
    void Execute(string[] args);
}

/// <summary>
/// Interface for plugins that preserve state across hot-reloads.
/// </summary>
public interface IStatefulPlugin
{
    byte[] SerializeState();
    void RestoreState(byte[] state);
}

/// <summary>
/// Interface for service plugins that support hot-reload with state preservation.
/// </summary>
public interface IReloadableService : IServicePlugin
{
    byte[]? SerializeState();
    void RestoreState(byte[]? state);
    TimeSpan ShutdownTimeout => TimeSpan.FromSeconds(5);
}

// ═══════════════════════════════════════════════════════════════
//  Bun Integration
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Registry and lifecycle controller for Bun worker processes.
/// Obtain via ICoreContext.Workers.
/// </summary>
public interface IBunWorkerManager
{
    void Stop(string name);
    void StopAll();
}

public interface IBunService
{
    bool IsRunning { get; }
    IReadOnlyList<string> Services { get; }
    IReadOnlyList<string> WebComponents { get; }
    int BunPort { get; }
    string? WebComponentUrl(string name);
    Task<string?> Query(string service, object? args = null);
    Task<string?> Eval(string code);
    void HandleAction(string action);
    /// <summary>Push data to a named WebSocket channel. Bun broadcasts to all subscribers on that channel.</summary>
    void Push(string channel, object data);
    /// <summary>Fired when Bun hot-reloads a web component. Arg is the component name (e.g. "dashboard").</summary>
    Action<string>? OnWebComponentHmr { get; set; }
    void Shutdown();
}
