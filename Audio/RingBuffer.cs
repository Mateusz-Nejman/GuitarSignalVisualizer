using System;
using System.Threading;

namespace GuitarSignalVisualiser.Audio;

/// <summary>
/// High-performance Single-Producer Single-Consumer (SPSC) Ring Buffer for audio samples.
/// Designed for zero-allocation audio streaming between the NAudio capture thread and the DSP processing thread.
/// </summary>
public sealed class RingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity;
    private readonly int _mask;
    private long _writePos;
    private long _readPos;

    public RingBuffer(int capacityPowerOfTwo = 16384)
    {
        // Ensure capacity is a power of 2 for fast bitwise masking
        int capacity = 1;
        while (capacity < capacityPowerOfTwo)
        {
            capacity <<= 1;
        }

        _capacity = capacity;
        _mask = capacity - 1;
        _buffer = new float[capacity];
    }

    public int Capacity => _capacity;

    public int AvailableToRead
    {
        get
        {
            long write = Volatile.Read(ref _writePos);
            long read = Volatile.Read(ref _readPos);
            return (int)(write - read);
        }
    }

    public int AvailableToWrite => _capacity - AvailableToRead;

    public int Write(ReadOnlySpan<float> data)
    {
        if (data.IsEmpty) return 0;

        long write = Volatile.Read(ref _writePos);
        long read = Volatile.Read(ref _readPos);
        int freeSpace = _capacity - (int)(write - read);

        int toWrite = Math.Min(data.Length, freeSpace);
        if (toWrite <= 0) return 0;

        int startIndex = (int)(write & _mask);
        int firstChunk = Math.Min(toWrite, _capacity - startIndex);
        data[..firstChunk].CopyTo(_buffer.AsSpan(startIndex, firstChunk));

        int secondChunk = toWrite - firstChunk;
        if (secondChunk > 0)
        {
            data.Slice(firstChunk, secondChunk).CopyTo(_buffer.AsSpan(0, secondChunk));
        }

        Volatile.Write(ref _writePos, write + toWrite);
        return toWrite;
    }

    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty) return 0;

        long write = Volatile.Read(ref _writePos);
        long read = Volatile.Read(ref _readPos);
        int available = (int)(write - read);

        int toRead = Math.Min(destination.Length, available);
        if (toRead <= 0) return 0;

        int startIndex = (int)(read & _mask);
        int firstChunk = Math.Min(toRead, _capacity - startIndex);
        _buffer.AsSpan(startIndex, firstChunk).CopyTo(destination[..firstChunk]);

        int secondChunk = toRead - firstChunk;
        if (secondChunk > 0)
        {
            _buffer.AsSpan(0, secondChunk).CopyTo(destination.Slice(firstChunk, secondChunk));
        }

        Volatile.Write(ref _readPos, read + toRead);
        return toRead;
    }

    /// <summary>
    /// Reads without advancing the read position (useful for overlapping DSP frames).
    /// </summary>
    public int Peek(Span<float> destination)
    {
        if (destination.IsEmpty) return 0;

        long write = Volatile.Read(ref _writePos);
        long read = Volatile.Read(ref _readPos);
        int available = (int)(write - read);

        int toRead = Math.Min(destination.Length, available);
        if (toRead <= 0) return 0;

        int startIndex = (int)(read & _mask);
        int firstChunk = Math.Min(toRead, _capacity - startIndex);
        _buffer.AsSpan(startIndex, firstChunk).CopyTo(destination[..firstChunk]);

        int secondChunk = toRead - firstChunk;
        if (secondChunk > 0)
        {
            _buffer.AsSpan(0, secondChunk).CopyTo(destination.Slice(firstChunk, secondChunk));
        }

        return toRead;
    }

    /// <summary>
    /// Discards specified number of samples from the buffer.
    /// </summary>
    public void Advance(int count)
    {
        long write = Volatile.Read(ref _writePos);
        long read = Volatile.Read(ref _readPos);
        int available = (int)(write - read);
        int toAdvance = Math.Min(count, available);
        Volatile.Write(ref _readPos, read + toAdvance);
    }

    public void Clear()
    {
        long write = Volatile.Read(ref _writePos);
        Volatile.Write(ref _readPos, write);
    }
}
