/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// HandlerRegistry — Ownership-tracked handler map.
// Replaces bare Map<string, T> for invoke/http handler registration.
// Conflicts between different owners fail fast at registration time.
// Hot-reload clears by owner before re-registering.

export class HandlerRegistry<T> {
  private handlers = new Map<string, { handler: T; owner: string }>();

  register(key: string, handler: T, owner: string): void {
    const existing = this.handlers.get(key);
    if (existing && existing.owner !== owner) {
      throw new Error(
        `Handler conflict: "${key}" owned by "${existing.owner}", cannot register from "${owner}"`
      );
    }
    this.handlers.set(key, { handler, owner });
  }

  get(key: string): T | undefined {
    return this.handlers.get(key)?.handler;
  }

  has(key: string): boolean {
    return this.handlers.has(key);
  }

  removeByOwner(owner: string): void {
    for (const [key, entry] of this.handlers) {
      if (entry.owner === owner) this.handlers.delete(key);
    }
  }

  delete(key: string): boolean {
    return this.handlers.delete(key);
  }

  keys(): IterableIterator<string> {
    return this.handlers.keys();
  }

  entries(): IterableIterator<[string, { handler: T; owner: string }]> {
    return this.handlers.entries();
  }

  clear(): void {
    this.handlers.clear();
  }

  get size(): number {
    return this.handlers.size;
  }
}
