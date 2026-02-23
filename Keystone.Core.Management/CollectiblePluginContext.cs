// CollectiblePluginContext - Wrapper for collectible AssemblyLoadContext enabling true hot-reload

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Keystone.Core.Management;

public class CollectiblePluginContext : IDisposable
{
    private readonly AssemblyLoadContext _context;
    private readonly string _assemblyPath;
    private WeakReference<Assembly>? _assemblyRef;
    private bool _disposed;

    public string Name { get; }
    public bool IsLoaded => _assemblyRef?.TryGetTarget(out _) ?? false;
    public AssemblyLoadContext Context => _context;

    public CollectiblePluginContext(string name, string assemblyPath)
    {
        Name = name;
        _assemblyPath = assemblyPath;
        _context = new AssemblyLoadContext(name, isCollectible: true);
        _context.Resolving += OnResolving;
    }

    [RequiresUnreferencedCode("Plugin loading requires dynamic assembly loading")]
    public Assembly Load()
    {
        using var fs = File.OpenRead(_assemblyPath);
        var asm = _context.LoadFromStream(fs);
        _assemblyRef = new WeakReference<Assembly>(asm);
        return asm;
    }

    public void Unload()
    {
        if (_disposed) return;
        _context.Unload();
        _assemblyRef = null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Plugin system requires dynamic loading")]
    private Assembly? OnResolving(AssemblyLoadContext ctx, AssemblyName name)
    {
        // Share Keystone.Core.* from default context
        if (name.Name?.StartsWith("Keystone.Core") == true)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == name.Name) return asm;
        }

        // Probe dylib directory for dependencies
        var dir = Path.GetDirectoryName(_assemblyPath);
        if (dir != null && name.Name != null)
        {
            var depPath = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(depPath))
            {
                using var fs = File.OpenRead(depPath);
                return _context.LoadFromStream(fs);
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
    }
}
