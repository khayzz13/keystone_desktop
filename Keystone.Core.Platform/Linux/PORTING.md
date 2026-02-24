# Porting Keystone to Windows & Linux

This covers what's already done for you and what you need to build. The platform abstraction layer (`IPlatform`, `INativeWindow`, `IWebView`) is complete — core engine files (`ManagedWindow`, `WindowManager`, `ApplicationRuntime`) are platform-agnostic and never need to be touched. You implement the three interfaces, wire them in `Program.cs`, and everything works.

---

## What's Already Laid Out

Three interfaces in `Keystone.Core.Platform/Abstractions/`:

### IPlatform
The application-level platform. One instance, created in `Program.cs`, passed to `ApplicationRuntime`.

| Method | What it does |
|--------|-------------|
| `Initialize()` | Boot the windowing system — Win32 message loop setup, GTK/X11/Wayland init |
| `Quit()` | Terminate the application |
| `PumpRunLoop(seconds)` | Process pending OS events for up to N seconds (called every frame) |
| `GetMainScreenFrame()` | Primary monitor work area as `(x, y, w, h)` |
| `SetCursor(CursorType)` | Set system cursor shape (arrow, pointer, text, resize, etc.) |
| `GetMouseLocation()` | Global mouse position in screen coordinates |
| `IsMouseButtonDown()` | Whether primary button is currently held |
| `BringAllWindowsToFront()` | Raise all app windows |
| `CreateWindow(WindowConfig)` | Create a platform window, return `INativeWindow` |
| `CreateOverlayWindow(WindowConfig)` | Create always-on-top borderless overlay |
| `ShowOpenDialogAsync(opts)` | Native file-open dialog |
| `ShowSaveDialogAsync(opts)` | Native file-save dialog |
| `ShowMessageBoxAsync(opts)` | Native alert, returns button index |
| `OpenExternal(url)` | Open URL in default browser |
| `OpenPath(path)` | Open file/folder in OS handler |
| `InitializeMenu(callback, config)` | Build the app menu bar |
| `AddMenuItem(menu, title, action, shortcut)` | Dynamic menu item at runtime |
| `AddToolScripts(names)` | Populate Tools menu |
| `SetWindowListProvider(provider)` | Register window list for taskbar/dock |

`WindowConfig` is a record: `(double X, Y, Width, Height, bool Floating, string TitleBarStyle)`. `TitleBarStyle` is `"hidden"` (transparent title bar, window content fills entire frame) or default (standard borderless).

### INativeWindow
One per window. Wraps the native window handle.

| Member | Notes |
|--------|-------|
| `Handle` | `IntPtr` — HWND on Windows, X11 Window / GdkWindow* / wl_surface* on Linux |
| `Title` | Get/set window title |
| `ScaleFactor` | HiDPI scale (2.0 = Retina/200%). Cache this — it's read from the render thread |
| `ContentBounds` | Content area size in logical points (excludes title bar/borders) |
| `Frame` | Full window rect in screen coords `(x, y, w, h)` |
| `MouseLocationInWindow` | Mouse pos relative to content area origin |
| `SetFrame(x, y, w, h, animate)` | Move + resize |
| `SetFloating(bool)` | Toggle always-on-top |
| `StartDrag()` | Initiate window drag from current mouse position |
| `Show/Hide/BringToFront/Minimize/Deminiaturize/Zoom/Close` | Standard window operations |
| `SetDelegate(INativeWindowDelegate)` | Resize/move/close callbacks (see below) |
| `CreateWebView(callback)` | Create a web view inside the window, fire callback when ready |
| `GetGpuSurface()` | Return the drawable GPU surface — see GPU section below |

`INativeWindowDelegate` has: `OnResizeStarted()`, `OnResized(w, h)`, `OnResizeEnded()`, `OnClosed()`, `OnMoved(x, y)`. Hook these to your window's resize/move/close events.

### IWebView
One per web component slot in a window.

| Method | Notes |
|--------|-------|
| `LoadUrl(url)` | Navigate. WebView2: `CoreWebView2.Navigate()`. WebKitGTK: `webkit_web_view_load_uri()` |
| `EvaluateJavaScript(js)` | Fire-and-forget JS execution |
| `EvaluateJavaScriptBool(js, callback)` | Execute JS, parse bool result |
| `InjectScriptOnLoad(js)` | JS that runs on every page load (user scripts) |
| `AddMessageHandler(name, handler)` | Register a named channel for JS→native IPC |
| `RemoveMessageHandler(name)` | Unregister |
| `SetFrame(x, y, w, h)` | Position/size within parent window |
| `SetTransparentBackground()` | Make background transparent (composited over GPU content) |
| `RemoveFromParent()` | Detach from parent before dispose |
| `OnCrash` | Callback for renderer process crash recovery |

