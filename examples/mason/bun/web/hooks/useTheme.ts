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
