using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Keystone.Core.Plugins;

namespace Mason;

/// <summary>
/// Hardware-accelerated media transcoder using macOS AVFoundation.
/// Registered as an IServicePlugin — the App ICorePlugin wires up HTTP routes that delegate here.
/// </summary>
public class HwTranscoder : IServicePlugin
{
    public string ServiceName => "hw-transcoder";
    public bool RunOnBackgroundThread => true;

    private IBunService? _bun;

    /// <summary>Called by App.cs after receiving ICoreContext.</summary>
    public void SetBunService(IBunService bun) => _bun = bun;

    // ── AVFoundation P/Invoke ────────────────────────────────────────────────

    private const string AVFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    [DllImport("libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr ObjcGetClass(string name);

    [DllImport("libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName(string name);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcMsgSend(IntPtr receiver, IntPtr selector);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcMsgSendUrl(IntPtr receiver, IntPtr selector, IntPtr url);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcMsgSendStr(IntPtr receiver, IntPtr selector, IntPtr str);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void ObjcMsgSendVoid(IntPtr receiver, IntPtr selector, IntPtr arg);

    // ── Supported formats ────────────────────────────────────────────────────

    private static readonly string[] SupportedInputExts =
        [".mp4", ".mov", ".m4v", ".m4a", ".mp3", ".aac", ".wav", ".caf", ".aiff", ".3gp"];

    private static readonly string[] SupportedOutputPresets =
        ["AVAssetExportPresetHighestQuality", "AVAssetExportPreset1920x1080", "AVAssetExportPreset1280x720"];

    public static bool CanHandle(string ext) =>
        Array.Exists(SupportedInputExts, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));

    public static object GetFormats() => new
    {
        inputFormats = SupportedInputExts,
        outputFormat = "mp4",
        presets = SupportedOutputPresets,
        hardwareAccelerated = true,
    };

    // ── Transcoding ──────────────────────────────────────────────────────────

    public async Task<string> Convert(string inputPath, string sessionId, CancellationToken ct = default)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "mason-hw-transcode");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, $"{sessionId}.mp4");

        if (File.Exists(outPath))
        {
            _bun?.Push($"transcode:ready:{sessionId}", new
            {
                url = $"/api/transcode-file?path={Uri.EscapeDataString(outPath)}"
            });
            return outPath;
        }

        await Task.Run(() => RunAvFoundationExport(inputPath, outPath, sessionId, ct), ct);
        return outPath;
    }

    private void RunAvFoundationExport(string inputPath, string outPath, string sessionId, CancellationToken ct)
    {
        // Load AVFoundation
        _ = NativeLibrary.Load(AVFoundation);

        // NSString helpers
        IntPtr nsStringClass = ObjcGetClass("NSString");
        IntPtr stringWithUtf8 = SelRegisterName("stringWithUTF8String:");

        IntPtr MakeNSString(string s)
        {
            using var handle = new SafeHGlobalHandle(Marshal.StringToHGlobalAnsi(s));
            return ObjcMsgSendStr(nsStringClass, stringWithUtf8, handle.Handle);
        }

        // NSURL for input
        IntPtr nsUrlClass = ObjcGetClass("NSURL");
        IntPtr fileUrlSel = SelRegisterName("fileURLWithPath:");
        IntPtr inputNsStr = MakeNSString(inputPath);
        IntPtr inputUrl = ObjcMsgSendStr(nsUrlClass, fileUrlSel, inputNsStr);

        // NSURL for output
        IntPtr outNsStr = MakeNSString(outPath);
        IntPtr outputUrl = ObjcMsgSendStr(nsUrlClass, fileUrlSel, outNsStr);

        // AVURLAsset
        IntPtr assetClass = ObjcGetClass("AVURLAsset");
        IntPtr assetWithUrlSel = SelRegisterName("assetWithURL:");
        IntPtr asset = ObjcMsgSendUrl(assetClass, assetWithUrlSel, inputUrl);

        // AVAssetExportSession
        IntPtr exportClass = ObjcGetClass("AVAssetExportSession");
        IntPtr initSel = SelRegisterName("initWithAsset:presetName:");

        IntPtr presetStr = MakeNSString("AVAssetExportPresetHighestQuality");
        // Use objc_msgSend with two args via reflection workaround
        IntPtr exportSession = ObjcMsgSendTwoArgs(exportClass, SelRegisterName("alloc"), asset, presetStr, initSel);

        // Set output URL and file type
        ObjcMsgSendVoid(exportSession, SelRegisterName("setOutputURL:"), outputUrl);
        IntPtr mp4Type = MakeNSString("com.apple.m4v-video");
        ObjcMsgSendVoid(exportSession, SelRegisterName("setOutputFileType:"), mp4Type);

        var progressSel = SelRegisterName("progress");
        var statusSel   = SelRegisterName("status");

        // Kick off the async export with a real ObjC block completion handler.
        // BlockInvoke releases the semaphore when AVFoundation calls the block.
        using var done = new SemaphoreSlim(0, 1);
        ObjcTriggerExport(exportSession, done);

        // Poll progress while waiting for the completion block to fire.
        while (!ct.IsCancellationRequested)
        {
            bool completed = done.Wait(200, ct);
            float progress = ObjcGetFloat(exportSession, progressSel);
            _bun?.Push($"transcode:progress:{sessionId}", new { progress });
            if (completed) break;
        }

        if (File.Exists(outPath))
        {
            _bun?.Push($"transcode:ready:{sessionId}", new
            {
                url = $"/api/transcode-file?path={Uri.EscapeDataString(outPath)}"
            });
        }
        else
        {
            throw new Exception("AVFoundation export produced no output file");
        }
    }

