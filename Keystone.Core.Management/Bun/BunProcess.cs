/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// BunProcess — Bun-specific subprocess wrapper.
// Adds compiled-exe detection and appRoot argument handling on top of ManagedProcess.

using Keystone.Core.Management.Process;

namespace Keystone.Core.Management.Bun;

public class BunProcess : ManagedProcess
{
    public BunProcess() : base("Bun") { }

    public bool Start(string entryPoint, string? appRoot = null, string? compiledExe = null,
        Dictionary<string, string>? env = null, string? binarySocketPath = null)
    {
        if (binarySocketPath != null)
        {
            env ??= new();
            env["KEYSTONE_BINARY_SOCKET"] = binarySocketPath;
        }

        var workingDir = Path.GetDirectoryName(entryPoint) ?? "";

        string exe;
        string[] args;

        if (compiledExe != null && File.Exists(compiledExe))
        {
            exe = compiledExe;
            args = appRoot != null ? [appRoot] : [];
        }
        else
        {
            exe = "bun";
            args = appRoot != null
                ? ["run", entryPoint, appRoot]
                : ["run", entryPoint];
        }

        return base.Start(exe, args, workingDir, env);
    }
}
