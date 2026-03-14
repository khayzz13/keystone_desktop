/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// ManagedProcess — General subprocess lifecycle.
// Extracted from BunProcess. No Bun-specific logic — usable for Go, Python, Rust, etc.
// stdin: fire-and-forget NDJSON writes (lock-protected)
// stdout: line-by-line reader on background thread

using System.Diagnostics;

namespace Keystone.Core.Management.Process;

public class ManagedProcess : IDisposable
{
    private System.Diagnostics.Process? _process;
    private StreamWriter? _writer;
    private readonly object _writeLock = new();
    private Thread? _readerThread;

    public string Name { get; }
    public int? ProcessId => _process?.Id;
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Optional restart policy. When set, the process will auto-restart on crash.</summary>
    public RestartPolicy? Restart { get; set; }

    /// <summary>Called on background reader thread for each stdout text line.</summary>
    public Action<string>? OnLine { get; set; }

    /// <summary>Called on background reader thread when stdout ends (crash or clean exit).
    /// Arg is the exit code, or -1 if unavailable.</summary>
    public Action<int>? OnExit { get; set; }

    // Restart state
    private int _restartCount;
    private volatile bool _shutdownRequested;
    private string? _lastExe;
    private string[]? _lastArgs;
    private string? _lastWorkingDir;
    private Dictionary<string, string>? _lastEnv;

    public ManagedProcess(string name)
    {
        Name = name;
    }

    public virtual bool Start(string exe, string[]? args = null, string? workingDir = null,
        Dictionary<string, string>? env = null)
    {
        if (IsRunning) return true;
        _shutdownRequested = false;

        // Store for restart
        _lastExe = exe;
        _lastArgs = args;
        _lastWorkingDir = workingDir;
        _lastEnv = env;

        var argStr = args != null ? string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)) : "";

        try
        {
            var psi = new ProcessStartInfo(exe, argStr)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? ""
            };

            if (env != null)
                foreach (var (k, v) in env)
                    psi.Environment[k] = v;

            _process = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{Name}] Failed to start: {ex.Message}");
            return false;
        }

        if (_process == null) return false;

        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[{Name}] {e.Data}"); };
        _process.BeginErrorReadLine();
        _writer = _process.StandardInput;

        var reader = _process.StandardOutput;
        _readerThread = new Thread(() =>
        {
            try
            {
                while (reader.ReadLine() is { } line)
                    OnLine?.Invoke(line);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[{Name}] Stdout reader error: {ex.Message}"); }
            finally
            {
                var exitCode = -1;
                try { exitCode = _process?.ExitCode ?? -1; }
                catch { }

                if (!_shutdownRequested && Restart is { } policy)
                    ScheduleRestart(exitCode, policy);

                OnExit?.Invoke(exitCode);
            }
        }) { IsBackground = true, Name = $"{Name}-StdoutReader" };
        _readerThread.Start();

        return true;
    }

    /// <summary>Write one NDJSON line to stdin. Non-blocking, thread-safe.</summary>
    public void Send(string line)
    {
        lock (_writeLock)
        {
            if (_writer == null) return;
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    /// <summary>Graceful shutdown: send message, wait, then kill.</summary>
    public void Stop(string? shutdownMessage = "{\"type\":\"shutdown\"}", int gracePeriodMs = 2000)
    {
        _shutdownRequested = true;
        if (_process is not { HasExited: false }) { _process = null; return; }
        if (shutdownMessage != null) try { Send(shutdownMessage); } catch { }
        _process.WaitForExit(gracePeriodMs);
        if (!_process.HasExited) try { _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
        _process = null;
        _writer = null;
        _readerThread = null;
    }

    public void Dispose() => Stop();

    private void ScheduleRestart(int exitCode, RestartPolicy policy)
    {
        var attempt = ++_restartCount;
        if (attempt > policy.MaxAttempts)
        {
            Console.WriteLine($"[{Name}] Restart limit ({policy.MaxAttempts}) reached");
            return;
        }

        var delayMs = (int)Math.Min(policy.BaseDelayMs * Math.Pow(2, attempt - 1), policy.MaxDelayMs);
        Console.WriteLine($"[{Name}] Exited (code={exitCode}), restarting in {delayMs}ms (attempt {attempt}/{policy.MaxAttempts})");

        Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            if (_shutdownRequested) return;

            _process?.Dispose();
            if (Start(_lastExe!, _lastArgs, _lastWorkingDir, _lastEnv))
                _restartCount = 0;
        });
    }

    /// <summary>Reset the restart counter (call after successful ready signal).</summary>
    public void ResetRestartCount() => _restartCount = 0;
}
