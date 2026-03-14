/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// DualModeReader — Reads a stream that may contain both NDJSON text lines
// and binary frames (identified by 0x4B53 magic). Used for subprocess pipes
// that upgrade from text-only to mixed mode.
//
// The reader peeks at the first bytes of each message:
// - 0x4B 0x53 → binary frame (read header + payload)
// - anything else → text line (read until \n)
//
// This allows gradual migration: existing NDJSON keeps working while new
// high-throughput paths (candle data, compute results) use binary framing.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;

namespace Keystone.Core.Management.Protocol;

/// <summary>
/// Message received from a dual-mode stream.
/// Exactly one of Text or Binary is set.
/// </summary>
public readonly struct DualMessage
{
    public readonly string? Text;
    public readonly ReadOnlyMemory<byte> Binary;
    public bool IsText => Text != null;
    public bool IsBinary => !IsText;

    public DualMessage(string text) { Text = text; Binary = default; }
    public DualMessage(ReadOnlyMemory<byte> binary) { Text = null; Binary = binary; }
}

/// <summary>
/// Reads from a Stream, dispatching text lines and binary frames to separate callbacks.
/// Designed for subprocess stdout where NDJSON and binary frames may be interleaved.
/// </summary>
public sealed class DualModeReader
{
    private readonly Stream _stream;
    private readonly Action<string> _onText;
    private readonly Action<ReadOnlyMemory<byte>> _onBinary;

    public DualModeReader(Stream stream, Action<string> onText, Action<ReadOnlyMemory<byte>> onBinary)
    {
        _stream = stream;
        _onText = onText;
        _onBinary = onBinary;
    }

    /// <summary>
    /// Run the read loop. Blocks until the stream ends or throws.
    /// Call from a background thread.
    /// </summary>
    public void Run()
    {
        var pipe = PipeReader.Create(_stream);

        try
        {
            while (true)
            {
                var readTask = pipe.ReadAsync();
                // Synchronous wait — this runs on a dedicated background thread
                var result = readTask.GetAwaiter().GetResult();
                var buffer = result.Buffer;

                Span<byte> peek = stackalloc byte[2];
                while (buffer.Length > 0)
                {
                    // Peek first two bytes to detect mode
                    if (buffer.Length >= 2)
                    {
                        buffer.Slice(0, 2).CopyTo(peek);

                        if (BinaryPrimitives.ReadUInt16BigEndian(peek) == BinaryFrame.Magic)
                        {
                            // Binary frame
                            if (BinaryFrame.TryRead(buffer, out var payload, out var consumed))
                            {
                                _onBinary(payload);
                                buffer = buffer.Slice(consumed);
                                continue;
                            }
                            break; // Need more data
                        }
                    }

                    // Text line — find \n
                    var pos = buffer.PositionOf((byte)'\n');
                    if (pos == null)
                        break; // Need more data

                    var lineSlice = buffer.Slice(0, pos.Value);
                    var line = Encoding.UTF8.GetString(lineSlice);
                    if (line.Length > 0 && line[^1] == '\r')
                        line = line[..^1];

                    if (line.Length > 0)
                        _onText(line);

                    buffer = buffer.Slice(buffer.GetPosition(1, pos.Value));
                }

                pipe.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DualModeReader] Error: {ex.Message}");
        }
        finally
        {
            pipe.Complete();
        }
    }

    /// <summary>Run the read loop on a new background thread.</summary>
    public Thread StartBackground(string? threadName = null)
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName ?? "DualModeReader"
        };
        thread.Start();
        return thread;
    }
}
