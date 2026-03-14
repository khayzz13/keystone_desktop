/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using Keystone.Core;
using Keystone.Core.Management.Bun;
using Keystone.Core.Management.Protocol;
using Keystone.Core.Security;

namespace Keystone.Core.Runtime;

/// <summary>
/// Owns Bun process lifecycle: start, wire stdout/exit, restart with exponential backoff, spawn workers.
/// All external dependencies injected via Config record — no singleton access for side effects.
/// </summary>
internal sealed class ProcessSupervisor : IDisposable
{
    internal record Config(
        ProcessRecoveryConfig Recovery,
        List<BunWorkerConfig>? Workers,
        string? CompiledWorkerExe,
        Action<Action> RunOnMainThread,
        Action<int> OnBunCrash,
        Action<int> OnBunRestart,
        Action<string> ExecuteAction,
        Action<string> HotSwapAllSlots,
        Action<int>? OnSchemePortReady,
        Action? OnRuntimeReady,
        CancellationToken Cancellation
    );

    readonly Config _cfg;
    readonly string _sessionToken = Guid.NewGuid().ToString();
    BunProcess? _bunProcess;
    BinarySocketServer? _binarySocket;
    string? _bunHostPath;
    string? _bunAppRoot;
    string? _bunCompiledExe;
    int _bunRestartAttempt;
    volatile bool _bunRestartScheduled;

    public ProcessSupervisor(Config cfg)
    {
        _cfg = cfg;
        BunManager.Instance.SessionToken = _sessionToken;
    }

    public void Start(string hostPath, string appBunRoot, string? compiledExe = null)
    {
        _bunHostPath = hostPath;
        _bunAppRoot = appBunRoot;
        _bunCompiledExe = compiledExe;

        // Start binary socket server before Bun so it's ready when Bun connects
        _binarySocket?.Dispose();
        var socketPath = BinarySocketServer.CreateSocketPath();
        _binarySocket = new BinarySocketServer(socketPath);
        _binarySocket.OnEnvelope = BunManager.Instance.HandleBinaryEnvelope;
        _binarySocket.Start();
        BunManager.Instance.BinarySocket = _binarySocket;

        var process = new BunProcess();
        WireBunProcess(process);

        var env = new Dictionary<string, string> { ["KEYSTONE_SESSION_TOKEN"] = _sessionToken };
        if (process.Start(hostPath, appBunRoot, compiledExe, env: env, binarySocketPath: socketPath))
        {
            _bunProcess = process;
            Console.WriteLine(compiledExe != null
                ? "[ProcessSupervisor] Bun process started (compiled)"
                : "[ProcessSupervisor] Bun process started (dev)");
        }
        else
        {
            Console.WriteLine("[ProcessSupervisor] WARNING: Bun process failed to start");
            Notifications.Warn("Bun process failed to start");
        }
    }

    void WireBunProcess(BunProcess process)
    {
        process.OnLine = line =>
        {
            if (!BunManager.Instance.IsRunning &&
                BunManager.Instance.TryAttachFromReadySignal(process, line))
            {
                _cfg.OnSchemePortReady?.Invoke(BunManager.Instance.BunPort);
                SpawnConfiguredWorkers();
                _cfg.OnRuntimeReady?.Invoke();
                return;
            }
        };

        process.OnExit = exitCode =>
        {
            if (_cfg.Cancellation.IsCancellationRequested) return;

            Console.WriteLine($"[ProcessSupervisor] Bun process exited (code={exitCode})");
            CrashReporter.Report("bun_crash", null, new() { ["exitCode"] = exitCode.ToString() });
            BunManager.Instance.Detach();
            _cfg.OnBunCrash(exitCode);

            if (_cfg.Recovery.BunAutoRestart)
                ScheduleBunRestart();
            else
                Console.WriteLine("[ProcessSupervisor] Bun auto-restart disabled");
        };

        BunManager.Instance.OnWebAction = action => _cfg.ExecuteAction(action);
        BunManager.Instance.OnWebComponentHmr = component => _cfg.HotSwapAllSlots(component);
    }

