/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// BinarySocketServer — Unix domain socket listener for binary frame traffic.
// Dedicated channel for stream/binary data between C# host and Bun subprocess.
// stdin/stdout stays pure NDJSON — this socket carries only KS-framed envelopes.
//
// Lifecycle: created before Bun starts → socket path passed via KEYSTONE_BINARY_SOCKET
// env var → Bun connects on startup → read loop dispatches envelopes → cleaned up on dispose.

using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text.Json;

namespace Keystone.Core.Management.Protocol;

public sealed class BinarySocketServer : IDisposable
{
    private readonly string _socketPath;
    private Socket? _listener;
    private Socket? _client;
    private readonly object _writeLock = new();
    private Thread? _acceptThread;
    private Thread? _readerThread;
    private volatile bool _disposed;

    /// <summary>Called when a complete envelope is received from Bun.</summary>
    public Action<KeystoneEnvelope>? OnEnvelope { get; set; }

    /// <summary>Called when a raw binary frame payload is received (non-envelope).</summary>
    public Action<ReadOnlyMemory<byte>>? OnRawFrame { get; set; }

    /// <summary>Called when the client disconnects.</summary>
    public Action? OnDisconnect { get; set; }

    public bool IsConnected => _client is { Connected: true };
    public string SocketPath => _socketPath;

    public BinarySocketServer(string socketPath)
    {
        _socketPath = socketPath;
    }

    /// <summary>Generate a unique socket path in $TMPDIR for this process.</summary>
    public static string CreateSocketPath()
    {
        var tmpDir = Environment.GetEnvironmentVariable("TMPDIR")
                     ?? Environment.GetEnvironmentVariable("TMP")
                     ?? "/tmp";
        return Path.Combine(tmpDir, $"keystone-{Environment.ProcessId}-bin.sock");
    }

    /// <summary>Bind and listen. Accepts one client on a background thread.</summary>
    public void Start()
    {
        // Clean up stale socket file
        if (File.Exists(_socketPath))
            File.Delete(_socketPath);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(1);

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "BinarySocket-Accept" };
        _acceptThread.Start();
    }

    /// <summary>Send a KeystoneEnvelope as a KS-framed binary frame.</summary>
    public void SendEnvelope(KeystoneEnvelope envelope)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope);
        SendFrame(json);
    }

    /// <summary>Send raw payload bytes wrapped in a KS binary frame.</summary>
    public void SendFrame(ReadOnlySpan<byte> payload)
    {
        lock (_writeLock)
        {
            if (_client is not { Connected: true }) return;
            var frame = BinaryFrame.Encode(payload);
            _client.Send(frame);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _client?.Shutdown(SocketShutdown.Both); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Close(); } catch { }
        try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
    }

    private void AcceptLoop()
    {
        try
        {
            while (!_disposed)
            {
                var client = _listener!.Accept();
                _client?.Close();
                _client = client;

                _readerThread = new Thread(() => ReadLoop(client))
                {
                    IsBackground = true,
                    Name = "BinarySocket-Reader"
                };
                _readerThread.Start();
            }
        }
        catch (SocketException) when (_disposed) { }
        catch (ObjectDisposedException) { }
    }

    private void ReadLoop(Socket client)
    {
        var stream = new NetworkStream(client, ownsSocket: false);
        var pipe = PipeReader.Create(stream);

        try
        {
            while (!_disposed && client.Connected)
            {
                var readTask = pipe.ReadAsync();
                var result = readTask.GetAwaiter().GetResult();
                var buffer = result.Buffer;

                while (buffer.Length > 0)
                {
                    if (!BinaryFrame.TryRead(buffer, out var payload, out var consumed))
                        break;

                    DispatchFrame(payload);
                    buffer = buffer.Slice(consumed);
                }

                pipe.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (SocketException) when (_disposed) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (!_disposed)
                Console.Error.WriteLine($"[BinarySocket] Read error: {ex.Message}");
        }
        finally
        {
            pipe.Complete();
            OnDisconnect?.Invoke();
        }
    }

    private void DispatchFrame(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<KeystoneEnvelope>(payload.Span);
            if (envelope != null)
            {
                OnEnvelope?.Invoke(envelope);
                return;
            }
        }
        catch { }

        // Not valid JSON envelope — dispatch as raw frame
        OnRawFrame?.Invoke(payload);
    }
}
