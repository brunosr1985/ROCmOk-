using System.Runtime.CompilerServices;

namespace RocmAudioSplitter.Core.Dsp;

public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity;
    private int _writeIndex;
    private int _readIndex;

    public int Capacity => _capacity;

    public AudioRingBuffer(int capacitySamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacitySamples);
        _capacity = capacitySamples;
        _buffer = GC.AllocateArray<float>(capacitySamples, pinned: true);
    }

    public int AvailableToRead
    {
        get
        {
            int write = Volatile.Read(ref _writeIndex);
            int read = Volatile.Read(ref _readIndex);
            return write >= read ? write - read : _capacity - read + write;
        }
    }

    public int AvailableToWrite => _capacity - 1 - AvailableToRead;

    public int Write(ReadOnlySpan<float> source)
    {
        int writable = Math.Min(source.Length, AvailableToWrite);
        int write = Volatile.Read(ref _writeIndex);

        for (int i = 0; i < writable; i++)
        {
            _buffer[write] = source[i];
            write = NextIndex(write);
        }

        Volatile.Write(ref _writeIndex, write);
        return writable;
    }

    public int Read(Span<float> destination)
    {
        int readable = Math.Min(destination.Length, AvailableToRead);
        int read = Volatile.Read(ref _readIndex);

        for (int i = 0; i < readable; i++)
        {
            destination[i] = _buffer[read];
            read = NextIndex(read);
        }

        Volatile.Write(ref _readIndex, read);
        return readable;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NextIndex(int index)
    {
        int next = index + 1;
        return next == _capacity ? 0 : next;
    }
}