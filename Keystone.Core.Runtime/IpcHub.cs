/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// IpcHub — Unified IPC facade implementation.
// Routes through existing singletons (BunManager, BunWorkerManager, ChannelManager).
// Stateless — thread safety inherited from underlying managers.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Keystone.Core;
using Keystone.Core.Management.Bun;
using Keystone.Core.Management.Protocol;
using Keystone.Core.Plugins;

namespace Keystone.Core.Runtime;

public sealed class IpcHub : IIpcFacade
{
    public IIpcBun Bun { get; } = new BunProxy();
    public IChannelManager Channels => ChannelManager.Instance;
    public IIpcWeb Web { get; } = new WebProxy();

    public IIpcWorker Worker(string name) => new WorkerProxy(name);

    public Task<string?> Call(IpcTarget target, string op, object? payload = null) => target.Plane switch
    {
        IpcPlane.Bun => BunManager.Instance.Query(op, payload),
        IpcPlane.Worker => BunWorkerManager.Instance[target.Name!]?.Query(op, payload)
                           ?? Task.FromResult<string?>(null),
        _ => Task.FromResult<string?>(null),
    };

    public void Action(IpcTarget target, string action)
    {
        switch (target.Plane)
        {
            case IpcPlane.Bun: BunManager.Instance.HandleAction(action); break;
            case IpcPlane.Worker: BunWorkerManager.Instance[target.Name!]?.HandleAction(action); break;
        }
    }

    // ── Bun proxy ─────────────────────────────────────────────────

    sealed class BunProxy : IIpcBun
    {
        private static int _nextStreamId = 1_000_000; // C# IDs start high to avoid collision with Bun IDs
        private readonly ConcurrentDictionary<string, Action<IStreamReader>> _streamHandlers = new();
        private readonly ConcurrentDictionary<int, ChannelStreamReader> _activeReaders = new();
        private volatile bool _envelopeWired;

        public Task<string?> Call(string service, object? args = null)
            => BunManager.Instance.Query(service, args);

        public void Push(string channel, object data)
            => BunManager.Instance.Push(channel, data);

        public void PushValue(string channel, object data)
        {
            BunManager.Instance.Send(JsonSerializer.Serialize(new
            {
                id = 0, type = "push_value", channel, data
            }));
        }

        public void Action(string action)
            => BunManager.Instance.HandleAction(action);

        public Task<string?> Eval(string code)
            => BunManager.Instance.Eval(code);

        public IStreamWriter OpenStream(string channel)
        {
            var id = Interlocked.Increment(ref _nextStreamId);
            EnsureEnvelopeWired();

            BunManager.Instance.SendEnvelope(new KeystoneEnvelope
            {
                Kind = EnvelopeKind.StreamOpen,
                StreamId = id,
                Op = channel,
                Source = "host",
                Target = "bun",
            });

            return new SocketStreamWriter(id);
        }

        public void OnStream(string channel, Action<IStreamReader> handler)
        {
            _streamHandlers[channel] = handler;
            EnsureEnvelopeWired();
        }

        private void EnsureEnvelopeWired()
        {
            if (_envelopeWired) return;
            _envelopeWired = true;
            BunManager.Instance.OnBinaryEnvelope = HandleEnvelope;
        }

        private void HandleEnvelope(KeystoneEnvelope env)
        {
            switch (env.Kind)
            {
                case EnvelopeKind.StreamOpen:
                {
                    var ch = env.Op ?? "";
                    if (_streamHandlers.TryGetValue(ch, out var handler))
                    {
                        var reader = new ChannelStreamReader(env.StreamId ?? 0, ch);
                        _activeReaders[reader.StreamId] = reader;
                        handler(reader);
                    }
                    else
                    {
                        BunManager.Instance.SendEnvelope(new KeystoneEnvelope
                        {
                            Kind = EnvelopeKind.StreamClose,
                            StreamId = env.StreamId,
                            Error = new EnvelopeError("handler_not_found", $"No stream handler for: {ch}"),
                        });
                    }
                    break;
                }

                case EnvelopeKind.StreamChunk:
                    if (env.StreamId is { } chunkId && _activeReaders.TryGetValue(chunkId, out var chunkReader))
                        chunkReader.Feed(env);
                    break;

                case EnvelopeKind.StreamClose:
                case EnvelopeKind.Cancel:
                    if (env.StreamId is { } closeId && _activeReaders.TryRemove(closeId, out var closeReader))
                        closeReader.Complete();
                    break;
            }
        }
    }

