/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { useEffect, useState } from "react";
import { keystone, type Theme } from "@keystone/sdk/bridge";

export type { Theme };

export function useTheme(): Theme {
  const [theme, setTheme] = useState<Theme>(() => keystone().theme);

  useEffect(() => {
    return keystone().onThemeChange(setTheme);
  }, []);

  return theme;
}
