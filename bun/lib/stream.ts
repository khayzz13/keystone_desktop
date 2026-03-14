/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// stream.ts — Stream lane registry for Keystone Bun runtime.
// Manages stream lifecycle: open → chunk → close.
// Transport-agnostic: caller injects a send callback (Unix socket or stdout).

import type { KeystoneEnvelope } from "../types";

// ── Types ────────────────────────────────────────────────────────

export type StreamSource = "host" | "web" | "worker" | "service";

export type StreamEntry = {
  id: number;
  channel: string;
  source: StreamSource;
  onChunk?: (data: Uint8Array) => void;
  onClose?: () => void;
  bufferedBytes: number;
  closed: boolean;
};

export interface StreamWriter {
  /** Write binary data to the stream. */
  write(data: ArrayBuffer | Uint8Array): void;
  /** Close the stream gracefully. */
  close(): void;
  /** True when the receiver is falling behind. Callers should slow down. */
  readonly backpressure: boolean;
  /** The stream ID. */
  readonly streamId: number;
}

export interface StreamReader {
  /** Async iterate over incoming binary chunks. */
  [Symbol.asyncIterator](): AsyncIterableIterator<Uint8Array>;
  /** Cancel the stream from the receiver side. */
  cancel(): void;
  /** The channel this stream was opened on. */
  readonly channel: string;
  /** The stream ID. */
  readonly streamId: number;
}

// ── Backpressure constants ───────────────────────────────────────

const SOFT_LIMIT = 4 * 1024 * 1024;  // 4 MB — backpressure flag
const HARD_LIMIT = 16 * 1024 * 1024; // 16 MB — force close

// ── Registry ─────────────────────────────────────────────────────

export type EnvelopeSender = (envelope: KeystoneEnvelope) => void;

export class StreamRegistry {
  private streams = new Map<number, StreamEntry>();
  private nextId = 1;
  private streamHandlers = new Map<string, (reader: StreamReader) => void>();
  private send: EnvelopeSender;

  constructor(send: EnvelopeSender) {
    this.send = send;
  }

  /** Register a handler for incoming streams on a channel. */
  onStream(channel: string, handler: (reader: StreamReader) => void): void {
    this.streamHandlers.set(channel, handler);
  }

  /** Create a new outbound stream. Returns a writer. */
  open(channel: string, target: "host" | "web"): StreamWriter {
    const id = this.nextId++;
    const entry: StreamEntry = {
      id, channel, source: "service", bufferedBytes: 0, closed: false,
    };
    this.streams.set(id, entry);
    const send = this.send;
    const streamsRef = this.streams;

    if (target === "host") {
      send({ v: 1, kind: "stream_open", streamId: id, op: channel, source: "bun", target: "host" });
    }

    const writer: StreamWriter = {
      get streamId() { return id; },
      get backpressure() { return entry.bufferedBytes > SOFT_LIMIT; },

      write(data: ArrayBuffer | Uint8Array) {
        if (entry.closed) return;
        const bytes = data instanceof Uint8Array ? data : new Uint8Array(data);
        entry.bufferedBytes += bytes.length;

        if (entry.bufferedBytes > HARD_LIMIT) {
          entry.closed = true;
          send({
            v: 1, kind: "stream_close", streamId: id,
            error: { code: "stream_backpressure", message: `Buffer exceeded ${HARD_LIMIT} bytes` },
          });
          streamsRef.delete(id);
          return;
        }

        if (target === "host") {
          send({
            v: 1, kind: "stream_chunk", streamId: id, encoding: "binary",
            payload: Buffer.from(bytes).toString("base64"),
          });
        }
      },

      close() {
        if (entry.closed) return;
        entry.closed = true;
        if (target === "host") send({ v: 1, kind: "stream_close", streamId: id });
        streamsRef.delete(id);
      },
    };

    return writer;
  }

  /** Handle an incoming stream_open from the host or browser. */
  handleOpen(envelope: KeystoneEnvelope): void {
    const id = envelope.streamId!;
    const channel = envelope.op!;
    const source = (envelope.source as StreamSource) ?? "host";

    const entry: StreamEntry = {
      id, channel, source, bufferedBytes: 0, closed: false,
    };
    this.streams.set(id, entry);

    const handler = this.streamHandlers.get(channel);
    if (!handler) {
      this.send({
        v: 1, kind: "stream_close", streamId: id,
        error: { code: "handler_not_found", message: `No stream handler for channel: ${channel}` },
      });
      this.streams.delete(id);
      return;
    }

    const chunks: Uint8Array[] = [];
    let resolve: ((value: IteratorResult<Uint8Array>) => void) | null = null;
    let done = false;
    const send = this.send;

    entry.onChunk = (data: Uint8Array) => {
      entry.bufferedBytes += data.length;
      if (resolve) {
        const r = resolve;
        resolve = null;
        entry.bufferedBytes -= data.length;
        r({ value: data, done: false });
      } else {
        chunks.push(data);
      }
    };

    entry.onClose = () => {
      done = true;
      if (resolve) {
        const r = resolve;
        resolve = null;
        r({ value: undefined as any, done: true });
      }
    };

    const reader: StreamReader = {
      channel,
      streamId: id,

      cancel() {
        if (entry.closed) return;
        entry.closed = true;
        done = true;
        send({ v: 1, kind: "cancel", streamId: id });
        if (resolve) {
          const r = resolve;
          resolve = null;
          r({ value: undefined as any, done: true });
        }
      },

      [Symbol.asyncIterator](): AsyncIterableIterator<Uint8Array> {
        const iter: AsyncIterableIterator<Uint8Array> = {
          next(): Promise<IteratorResult<Uint8Array>> {
            if (chunks.length > 0) {
              const chunk = chunks.shift()!;
              entry.bufferedBytes -= chunk.length;
              return Promise.resolve({ value: chunk, done: false });
            }
            if (done) return Promise.resolve({ value: undefined as any, done: true });
            return new Promise(r => { resolve = r; });
          },
          [Symbol.asyncIterator]() { return iter; },
        };
        return iter;
      },
    };

    handler(reader);
  }

  /** Route an incoming stream_chunk to the right stream. */
  handleChunk(envelope: KeystoneEnvelope): void {
    const entry = this.streams.get(envelope.streamId!);
    if (!entry || entry.closed) return;

    let data: Uint8Array;
    if (envelope.encoding === "binary" && typeof envelope.payload === "string") {
      data = Buffer.from(envelope.payload as string, "base64");
    } else if (envelope.payload instanceof Uint8Array) {
      data = envelope.payload as Uint8Array;
    } else {
      data = new TextEncoder().encode(JSON.stringify(envelope.payload));
    }

    entry.onChunk?.(data);
  }

  /** Handle stream_close or cancel. */
  handleClose(envelope: KeystoneEnvelope): void {
    const entry = this.streams.get(envelope.streamId!);
    if (!entry) return;
    entry.closed = true;
    entry.onClose?.();
    this.streams.delete(envelope.streamId!);
  }

  /** Get a stream entry by ID. */
  get(streamId: number): StreamEntry | undefined {
    return this.streams.get(streamId);
  }

  /** Number of active streams. */
  get size(): number {
    return this.streams.size;
  }
}
