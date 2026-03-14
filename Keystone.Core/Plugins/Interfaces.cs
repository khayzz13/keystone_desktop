/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// Plugin interfaces for hot-reloadable window renderers and services

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
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

    /// <summary>Whether to exclude this window from tab group merging.</summary>
    bool ExcludeFromTabGroups => false;
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
    public virtual bool ExcludeFromTabGroups => false;

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

    /// <summary>Fired when the system is about to sleep (lid close, forced sleep).</summary>
    event Action? OnSystemWillSleep;
    /// <summary>Fired when the system wakes from sleep.</summary>
    event Action? OnSystemDidWake;

    /// <summary>
    /// Fired on any crash event (bun, webview, unhandled exception, render exception).
    /// Subscribe for unified observability across all process boundaries.
    /// </summary>
    event Action<CrashEvent>? OnCrash;

    /// <summary>Fired when a second app instance launches. Args: (argv, workingDir).</summary>
    event Action<string[], string>? OnSecondInstance;
    /// <summary>Fired when the OS opens URLs with this app (custom protocol, etc.).</summary>
    event Action<string[]>? OnOpenUrls;
    /// <summary>Fired when the OS opens a file with this app.</summary>
    event Action<string>? OnOpenFile;

    /// <summary>Prevent idle system sleep. Returns an opaque token to pass to EndPreventSleep.</summary>
    object? BeginPreventSleep(string reason);
    void EndPreventSleep(object? token);

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

    /// <summary>Named thread pools for service/plugin work consolidation.</summary>
    IThreadPoolManager ThreadPools { get; }

    /// <summary>
    /// Unified channel system — typed pub/sub, render-wake, alerts.
    /// Primary API for all C#-side data/event flow between plugins.
    /// </summary>
    IChannelManager Channels { get; }

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

    /// <summary>
    /// Unified IPC facade — routes calls across all process boundaries.
    /// Prefer context.Ipc.Bun/Worker/Web over context.Bun for new code.
    /// </summary>
    IIpcFacade Ipc { get; }
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

    /// <summary>
    /// Called after Bun ready + workers spawned + initial windows opened.
    /// Safe to query services, push to channels, interact with windows.
    /// If Bun is not configured, fires after window spawn before main loop.
    /// </summary>
    void OnReady(ICoreContext context) { }

    /// <summary>
    /// Called at the start of Shutdown(), before windows close and Bun stops.
    /// Save state, flush buffers, unsubscribe from external resources.
    /// </summary>
    void OnShutdown(ICoreContext context) { }
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
//  Thread Pool
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Named thread pool manager. Configure pool sizes in app startup,
/// then queue work by pool name from any service or plugin.
/// </summary>
public interface IThreadPoolManager
{
    void Configure(string name, int maxThreads);
    void QueueWork(string poolName, Action work);
    IManagedThreadPool Get(string name);
}

public interface IManagedThreadPool
{
    void QueueWork(Action work);
}

// ═══════════════════════════════════════════════════════════════
//  Channels — Unified Communication
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Unified channel system for C#-side data/event flow between plugins.
/// Typed pub/sub (Value/Event channels), render-wake (built on DataChannel),
/// and alerts (built on Notifications) — all with managed lifecycle and
/// automatic hot-reload cleanup.
/// Obtain via ICoreContext.Channels or ChannelManager.Instance.
/// </summary>
public interface IChannelManager
{
    // Typed pub/sub
    ValueChannel<T> Value<T>(string name);
    EventChannel<T> Event<T>(string name);

    // Request/reply (local C# plugin-to-plugin)
    CallChannel<TReq, TRes> Call<TReq, TRes>(string name);

    // Render wake (absorbs DataChannel)
    void Notify(string channel);
    IDisposable Subscribe(string channel, Action callback);
    IDisposable Subscribe(IEnumerable<string> channels, Action callback);

    // Alerts (absorbs Notifications)
    IAlertChannel Alert { get; }

    // Hot-reload cleanup — removes all subscriptions from unloaded assembly
    void UnsubscribeAll(Assembly assembly);
}

