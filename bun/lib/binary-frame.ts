/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// binary-frame.ts — KS binary frame codec for Bun stdin/stdout.
// Wire format: [0x4B 0x53] [uint32-BE length] [payload bytes]
// Mirrors C# BinaryFrame.cs. Used by the dual-mode stdin reader
// to detect and extract binary frames interleaved with NDJSON text.

import { BINARY_FRAME_MAGIC, BINARY_FRAME_HEADER_SIZE } from "../types";
import type { KeystoneEnvelope } from "../types";

/** Encode a payload into a KS binary frame. */
export function encodeFrame(payload: Uint8Array): Uint8Array {
  const frame = new Uint8Array(BINARY_FRAME_HEADER_SIZE + payload.length);
  const view = new DataView(frame.buffer);
  view.setUint16(0, BINARY_FRAME_MAGIC, false); // big-endian
  view.setUint32(2, payload.length, false);
  frame.set(payload, BINARY_FRAME_HEADER_SIZE);
  return frame;
}

/** Encode a KeystoneEnvelope as a KS binary frame (JSON payload). */
export function encodeEnvelope(envelope: KeystoneEnvelope): Uint8Array {
  const json = new TextEncoder().encode(JSON.stringify(envelope));
  return encodeFrame(json);
}

/** Check if a buffer starts with the KS magic bytes. */
export function isBinaryFrame(data: Uint8Array, offset = 0): boolean {
  if (data.length - offset < 2) return false;
  return (data[offset] << 8 | data[offset + 1]) === BINARY_FRAME_MAGIC;
}

/** Result of trying to read a frame from a buffer. */
export type FrameReadResult =
  | { ok: true; payload: Uint8Array; consumed: number }
  | { ok: false; consumed: 0 };

/** Try to read one complete frame from a buffer at the given offset. */
export function tryReadFrame(data: Uint8Array, offset = 0): FrameReadResult {
  const remaining = data.length - offset;
  if (remaining < BINARY_FRAME_HEADER_SIZE) return { ok: false, consumed: 0 };

  const view = new DataView(data.buffer, data.byteOffset + offset, remaining);
  const magic = view.getUint16(0, false);
  if (magic !== BINARY_FRAME_MAGIC) return { ok: false, consumed: 0 };

  const length = view.getUint32(2, false);
  const totalLength = BINARY_FRAME_HEADER_SIZE + length;
  if (remaining < totalLength) return { ok: false, consumed: 0 };

  const payload = data.slice(offset + BINARY_FRAME_HEADER_SIZE, offset + totalLength);
  return { ok: true, payload, consumed: totalLength };
}

/** Decode a frame payload as a KeystoneEnvelope. */
export function decodeEnvelope(payload: Uint8Array): KeystoneEnvelope {
  return JSON.parse(new TextDecoder().decode(payload));
}

/** Write a KS binary frame to stdout (for Bun → C# binary communication). */
export function writeFrameToStdout(payload: Uint8Array): void {
  const frame = encodeFrame(payload);
  Bun.write(Bun.stdout, frame);
}

/** Write an envelope as a binary frame to stdout. */
export function writeEnvelopeToStdout(envelope: KeystoneEnvelope): void {
  writeFrameToStdout(new TextEncoder().encode(JSON.stringify(envelope)));
}
