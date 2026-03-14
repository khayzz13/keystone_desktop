/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// BunWorker — Per-worker lifecycle manager for additional Bun subprocesses.
// Uses ManagedProcessBridge for protocol layer (pre-ready queue, callback routing).
// Restart logic delegated to ManagedProcess.Restart property.

using System.Text.Json;
using Keystone.Core.Management.Process;

namespace Keystone.Core.Management.Bun;

public class BunWorker : IDisposable
{
    public string Name { get; }
    public BunWorkerConfig Config { get; }
    public bool IsRunning => _bridge is { IsReady: true } && _process is { IsRunning: true };
    public int? Port => _port > 0 ? _port : null;
    public IReadOnlyList<string> Services => _services;

    private BunProcess? _process;
    private ManagedProcessBridge? _bridge;
    private int _port;
    private List<string> _services = new();

    // Launch params for restart
    private string? _workerHostPath;
    private string? _appRoot;
    private string? _compiledExe;

    // Events
    public Action<string, string>? OnServicePush;
    public Action<int>? OnCrash;
    public Action<int>? OnRestart;

    public BunWorker(BunWorkerConfig config)
    {
        Name = config.Name;
        Config = config;
    }

    public bool Start(string workerHostPath, string appRoot, string? compiledExe = null)
    {
        _workerHostPath = workerHostPath;
        _appRoot = appRoot;
        _compiledExe = compiledExe;

        var process = new BunProcess();
        process.Restart = new RestartPolicy(Config.MaxRestarts, Config.BaseBackoffMs);

        var env = new Dictionary<string, string>
        {
            ["KEYSTONE_WORKER_NAME"] = Config.Name,
            ["KEYSTONE_SERVICES_DIR"] = Config.ServicesDir,
            ["KEYSTONE_BROWSER_ACCESS"] = Config.BrowserAccess.ToString().ToLower(),
            ["KEYSTONE_APP_ROOT"] = appRoot,
        };
        if (BunManager.Instance.SessionToken is { } token)
            env["KEYSTONE_SESSION_TOKEN"] = token;

        if (Config.IsExtensionHost)
        {
            env["KEYSTONE_EXTENSION_HOST"] = "true";
            if (Config.AllowedChannels is { Count: > 0 })
                env["KEYSTONE_ALLOWED_CHANNELS"] = string.Join(",", Config.AllowedChannels);
        }

        // Create bridge — wires OnLine to protocol dispatch
        var bridge = new ManagedProcessBridge(process);
        bridge.OnServicePush = (ch, d) => OnServicePush?.Invoke(ch, d);
        bridge.OnRelay = (target, channel, data) => BunWorkerManager.Instance.Route(Name, target, channel, data);
        // Worker host queries route through the same BunManager registry
        bridge.OnHostQuery = BunManager.Instance.DispatchHostQuery;

        // Before bridge is ready, intercept ready signal
        process.OnLine = line =>
        {
            if (TryAttachFromReadySignal(bridge, process, line))
                return;
        };

        process.OnExit = exitCode =>
        {
            Console.WriteLine($"[BunWorker:{Name}] Process exited (code={exitCode})");
            Detach();
            OnCrash?.Invoke(exitCode);
        };

        if (!process.Start(workerHostPath, appRoot, compiledExe, env: env))
        {
            Console.WriteLine($"[BunWorker:{Name}] Failed to start");
            return false;
        }

        _process = process;
        _bridge = bridge;
        Console.WriteLine($"[BunWorker:{Name}] Started");
        return true;
    }

    private bool TryAttachFromReadySignal(ManagedProcessBridge bridge, BunProcess process, string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "ready")
                return false;

            _services = root.TryGetProperty("services", out var sv)
                ? sv.EnumerateArray().Select(e => e.GetString()!).ToList()
                : new List<string>();
            _port = root.TryGetProperty("port", out var p) ? p.GetInt32() : 0;

            bridge.MarkReady();
            process.ResetRestartCount();
            OnRestart?.Invoke(0);

            Console.WriteLine($"[BunWorker:{Name}] Ready: {_services.Count} services, port={_port}");
            return true;
        }
        catch { return false; }
    }

    private void Detach()
    {
        _bridge?.Detach();
        _bridge = null;
        _process = null;
        _services = new();
        _port = 0;
    }

    // ── Send ──────────────────────────────────────────────────────────

    public void Send(string jsonLine) => _bridge?.Send(jsonLine);

    public void Send(int id, string jsonLine, Action<string> onResponse)
        => _bridge?.Send(id, jsonLine, onResponse);

    public int NextId() => _bridge?.NextId() ?? 0;

    public Task<string?> Query(string service, object? args = null)
        => _bridge?.Query(service, args) ?? Task.FromResult<string?>(null);

    public void Push(string channel, object data) => _bridge?.Push(channel, data);

    public void HandleAction(string action) => _bridge?.HandleAction(action);

    public void Stop()
    {
        _process?.Stop();
        Detach();
    }

    public void Dispose() => Stop();
}
