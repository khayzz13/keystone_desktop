namespace Keystone.Core.Platform;

public interface IWebView : IDisposable
{
    void LoadUrl(string url);
    void EvaluateJavaScript(string js);
    void EvaluateJavaScriptBool(string js, Action<bool> completion);
    void InjectScriptOnLoad(string js);
    void AddMessageHandler(string name, Action<string> handler);
    void RemoveMessageHandler(string name);
    void SetFrame(double x, double y, double w, double h);
    void SetTransparentBackground();
    void RemoveFromParent();
    Action? OnCrash { get; set; }
}
