// BunProcess - Subprocess lifecycle with background stdout reader
// stdin: fire-and-forget writes (lock-protected)
// stdout: background thread reads lines, dispatches via OnLine callback

using System.Diagnostics;
using Keystone.Core;

namespace Keystone.Core.Management.Bun;

public class BunProcess : IDisposable
{
    private Process? _process;
    private StreamWriter? _writer;
    private readonly object _writeLock = new();
    private Thread? _readerThread;

    /// <summary>Called on background reader thread for each stdout line.</summary>
    public Action<string>? OnLine { get; set; }

    /// <summary>Called on background reader thread when the process stdout stream ends (crash or clean exit).
    /// Arg is the exit code, or -1 if unavailable.</summary>
    public Action<int>? OnExit { get; set; }

    public bool IsRunning => _process is { HasExited: false };

    public bool Start(string entryPoint, string? appRoot = null, string? compiledExe = null,
        Dictionary<string, string>? env = null)
    {
        if (IsRunning) return true;

        var engineDir = Path.GetDirectoryName(entryPoint) ?? "";

        string exe;
        string args;

        if (compiledExe != null && File.Exists(compiledExe))
        {
            // Package mode: run the compiled single-file executable directly
            exe = compiledExe;
            args = appRoot != null ? $"\"{appRoot}\"" : "";
        }
        else
        {
            // Dev mode: spawn via system bun
            exe = "bun";
            args = appRoot != null
                ? $"run \"{entryPoint}\" \"{appRoot}\""
                : $"run \"{entryPoint}\"";
        }

        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = engineDir
            };

            if (env != null)
                foreach (var (k, v) in env)
                    psi.Environment[k] = v;

            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bun] Failed to start: {ex.Message}");
            Notifications.Error($"Bun failed to start: {ex.Message}");
            return false;
        }

        if (_process == null) return false;

        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[Bun] {e.Data}"); };
        _process.BeginErrorReadLine();
        _writer = _process.StandardInput;

        // Background stdout reader — lives for the lifetime of the process
        var reader = _process.StandardOutput;
        _readerThread = new Thread(() =>
        {
            try
            {
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (line != null) OnLine?.Invoke(line);
                }
            }
            catch { }
            finally
            {
                // Stream ended — process has exited (crash or clean shutdown)
                var exitCode = -1;
                try { exitCode = _process?.ExitCode ?? -1; } catch { }
                OnExit?.Invoke(exitCode);
            }
        }) { IsBackground = true, Name = "BunStdoutReader" };
        _readerThread.Start();

        return true;
    }

    /// <summary>Write one NDJSON line to stdin. Non-blocking, thread-safe.</summary>
    public void Send(string jsonLine)
    {
        lock (_writeLock)
        {
            if (_writer == null) return;
            _writer.WriteLine(jsonLine);
            _writer.Flush();
        }
    }

    public void Stop()
    {
        if (_process is not { HasExited: false }) { _process = null; return; }
        try { Send("{\"type\":\"shutdown\"}"); } catch { }
        _process.WaitForExit(2000);
        if (!_process.HasExited) try { _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
        _process = null;
        _writer = null;
        _readerThread = null;
    }

    public void Dispose() => Stop();
}