    void ScheduleBunRestart()
    {
        if (_bunRestartScheduled) return;
        _bunRestartScheduled = true;

        var attempt = ++_bunRestartAttempt;
        var cfg = _cfg.Recovery;

        if (attempt > cfg.BunMaxRestarts)
        {
            Console.WriteLine($"[ProcessSupervisor] Bun restart limit ({cfg.BunMaxRestarts}) reached — giving up");
            Notifications.Error($"Bun process failed to recover after {cfg.BunMaxRestarts} attempts.");
            _bunRestartScheduled = false;
            return;
        }

        var delayMs = (int)Math.Min(cfg.BunRestartBaseDelayMs * Math.Pow(2, attempt - 1), cfg.BunRestartMaxDelayMs);
        Console.WriteLine($"[ProcessSupervisor] Restarting Bun in {delayMs}ms (attempt {attempt}/{cfg.BunMaxRestarts})");

        Task.Run(async () =>
        {
            await Task.Delay(delayMs);

            _cfg.RunOnMainThread(() =>
            {
                _bunRestartScheduled = false;
                _bunProcess?.Dispose();

                var process = new BunProcess();
                WireBunProcess(process);

                var restartEnv = new Dictionary<string, string> { ["KEYSTONE_SESSION_TOKEN"] = _sessionToken };
                if (process.Start(_bunHostPath!, _bunAppRoot!, _bunCompiledExe,
                    env: restartEnv, binarySocketPath: _binarySocket?.SocketPath))
                {
                    _bunProcess = process;
                    Console.WriteLine($"[ProcessSupervisor] Bun restarted (attempt {attempt})");
                    _cfg.OnBunRestart(attempt);
                    _bunRestartAttempt = 0;
                }
                else
                {
                    Console.WriteLine($"[ProcessSupervisor] Bun restart attempt {attempt} failed");
                    Notifications.Warn($"Bun restart attempt {attempt} failed");
                    ScheduleBunRestart();
                }
            });
        });
    }

    void SpawnConfiguredWorkers()
    {
        var workers = _cfg.Workers;
        if (workers == null || workers.Count == 0) return;

        // Resolve compiled worker exe for package mode
        string? compiledWorkerExe = null;
        if (_cfg.CompiledWorkerExe is { } workerExeName)
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
                Console.WriteLine($"[ProcessSupervisor] Worker exe: {compiledWorkerExe}");
        }

        var workerHostPath = Path.Combine(Path.GetDirectoryName(_bunHostPath!)!, "worker-host.ts");
        var readyCount = 0;
        var totalAutoStart = workers.Count(w => w.AutoStart);

        foreach (var wCfg in workers.Where(w => w.AutoStart))
        {
            var worker = BunWorkerManager.Instance.Spawn(wCfg, workerHostPath, _bunAppRoot!, compiledWorkerExe);
            worker.OnRestart = attempt =>
                Console.WriteLine($"[ProcessSupervisor] Worker '{wCfg.Name}' recovered (attempt {attempt})");

            var checkReady = () =>
            {
                if (worker.IsRunning && Interlocked.Increment(ref readyCount) == totalAutoStart)
                    BunWorkerManager.Instance.BroadcastPorts();
            };

            Task.Run(async () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    await Task.Delay(100);
                    if (worker.IsRunning) { checkReady(); return; }
                }
                Console.WriteLine($"[ProcessSupervisor] Worker '{wCfg.Name}' did not become ready in time");
            });
        }
    }

    public void Shutdown()
    {
        _bunProcess?.Dispose();
        _bunProcess = null;
        _binarySocket?.Dispose();
        _binarySocket = null;
    }

    public void Dispose() => Shutdown();
}