    // ── Stream implementations ───────────────────────────────────

    sealed class SocketStreamWriter(int streamId) : IStreamWriter
    {
        private const int SoftLimit = 4 * 1024 * 1024;
        private const int HardLimit = 16 * 1024 * 1024;
        private long _buffered;
        private volatile bool _closed;

        public int StreamId => streamId;
        public bool Backpressure => _buffered > SoftLimit;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (_closed) return;
            _buffered += data.Length;

            if (_buffered > HardLimit)
            {
                _closed = true;
                BunManager.Instance.SendEnvelope(new KeystoneEnvelope
                {
                    Kind = EnvelopeKind.StreamClose,
                    StreamId = streamId,
                    Error = new EnvelopeError("stream_backpressure", $"Buffer exceeded {HardLimit} bytes"),
                });
                return;
            }

            BunManager.Instance.SendEnvelope(new KeystoneEnvelope
            {
                Kind = EnvelopeKind.StreamChunk,
                StreamId = streamId,
                Encoding = "binary",
                Payload = Convert.ToBase64String(data),
            });
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            BunManager.Instance.SendEnvelope(new KeystoneEnvelope
            {
                Kind = EnvelopeKind.StreamClose,
                StreamId = streamId,
            });
        }

        public void Dispose() => Close();
    }

    sealed class ChannelStreamReader(int streamId, string channel) : IStreamReader
    {
        private readonly Channel<ReadOnlyMemory<byte>> _pipe =
            System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });

        public string Channel => channel;
        public int StreamId => streamId;

        public void Cancel()
        {
            _pipe.Writer.TryComplete();
            BunManager.Instance.SendEnvelope(new KeystoneEnvelope
            {
                Kind = EnvelopeKind.Cancel,
                StreamId = streamId,
            });
        }

        internal void Feed(KeystoneEnvelope env)
        {
            byte[] data;
            if (env.Encoding == "binary" && env.Payload is JsonElement el && el.ValueKind == JsonValueKind.String)
                data = Convert.FromBase64String(el.GetString()!);
            else if (env.Payload is JsonElement raw)
                data = System.Text.Encoding.UTF8.GetBytes(raw.GetRawText());
            else
                return;

            _pipe.Writer.TryWrite(data);
        }

        internal void Complete() => _pipe.Writer.TryComplete();

        public async IAsyncEnumerator<ReadOnlyMemory<byte>> GetAsyncEnumerator(CancellationToken ct = default)
        {
            await foreach (var chunk in _pipe.Reader.ReadAllAsync(ct))
                yield return chunk;
        }
    }

    // ── Worker proxy ──────────────────────────────────────────────

    sealed class WorkerProxy(string name) : IIpcWorker
    {
        public Task<string?> Call(string service, object? args = null)
            => BunWorkerManager.Instance[name]?.Query(service, args)
               ?? Task.FromResult<string?>(null);

        public void Push(string channel, object data)
            => BunWorkerManager.Instance[name]?.Push(channel, data);

        public void Action(string action)
            => BunWorkerManager.Instance[name]?.HandleAction(action);
    }

    // ── Web proxy ─────────────────────────────────────────────────

    sealed class WebProxy : IIpcWeb
    {
        public void Push(string channel, object data)
            => BunManager.Instance.Push(channel, data);

        public void PushValue(string channel, object data)
        {
            BunManager.Instance.Send(JsonSerializer.Serialize(new
            {
                id = 0, type = "push_value", channel, data
            }));
        }

        public void PushToWindow(string windowId, string channel, object data)
        {
            // Window-scoped push — Bun routes to the specific WebSocket connection
            BunManager.Instance.Send(JsonSerializer.Serialize(new
            {
                id = 0, type = "push", channel, data, windowId
            }));
        }
    }
}
