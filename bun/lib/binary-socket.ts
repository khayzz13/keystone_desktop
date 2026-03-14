/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// binary-socket.ts — Unix domain socket client for binary frame traffic.
// Connects to the C# host's binary socket (path from KEYSTONE_BINARY_SOCKET env).
// All binary/stream IPC goes through this socket — stdin/stdout stays pure NDJSON.

import type { KeystoneEnvelope } from "../types";
import { BINARY_FRAME_MAGIC, BINARY_FRAME_HEADER_SIZE } from "../types";
import { encodeFrame, tryReadFrame, decodeEnvelope } from "./binary-frame";

export class BinarySocket {
  private socket: ReturnType<typeof import("bun").connect> extends Promise<infer T> ? T : never;
  private connected = false;
  private buffer = new Uint8Array(0);

  onEnvelope: ((env: KeystoneEnvelope) => void) | null = null;
  onRawFrame: ((data: Uint8Array) => void) | null = null;
  onClose: (() => void) | null = null;

  get isConnected() { return this.connected; }

  async connect(socketPath: string): Promise<void> {
    const self = this;

    this.socket = await Bun.connect({
      unix: socketPath,
      socket: {
        open() {
          self.connected = true;
        },

        data(_socket, data: Buffer) {
          self.onData(new Uint8Array(data));
        },

        close() {
          self.connected = false;
          self.onClose?.();
        },

        error(_socket, err) {
          console.error(`[BinarySocket] Error: ${err.message}`);
        },
      },
    });
  }

  /** Send a KeystoneEnvelope as a KS-framed binary message. */
  send(envelope: KeystoneEnvelope): void {
    if (!this.connected) return;
    const json = new TextEncoder().encode(JSON.stringify(envelope));
    const frame = encodeFrame(json);
    this.socket.write(frame);
  }

  /** Send raw payload bytes wrapped in a KS binary frame. */
  sendRaw(payload: Uint8Array): void {
    if (!this.connected) return;
    const frame = encodeFrame(payload);
    this.socket.write(frame);
  }

  close(): void {
    this.connected = false;
    this.socket?.end();
  }

  private onData(data: Uint8Array<ArrayBufferLike>): void {
    // Accumulate into buffer
    if (this.buffer.length === 0) {
      this.buffer = new Uint8Array(data);
    } else {
      const merged = new Uint8Array(this.buffer.length + data.length);
      merged.set(this.buffer);
      merged.set(data, this.buffer.length);
      this.buffer = merged;
    }

    // Extract complete frames
    let offset = 0;
    while (offset < this.buffer.length) {
      const result = tryReadFrame(this.buffer, offset);
      if (!result.ok) break;

      this.dispatchFrame(result.payload);
      offset += result.consumed;
    }

    // Keep remainder
    if (offset > 0) {
      this.buffer = offset < this.buffer.length
        ? this.buffer.slice(offset)
        : new Uint8Array(0);
    }
  }

  private dispatchFrame(payload: Uint8Array): void {
    try {
      const envelope = decodeEnvelope(payload);
      this.onEnvelope?.(envelope);
    } catch {
      // Not valid JSON — dispatch as raw
      this.onRawFrame?.(payload);
    }
  }
}
