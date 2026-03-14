/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// BinaryFrame — Length-prefixed binary framing for Keystone IPC.
// Wire format: [0x4B 0x53] [uint32-BE length] [payload]
// Magic bytes "KS" (0x4B53) identify Keystone binary frames vs NDJSON text.
// Payload is opaque at this layer — currently JSON bytes, future MessagePack.
// Used on binary WebSocket lanes and future stdin/stdout binary mode.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Keystone.Core.Management.Protocol;

/// <summary>
/// Reads and writes length-prefixed binary frames with "KS" magic header.
/// Thread-safe for concurrent writes via lock. Reader is single-threaded (one consumer per stream).
/// </summary>
public static class BinaryFrame
{
    /// <summary>Magic bytes identifying a Keystone binary frame.</summary>
    public const ushort Magic = 0x4B53; // "KS"

    /// <summary>Header size: 2 (magic) + 4 (length).</summary>
    public const int HeaderSize = 6;

    /// <summary>Maximum payload size: 64 MB.</summary>
    public const int MaxPayload = 64 * 1024 * 1024;

    // ── Write ─────────────────────────────────────────────────────────

    /// <summary>Encode an envelope as a binary frame (JSON payload).</summary>
    public static byte[] Encode(KeystoneEnvelope envelope)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope);
        return Encode(json);
    }

    /// <summary>Wrap raw payload bytes in a binary frame.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, Magic);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>Write a binary frame directly to a stream.</summary>
    public static void WriteTo(Stream stream, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt16BigEndian(header, Magic);
        BinaryPrimitives.WriteUInt32BigEndian(header[2..], (uint)payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    // ── Read ──────────────────────────────────────────────────────────

    /// <summary>
    /// Try to read one complete frame from a buffer. Returns the payload and
    /// advances consumed past the frame. Returns false if not enough data.
    /// </summary>
    public static bool TryRead(ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> payload, out long consumed)
    {
        payload = default;
        consumed = 0;

        if (buffer.Length < HeaderSize)
            return false;

        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);

        var magic = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (magic != Magic)
            throw new InvalidDataException($"Invalid frame magic: 0x{magic:X4}");

        var length = BinaryPrimitives.ReadUInt32BigEndian(header[2..]);
        if (length > MaxPayload)
            throw new InvalidDataException($"Frame payload too large: {length}");

        var totalLength = HeaderSize + (long)length;
        if (buffer.Length < totalLength)
            return false;

        var payloadBytes = new byte[length];
        buffer.Slice(HeaderSize, length).CopyTo(payloadBytes);
        payload = payloadBytes;
        consumed = totalLength;
        return true;
    }

    /// <summary>Check if a byte sequence starts with the KS magic bytes.</summary>
    public static bool IsBinaryFrame(ReadOnlySpan<byte> data)
        => data.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(data) == Magic;

    // ── Convenience ───────────────────────────────────────────────────

    /// <summary>Decode a frame payload as a KeystoneEnvelope (JSON).</summary>
    public static KeystoneEnvelope? DecodeEnvelope(ReadOnlySpan<byte> payload)
        => JsonSerializer.Deserialize<KeystoneEnvelope>(payload);

    /// <summary>Decode a frame payload as UTF-8 JSON string.</summary>
    public static string DecodeUtf8(ReadOnlySpan<byte> payload)
        => Encoding.UTF8.GetString(payload);
}
