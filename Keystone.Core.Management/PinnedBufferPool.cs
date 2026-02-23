using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Keystone.Core.Management;

/// <summary>
/// Pattern 2: C# Allocates, Rust Borrows
/// Pooled pinned buffers for passing to native code (command buffers, etc.)
/// </summary>
public class PinnedBufferPool : IDisposable
{
    private readonly ConcurrentBag<PinnedBuffer> _pool = new();
    private readonly int _bufferSize;
    private bool _disposed;

    public PinnedBufferPool(int bufferSize = 4 * 1024 * 1024) // 4MB default
    {
        _bufferSize = bufferSize;
    }

    public PinnedBuffer Rent()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PinnedBufferPool));

        if (_pool.TryTake(out var buffer))
            return buffer;

        return new PinnedBuffer(_bufferSize);
    }

    public void Return(PinnedBuffer buffer)
    {
        if (_disposed || buffer == null)
            return;

        buffer.Clear();
        _pool.Add(buffer);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        while (_pool.TryTake(out var buffer))
            buffer.Dispose();

        _disposed = true;
    }
}

/// <summary>
/// Pinned byte buffer that can be passed to native code
/// </summary>
public class PinnedBuffer : IDisposable
{
    private byte[] _buffer;
    private GCHandle _handle;
    private bool _disposed;

    public PinnedBuffer(int size)
    {
        _buffer = new byte[size];
        _handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
    }

    public IntPtr Pointer => _handle.AddrOfPinnedObject();
    public int Size => _buffer.Length;

    public Span<byte> AsSpan() => _buffer.AsSpan();
    public Span<byte> AsSpan(int length) => _buffer.AsSpan(0, length);

    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_handle.IsAllocated)
            _handle.Free();

        _disposed = true;
    }

    ~PinnedBuffer()
    {
        Dispose();
    }
}
