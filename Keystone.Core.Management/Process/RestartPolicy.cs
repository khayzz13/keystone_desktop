/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace Keystone.Core.Management.Process;

public record RestartPolicy(
    int MaxAttempts = 5,
    int BaseDelayMs = 500,
    int MaxDelayMs = 30_000
);