    // ── ObjC block ABI (arm64) ───────────────────────────────────────────────
    //
    // An ObjC block on the wire is a heap pointer to:
    //   [isa: ptr][flags: i32][reserved: i32][invoke: ptr][descriptor: ptr][...captured vars]
    //
    // For a void(^)(void) completion handler with one captured IntPtr (GCHandle):
    //   invoke signature: void invoke(BlockLiteral* block)
    //
    // We build this struct on the heap, pass a GCHandle to the SemaphoreSlim as the
    // captured var, and use [UnmanagedCallersOnly] for the invoke function pointer.
    // The isa is _NSConcreteStackBlock (looked up from libobjc) — the runtime only uses
    // isa for retain/release/copy, none of which fire on a one-shot completion block.

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public ulong Reserved;  // 0
        public ulong Size;      // sizeof(BlockLiteral) in bytes
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int    Flags;
        public int    Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
        public IntPtr Context;  // GCHandle to SemaphoreSlim
    }

    // Static descriptor shared across all calls (immutable)
    private static readonly unsafe BlockDescriptor* s_blockDescriptor = AllocDescriptor();
    private static unsafe BlockDescriptor* AllocDescriptor()
    {
        var p = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        p->Reserved = 0;
        p->Size = (ulong)sizeof(BlockLiteral);
        return p;
    }

    // The actual block invoke function — called by AVFoundation when export finishes.
    // Extracts the GCHandle from the block's Context field and releases the semaphore.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void BlockInvoke(BlockLiteral* block)
    {
        var handle = GCHandle.FromIntPtr(block->Context);
        if (handle.Target is SemaphoreSlim sem)
            sem.Release();
        handle.Free();
    }

    private static unsafe void ObjcTriggerExport(IntPtr session, SemaphoreSlim completion)
    {
        IntPtr libobjc = NativeLibrary.Load("libobjc.dylib");
        IntPtr isa = NativeLibrary.GetExport(libobjc, "_NSConcreteStackBlock");

        var gcHandle = GCHandle.Alloc(completion);

        var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        block->Isa        = isa;
        block->Flags      = 0;
        block->Reserved   = 0;
        block->Invoke     = (IntPtr)(delegate* unmanaged[Cdecl]<BlockLiteral*, void>)&BlockInvoke;
        block->Descriptor = (IntPtr)s_blockDescriptor;
        block->Context    = GCHandle.ToIntPtr(gcHandle);

        var sel = SelRegisterName("exportAsynchronouslyWithCompletionHandler:");
        ObjcMsgSendVoid(session, sel, (IntPtr)block);

        // BlockInvoke frees the GCHandle when AVFoundation calls back.
        // The block allocation itself is freed here after the call returns —
        // AVFoundation does not retain the block beyond the call to exportAsynchronously.
        NativeMemory.Free(block);
    }

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcMsgSendTwoPtr(IntPtr receiver, IntPtr sel, IntPtr a1, IntPtr a2);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern float ObjcMsgSendFloat(IntPtr receiver, IntPtr sel);

    private static IntPtr ObjcMsgSendTwoArgs(IntPtr cls, IntPtr allocSel, IntPtr asset, IntPtr preset, IntPtr initSel)
    {
        IntPtr obj = ObjcMsgSend(cls, allocSel);
        return ObjcMsgSendTwoPtr(obj, initSel, asset, preset);
    }

    private static float ObjcGetFloat(IntPtr obj, IntPtr sel)
    {
        try { return ObjcMsgSendFloat(obj, sel); }
        catch { return 0f; }
    }

    // ── IServicePlugin lifecycle ─────────────────────────────────────────────

    public void Initialize()
    {
        Console.WriteLine("[HwTranscoder] AVFoundation transcoder ready");
    }

    public void Shutdown()
    {
        Console.WriteLine("[HwTranscoder] Shutdown");
    }
}

/// <summary>Simple RAII wrapper for unmanaged memory.</summary>
file sealed class SafeHGlobalHandle : IDisposable
{
    public IntPtr Handle { get; }
    public SafeHGlobalHandle(IntPtr h) => Handle = h;
    public void Dispose() => Marshal.FreeHGlobal(Handle);
}
