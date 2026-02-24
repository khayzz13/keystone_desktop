using System;
using System.IO;
using Keystone.Core;
using Keystone.Core.Platform;
using Keystone.Core.Runtime;

namespace Keystone.App;

class Program
{
    static void Main(string[] args)
    {
        // Tee stdout/stderr to log file for Dev Console
        SetupLogging();
        Console.WriteLine("[Keystone] Starting...");

        // Resolve paths relative to executable
        var exeDir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
                     ?? AppContext.BaseDirectory;
        // KEYSTONE_ROOT env var or first CLI arg overrides root discovery (used by build scripts)
        var rootDir = Environment.GetEnvironmentVariable("KEYSTONE_ROOT")
                      ?? (args.Length > 0 && Directory.Exists(args[0]) ? args[0] : null)
                      ?? FindRootDir(exeDir)
                      ?? exeDir;

        // Load config (keystone.config.json → keystone.json search order)
        var config = KeystoneConfig.LoadOrDefault(rootDir);
        Console.WriteLine($"[Keystone] App: {config.Name} ({config.Id})");

        // Platform init (must happen before ApplicationRuntime)
        NativeLibraryLoader.Initialize();

        // Register app-specific native library search path
        if (config.Plugins.NativeDir is { } nativeDir)
        {
            var nativePath = Path.IsPathRooted(nativeDir)
                ? nativeDir
                : Path.Combine(rootDir, nativeDir);
            if (Directory.Exists(nativePath))
                NativeLibraryLoader.AddSearchPath(nativePath);
        }
#if MACOS
        IPlatform platform = new Keystone.Core.Platform.MacOS.MacOSPlatform();
        platform.Initialize();
        Keystone.Core.Graphics.Skia.SkiaWindow.Initialize();
#elif WINDOWS
        IPlatform platform = new Keystone.Core.Platform.Windows.WindowsPlatform();
        platform.Initialize();
        Keystone.Core.Graphics.Skia.D3D.D3DSkiaWindow.Initialize();
#elif LINUX
        IPlatform platform = new Keystone.Core.Platform.Linux.LinuxPlatform();
        platform.Initialize();
#else
        throw new PlatformNotSupportedException("Unsupported OS");
        IPlatform platform = null!;
#endif

        // Create, initialize, and run
        var runtime = new ApplicationRuntime(config, rootDir, platform);
        runtime.Initialize();

        Console.WriteLine("[Keystone] Entering main loop...");
        try
        {
            runtime.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Keystone] Fatal: {ex}");
        }
        finally
        {
            runtime.Shutdown();
        }
    }

    private static void SetupLogging()
    {
        var logPath = Environment.GetEnvironmentVariable("KEYSTONE_LOG")
            ?? Path.Combine(Path.GetTempPath(), "keystone.log");
        var logStream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var logWriter = new StreamWriter(logStream) { AutoFlush = true };
        Console.SetOut(new TeeTextWriter(Console.Out, logWriter));
        Console.SetError(new TeeTextWriter(Console.Error, logWriter));
    }

    /// <summary>Walk up from exe dir looking for a config file or dylib/ folder.</summary>
    private static string? FindRootDir(string startDir)
    {
        // AppContext.BaseDirectory on macOS .NET = Contents/MonoBundle/
        // startDir = parent = Contents/
        // Check Contents/Resources/ first — that's where the packaged config lives.
        var resources = Path.Combine(startDir, "Resources");
        if (KeystoneConfig.FindConfigFile(resources) != null) return resources;

        var dir = startDir;
        for (int i = 0; i < 6; i++)
        {
            if (KeystoneConfig.FindConfigFile(dir) != null) return dir;
            if (Directory.Exists(Path.Combine(dir, "dylib"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        return null;
    }

}

/// <summary>Writes to both console and log file simultaneously.</summary>
class TeeTextWriter : TextWriter
{
    private readonly TextWriter _a, _b;
    public TeeTextWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
    public override System.Text.Encoding Encoding => _a.Encoding;
    public override void Write(char value) { _a.Write(value); _b.Write(value); }
    public override void Write(string? value) { _a.Write(value); _b.Write(value); }
    public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
    public override void Flush() { _a.Flush(); _b.Flush(); }
}