**JS→native IPC bridge**: macOS uses `window.webkit.messageHandlers[name].postMessage(body)`. You need the same API shape from JS:
- **WebView2**: `window.chrome.webview.postMessage(body)` — you route by name in the native handler, or inject a shim that maps `window.webkit.messageHandlers[name].postMessage` to the WebView2 API.
- **WebKitGTK**: Same `window.webkit.messageHandlers` API as macOS (WebKit under the hood).

---

## GPU / Skia / Metal — What Needs Porting

This is the most involved piece. The current rendering pipeline is Metal-specific. Here's exactly how it works and what changes per platform.

### Current Architecture (macOS)

```
CADisplayLink (VSync timer, fires on background thread)
  → broadcasts to per-window ManualResetEventSlim subscribers
  → WindowRenderThread wakes, calls ManagedWindow.RenderOnThread(gpu)
    → WindowGpuContext.BeginFrame(CAMetalLayer, w, h)
      → CAMetalLayer.NextDrawable()     ← Metal: acquire framebuffer
      → GRMtlTextureInfo(drawable.Texture)
      → GRBackendRenderTarget(w, h, textureInfo)
      → SKSurface.Create(grContext, backendRT, ...)
      → return SKCanvas                 ← platform-independent from here
    → SceneRenderer draws UI on SKCanvas ← fully platform-independent
    → WindowGpuContext.FinishAndPresent()
      → grContext.Flush() + Submit()
      → commandBuffer.PresentDrawable() ← Metal: swap
      → commandBuffer.Commit() + WaitUntilCompleted()
```

**Platform-independent** (no changes needed): `SceneRenderer`, `RenderContext`, `SkiaPaintCache`, all `BuildScene` plugin code, `SKCanvas` drawing. Everything above `BeginFrame` and below `FinishAndPresent` is pure Skia.

**Platform-specific** (must be replaced):

### 1. `SkiaWindow.cs` — Shared GPU Device

