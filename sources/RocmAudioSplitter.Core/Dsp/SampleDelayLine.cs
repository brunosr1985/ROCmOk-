namespace RocmAudioSplitter.Core.Dsp;

/// <summary>
/// Linha de atraso lock-free com precisão de sample em memória pinada.
/// Mantém o áudio de monitoramento (fones) e o microfone perfeitamente sincronizados
/// com a latência de janela + inferência GPU do pipeline de separação.
/// </summary>
public sealed class SampleDelayLine
{
    private readonly float[] _buffer;
    private int _writeIndex;
    private int _delaySamples;

    public SampleDelayLine(int maxDelaySamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDelaySamples);
        _buffer = GC.AllocateArray<float>(maxDelaySamples, pinned: true);
    }

    public int DelaySamples => Volatile.Read(ref _delaySamples);

    public void SetDelaySamples(int delaySamples)
    {
        int clamped = Math.Clamp(delaySamples, 0, _buffer.Length - 1);
        Volatile.Write(ref _delaySamples, clamped);
    }

    public void ProcessBlock(ReadOnlySpan<float> input, Span<float> output)
    {
        int delay = Volatile.Read(ref _delaySamples);
        int capacity = _buffer.Length;
        int len = Math.Min(input.Length, output.Length);

        for (int i = 0; i < len; i++)
        {
            _buffer[_writeIndex] = input[i];
            int readIndex = _writeIndex - delay;
            if (readIndex < 0) readIndex += capacity;

            output[i] = _buffer[readIndex];

            _writeIndex++;
            if (_writeIndex == capacity) _writeIndex = 0;
        }
    }
}