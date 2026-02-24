// bridge.ts — React hook wrappers for the Keystone bridge SDK.

import { useEffect, useRef, useState } from "react";
import { keystone } from "@keystone/sdk/bridge";

/** Subscribe to a Keystone push channel. Cleans up on unmount. */
export function useSubscribe<T>(channel: string, cb: (data: T) => void): void {
  const cbRef = useRef(cb);
  cbRef.current = cb;

  useEffect(() => {
    const ks = keystone();
    return ks.subscribe(channel, (data: T) => cbRef.current(data));
  }, [channel]);
}

/** Subscribe and return the latest value pushed on a channel. */
export function useChannel<T>(channel: string, initial: T): T {
  const [value, setValue] = useState<T>(initial);
  useSubscribe<T>(channel, setValue);
  return value;
}

/** Listen for menu/action events matching a specific action string. */
export function useAction(action: string, cb: () => void): void {
  const cbRef = useRef(cb);
  cbRef.current = cb;

  useEffect(() => {
    const ks = keystone();
    return ks.onAction((incoming: string) => {
      if (incoming === action) cbRef.current();
    });
  }, [action]);
}

/** Listen for any action and pass it to the handler. */
export function useActions(cb: (action: string) => void): void {
  const cbRef = useRef(cb);
  cbRef.current = cb;

  useEffect(() => {
    const ks = keystone();
    return ks.onAction((a: string) => cbRef.current(a));
  }, []);
}