Currently holds `IMTLDevice` (Metal's GPU handle). Singleton, thread-safe.

| macOS | Windows | Linux |
|-------|---------|-------|
| `MTLDevice.SystemDefault` | `SharpDX.Direct3D12.Device` or Vulkan `VkDevice` | Vulkan `VkDevice` or EGL display |

You create the equivalent project (e.g. `Keystone.Core.Graphics.Skia.D3D` or `Keystone.Core.Graphics.Skia.Vulkan`) with the same `SkiaWindow.Initialize()` → `CreateWindowContext()` factory pattern. Or refactor the existing one behind an interface.

### 2. `WindowGpuContext.cs` — Per-Window GPU State

This is the core file. Each window gets its own graphics context. The key methods:

**`Create(device)`** — Create per-window GPU resources:

| macOS (Metal) | Windows (D3D12) | Linux (Vulkan) |
|---------------|-----------------|----------------|
| `device.CreateCommandQueue()` | `device.CreateCommandQueue(CommandListType.Direct)` | `vkCreateCommandPool()` + `vkAllocateCommandBuffers()` |
| `GRContext.CreateMetal(GRMtlBackendContext)` | `GRContext.CreateDirect3D(GRD3DBackendContext)` | `GRContext.CreateVulkan(GRVkBackendContext)` |

**`BeginFrame(surface, w, h)`** — Acquire a framebuffer and create an SKSurface:

| macOS (Metal) | Windows (D3D12) | Linux (Vulkan) |
|---------------|-----------------|----------------|
| `CAMetalLayer.NextDrawable()` | `swapChain.AcquireNextBuffer()` | `vkAcquireNextImageKHR()` |
| `GRMtlTextureInfo(drawable.Texture)` | `GRD3DTextureResourceInfo(d3dTexture)` | `GRVkImageInfo(vkImage)` |
| `new GRBackendRenderTarget(w, h, mtlInfo)` | `new GRBackendRenderTarget(w, h, d3dInfo)` | `new GRBackendRenderTarget(w, h, vkInfo)` |
| `SKSurface.Create(grContext, backendRT, TopLeft, Bgra8888)` | same | same |

After `SKSurface.Create` returns an `SKCanvas`, everything is identical across platforms.

**`FinishAndPresent()`** — Flush Skia and present:

| macOS (Metal) | Windows (D3D12) | Linux (Vulkan) |
|---------------|-----------------|----------------|
| `grContext.Flush()` + `Submit(sync)` | same | same |
| `commandBuffer.PresentDrawable()` | `commandQueue.ExecuteCommandLists()` | `vkQueueSubmit()` |
| `commandBuffer.Commit()` + `WaitUntilCompleted()` | `swapChain.Present()` + fence wait | `vkQueuePresentKHR()` + fence wait |

### 3. `DisplayLink.cs` — VSync Timer

Fires a callback at display refresh rate on a background thread. Broadcasts to per-window render threads via `ManualResetEventSlim`.

| macOS | Windows | Linux |
|-------|---------|-------|
| `CADisplayLink` on dedicated `NSRunLoop` thread | `IDXGIOutput.WaitForVBlank()` on a thread, or `DwmFlush()`, or a high-res timer at monitor refresh rate | DRM vblank ioctl, or `libdrm` vblank event, or a timer at refresh rate |

The broadcast pattern (`Subscribe`/`Unsubscribe`/`Resubscribe` with `ManualResetEventSlim` per window) is reusable. Only the VSync source changes.

### 4. `WindowRenderThread.cs` — Per-Window Render Loop

Almost platform-independent already. The one macOS-specific line:

```csharp
using var pool = new NSAutoreleasePool(); // ObjC memory management
```

Windows/Linux: remove this line (no equivalent needed — .NET GC handles everything). The rest of the file (`_vsyncSignal.Wait()` → `RenderOnThread()` → suspend/resume) is pure C#.

### 5. `INativeWindow.GetGpuSurface()`

This is how `ManagedWindow` gets the drawable surface. Returns `object?`, cast in `OnCreated()`:

| macOS | Windows | Linux |
|-------|---------|-------|
| Returns `CAMetalLayer` | Return your swap chain or DXGI surface | Return `VkSurfaceKHR` or EGL surface |

`ManagedWindow.OnCreated()` currently casts to `CAMetalLayer`. The Windows/Linux contributor changes this cast (or abstracts it — `ManagedWindow` still has `using CoreAnimation` for this reason).

### 6. `MacOSPlatform.ConfigureMetalLayer()`

Static helper called from `ManagedWindow` to configure the GPU layer on a window. Your equivalent sets up the swap chain/surface on your window:

| macOS | Windows | Linux |
|-------|---------|-------|
| Set pixel format, drawable count, contents scale on `CAMetalLayer` | Create `IDXGISwapChain` for HWND | Create `VkSwapchainKHR` for surface |

---

## Entry Point Wiring

`Program.cs` and `KeystoneApp.cs` create the platform and pass it through:

```csharp
// Current macOS:
var platform = new MacOSPlatform();
platform.Initialize();
SkiaWindow.Initialize();  // GPU init — replace with your GPU project's equivalent
var runtime = new ApplicationRuntime(config, rootDir, platform);
```

Add your platform branch:
```csharp
IPlatform platform = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
    ? new MacOSPlatform()
    : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    ? new WindowsPlatform()
    : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
    ? new LinuxPlatform()
    : throw new PlatformNotSupportedException();
```

---

## What You Never Touch

- `ManagedWindow.cs` — platform-agnostic (talks through `INativeWindow` / `IWebView`)
- `WindowManager.cs` — platform-agnostic (except `ProcessEvents` which is macOS event routing, needs its own abstraction eventually)
- `ApplicationRuntime.cs` — platform-agnostic (talks through `IPlatform`)
- `SceneRenderer`, `FlexRenderer`, `TaffyInterop` — pure layout/rendering math
- Plugin system, Bun/HMR, TypeScript toolchain — all platform-independent
- `RenderContext`, `FrameState`, `IGpuContext` — abstractions, no platform code

## Recommended Approach

1. Get a window on screen — implement `IPlatform.Initialize()` + `CreateWindow()` + `PumpRunLoop()` + `INativeWindow` basics (Show/Hide/Frame/SetDelegate)
2. Get WebView working — implement `CreateWebView()` + `IWebView` with message handlers. This alone gets web-only apps running
3. Add GPU rendering — create your `WindowGpuContext` equivalent with the Skia backend for your graphics API. This is the biggest piece but web-only apps work without it
4. Wire up dialogs, menus, shell commands — the remaining `IPlatform` surface area