/// <summary>
/// Alert/notification channel. Wraps Notifications static class with
/// managed lifecycle and assembly-tracked subscribers for hot-reload safety.
/// </summary>
public interface IAlertChannel
{
    void Push(string message, NotificationLevel level = NotificationLevel.Info);
    void Error(string message);
    void Warn(string message);
    void Info(string message);
    IReadOnlyList<Notification> Recent { get; }
    IDisposable OnNotification(Action<Notification> callback);
    void Dismiss(Notification notification);
    void Clear();
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
    /// <summary>Register a handler that Bun services can call via ipc.host.call(name, args).</summary>
    void RegisterHostHandler(string name, Func<JsonElement?, Task<object?>> handler);
    /// <summary>Remove a host query handler.</summary>
    void RemoveHostHandler(string name);
    void Shutdown();
}

// ═══════════════════════════════════════════════════════════════
//  Unified IPC Facade
// ═══════════════════════════════════════════════════════════════

/// <summary>Execution plane — identifies which process boundary a message targets.</summary>
public enum IpcPlane { Host, Bun, Worker, Web }

/// <summary>Typed target for IPC routing.</summary>
public record IpcTarget
{
    public IpcPlane Plane { get; init; }
    public string? Name { get; init; }

    public static IpcTarget Host => new() { Plane = IpcPlane.Host };
    public static IpcTarget BunMain => new() { Plane = IpcPlane.Bun };
    public static IpcTarget BunWorker(string name) => new() { Plane = IpcPlane.Worker, Name = name };
    public static IpcTarget WebSurface => new() { Plane = IpcPlane.Web };
}

/// <summary>
/// Unified IPC surface — routes calls to the correct process boundary.
/// Obtain via ICoreContext.Ipc.
/// </summary>
public interface IIpcFacade
{
    /// <summary>Main Bun subprocess IPC.</summary>
    IIpcBun Bun { get; }

    /// <summary>Named worker subprocess IPC.</summary>
    IIpcWorker Worker(string name);

    /// <summary>The C#-side channel manager (same as ICoreContext.Channels).</summary>
    IChannelManager Channels { get; }

    /// <summary>WebKit content process IPC.</summary>
    IIpcWeb Web { get; }

    /// <summary>Generic call to any plane.</summary>
    Task<string?> Call(IpcTarget target, string op, object? payload = null);

    /// <summary>Generic fire-and-forget action to any plane.</summary>
    void Action(IpcTarget target, string action);
}

/// <summary>IPC surface for the main Bun subprocess.</summary>
public interface IIpcBun
{
    Task<string?> Call(string service, object? args = null);
    void Push(string channel, object data);
    /// <summary>Push with server-side retention — new WebSocket clients get the last value on connect.</summary>
    void PushValue(string channel, object data);
    void Action(string action);
    Task<string?> Eval(string code);

    /// <summary>Open a binary stream to a Bun service over the Unix socket.</summary>
    IStreamWriter OpenStream(string channel);
    /// <summary>Register a handler for incoming streams from Bun.</summary>
    void OnStream(string channel, Action<IStreamReader> handler);
}

/// <summary>IPC surface for a named Bun worker subprocess.</summary>
public interface IIpcWorker
{
    Task<string?> Call(string service, object? args = null);
    void Push(string channel, object data);
    void Action(string action);
}

/// <summary>IPC surface for WebKit content processes.</summary>
public interface IIpcWeb
{
    void Push(string channel, object data);
    /// <summary>Push with server-side retention — new WebSocket clients get the last value on connect.</summary>
    void PushValue(string channel, object data);
    void PushToWindow(string windowId, string channel, object data);
}

/// <summary>Write side of a binary stream — send chunks to Bun or browser.</summary>
public interface IStreamWriter : IDisposable
{
    void Write(ReadOnlySpan<byte> data);
    void Close();
    bool Backpressure { get; }
    int StreamId { get; }
}

/// <summary>Read side of a binary stream — async iterate over incoming chunks.</summary>
public interface IStreamReader : IAsyncEnumerable<ReadOnlyMemory<byte>>
{
    string Channel { get; }
    int StreamId { get; }
    void Cancel();
}
