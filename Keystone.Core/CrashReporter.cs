/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keystone.Core;

public static class CrashReporter
{
    private static string _crashDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".keystone", "crashes");

    private static readonly List<CrashEvent> _recent = new();
    private static readonly object _lock = new();
    private const int MaxRecent = 100;

    public static event Action<CrashEvent>? OnCrash;

    public static IReadOnlyList<CrashEvent> Recent
    {
        get { lock (_lock) return _recent.ToList(); }
    }

    public static void Report(string kind, Exception? ex, Dictionary<string, string>? extra = null)
    {
        var evt = new CrashEvent
        {
            Kind = kind,
            Timestamp = DateTime.UtcNow,
            Message = ex?.Message,
            StackTrace = ex?.ToString(),
            ProcessId = Environment.ProcessId,
            Extra = extra,
        };

        lock (_lock)
        {
            _recent.Add(evt);
            if (_recent.Count > MaxRecent)
                _recent.RemoveAt(0);
        }

        OnCrash?.Invoke(evt);

        try
        {
            Directory.CreateDirectory(_crashDir);
            var filename = $"crash-{evt.Timestamp:yyyyMMdd-HHmmss}-{kind}.json";
            File.WriteAllText(Path.Combine(_crashDir, filename),
                JsonSerializer.Serialize(evt, CrashJsonContext.Default.CrashEvent));
        }
        catch { /* crash reporting must never itself crash */ }

        PruneOldCrashes();
    }

    public static void ReportWidgetError(string tag, Exception ex)
        => Report("widget_build", ex, new() { ["widget"] = tag });

    private static void PruneOldCrashes()
    {
        try
        {
            var files = Directory.GetFiles(_crashDir, "crash-*.json");
            if (files.Length <= 50) return;
            Array.Sort(files);
            for (int i = 0; i < files.Length - 50; i++)
                File.Delete(files[i]);
        }
        catch { }
    }
}

public record CrashEvent
{
    public string Kind { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public string? Message { get; init; }
    public string? StackTrace { get; init; }
    public int ProcessId { get; init; }
    public Dictionary<string, string>? Extra { get; init; }
}

[JsonSerializable(typeof(CrashEvent))]
internal partial class CrashJsonContext : JsonSerializerContext;
