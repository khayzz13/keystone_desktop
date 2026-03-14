/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using Keystone.Core;
using Keystone.Core.Plugins;

namespace {{APP_NAMESPACE}};

public class AppCore : ICorePlugin
{
    public string CoreName => "{{APP_NAME}}";

    public void Initialize(ICoreContext context)
    {
        Console.WriteLine("[{{APP_NAME}}] Initializing...");

        context.OnUnhandledAction = (action, source) =>
        {
            Console.WriteLine($"[{{APP_NAME}}] Action: {action} from {source}");
        };

        Console.WriteLine("[{{APP_NAME}}] Ready");
    }
}
