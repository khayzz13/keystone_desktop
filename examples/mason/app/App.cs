using System;
using System.IO;
using System.Text.Json;
using Keystone.Core;
using Keystone.Core.Plugins;

namespace Mason;

public class App : ICorePlugin
{
    public string CoreName => "Mason";

    private HwTranscoder _transcoder = new();

    public void Initialize(ICoreContext context)
    {
        // Wire IBunService into the transcoder so it can push progress channels
        _transcoder.SetBunService(context.Bun);

        // Register the transcoder as a service plugin
        context.RegisterService(_transcoder);

        // ── HTTP routes — transcode-file serving ─────────────────────────────
        // Serve transcoded temp files by absolute path.
        // The browser requests /api/transcode-file?path=... after transcode completes.
        context.Http.Get("/api/transcode-file", req =>
        {
            if (!req.Query.TryGetValue("path", out var path) || string.IsNullOrEmpty(path))
                return HttpResponse.Error("Missing path", 400);

            if (!File.Exists(path))
                return HttpResponse.NotFound("File not found");

            var bytes = File.ReadAllBytes(path);
            return new HttpResponse { Status = 200, ContentType = "video/mp4", Body = bytes };
        });

        // Serve local media files for native formats (skips transcoding).
        context.Http.Get("/api/file", req =>
        {
            if (!req.Query.TryGetValue("path", out var path) || string.IsNullOrEmpty(path))
                return HttpResponse.Error("Missing path", 400);

            if (!File.Exists(path))
                return HttpResponse.NotFound("File not found");

            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch
            {
                ".mp3"  => "audio/mpeg",
                ".m4a"  => "audio/mp4",
                ".aac"  => "audio/aac",
                ".wav"  => "audio/wav",
                ".ogg"  => "audio/ogg",
                ".opus" => "audio/ogg",
                ".flac" => "audio/flac",
                ".mp4"  => "video/mp4",
                ".webm" => "video/webm",
                ".mov"  => "video/quicktime",
                _       => "application/octet-stream",
            };

            var bytes = File.ReadAllBytes(path);
            return new HttpResponse { Status = 200, ContentType = mime, Body = bytes };
        });

        // ── HTTP routes — AVFoundation transcoder ────────────────────────────

        context.Http.Get("/api/hw-transcode/formats", _ =>
            HttpResponse.Json(HwTranscoder.GetFormats()));

        context.Http.Get("/api/hw-transcode/can-handle", req =>
        {
            req.Query.TryGetValue("ext", out var ext);
            return HttpResponse.Json(new { canHandle = HwTranscoder.CanHandle(ext ?? "") });
        });

        context.Http.Post("/api/hw-transcode/convert", async req =>
        {
            string? inputPath = null;
            string? sessionId = null;

            try
            {
                inputPath = req.Body.TryGetProperty("input", out var inp) ? inp.GetString() : null;
                sessionId = req.Body.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
            }
            catch
            {
                return HttpResponse.Error("Invalid JSON body", 400);
            }

            if (string.IsNullOrEmpty(inputPath) || string.IsNullOrEmpty(sessionId))
                return HttpResponse.Error("Missing input or sessionId", 400);

            var ext = Path.GetExtension(inputPath);
            if (!HwTranscoder.CanHandle(ext))
                return HttpResponse.Error("Unsupported format for AVFoundation", 415);

            // Start transcoding in background — progress arrives via push channels
            _ = _transcoder.Convert(inputPath, sessionId);

            return HttpResponse.Json(new { ok = true, async = true });
        });

        context.OnUnhandledAction = (action, source) =>
            Console.WriteLine($"[Mason] Unhandled action: {action} from {source}");

        Console.WriteLine("[Mason] App initialized");
    }
}
