namespace Keystone.Core.Platform;

public interface INativeWindow : IDisposable
{
    IntPtr Handle { get; }
    string Title { get; set; }
    double ScaleFactor { get; }
    (double w, double h) ContentBounds { get; }
    (double x, double y, double w, double h) Frame { get; }
    (double x, double y) MouseLocationInWindow { get; }
    void SetFrame(double x, double y, double w, double h, bool animate = false);
    void SetFloating(bool floating);
    void StartDrag();
    void Show();
    void Hide();
    void BringToFront();
    void Minimize();
    void Deminiaturize();
    void Zoom();
    void Close();
    void SetDelegate(INativeWindowDelegate del);
    void CreateWebView(Action<IWebView> callback);
    object? GetGpuSurface();

    // ── Window semantics ──────────────────────────────────────────────────
    void SetMinSize(double w, double h);
    void SetMaxSize(double w, double h);
    void SetAspectRatio(double ratio); // 0 = clear
    void SetOpacity(double opacity);   // 0.0–1.0
    void EnterFullscreen();
    void ExitFullscreen();
    bool IsFullscreen { get; }
    bool IsMinimized { get; }
    bool IsFocused { get; }
    void SetContentProtection(bool enabled);
    void SetIgnoreMouseEvents(bool ignore);
    void SetResizable(bool resizable);
    void SetParent(INativeWindow? parent);
}

public interface INativeWindowDelegate
{
    void OnResizeStarted();
    void OnResized(double w, double h);
    void OnResizeEnded();
    void OnClosed();
    void OnMoved(double x, double y);
    void OnFocused();
    void OnBlurred();
    void OnMiniaturized();
    void OnDeminiaturized();
    void OnEnteredFullscreen();
    void OnExitedFullscreen();
}
