using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Keystone.Core.Platform;

public static class NativeLibraryLoader
{
    private static readonly List<string> _searchPaths = new();
    private static readonly HashSet<string> _loadedLibs = new();
    private static bool _initialized;

    /// <summary>
    /// Initialize native library resolution. Automatically searches the executable
    /// directory (MacOS/ in a bundle) for framework-shipped dylibs.
    /// Call AddSearchPath() to add app-specific native lib directories.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        // Framework native libs always live next to the executable
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null)
            _searchPaths.Add(exeDir);

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolver);
    }

    /// <summary>
    /// Add a directory to search for native dylibs. Searched in order after the executable directory.
    /// Use for app-specific native libraries (e.g. a Go strategy engine dylib).
    /// </summary>
    public static void AddSearchPath(string path)
    {
        if (!_searchPaths.Contains(path))
            _searchPaths.Add(path);
    }

    /// <summary>
    /// Resolve a native library by name against all registered search paths.
    /// Used by DyLibLoader as a fallback for plugin assemblies.
    /// </summary>
    public static IntPtr Resolve(string libraryName)
    {
        foreach (var dir in _searchPaths)
        {
            var libPath = Path.Combine(dir, libraryName + ".dylib");
            if (File.Exists(libPath))
            {
                if (_loadedLibs.Add(libraryName))
                    Console.WriteLine($"[NativeLib] Loaded: {libraryName} from {dir}");
                return NativeLibrary.Load(libPath);
            }
        }
        return IntPtr.Zero;
    }

    private static IntPtr Resolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        => Resolve(libraryName);
}
