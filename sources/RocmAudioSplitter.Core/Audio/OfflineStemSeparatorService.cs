using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RocmAudioSplitter.Core.Inference;

namespace RocmAudioSplitter.Core.Audio;

public sealed class OfflineStemSeparatorService
{
    private readonly IInferenceEngine _inferenceEngine;

    public OfflineStemSeparatorService(IInferenceEngine inferenceEngine)
    {
        _inferenceEngine = inferenceEngine;
    }

    public async Task<SeparationBenchmark> ProcessFileAsync(
        string inputAudioPath,
        string outputDirectory,
        int targetSampleRate = 44100,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        string baseName = Path.GetFileNameWithoutExtension(inputAudioPath);

        float[] interleavedPcm = DecodeAndResampleToStereoFloat(inputAudioPath, targetSampleRate);

        SeparationResult result = await _inferenceEngine.SeparateAsync(
            interleavedPcm,
            targetSampleRate,
            cancellationToken);

        string instrumentalPath = Path.Combine(outputDirectory, $"{baseName}_instrumental.wav");
        string vocalsPath = Path.Combine(outputDirectory, $"{baseName}_vocals.wav");

        WriteWav24Bit(instrumentalPath, result.InstrumentalInterleaved, targetSampleRate, 2);
        WriteWav24Bit(vocalsPath, result.VocalsInterleaved, targetSampleRate, 2);

        return result.Benchmark;
    }

    private static float[] DecodeAndResampleToStereoFloat(string filePath, int targetSampleRate)
    {
        using var reader = new AudioFileReader(filePath);
        ISampleProvider provider = reader;

        if (provider.WaveFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != targetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
        }

        var samples = new List<float>((int)(reader.Length / sizeof(float)));
        float[] readBuffer = new float[targetSampleRate * 2];
        int read;

        while ((read = provider.Read(readBuffer.AsSpan())) > 0)
        {
            samples.AddRange(readBuffer.AsSpan(0, read));
        }

        return samples.ToArray();
    }

    private static void WriteWav24Bit(string outputPath, float[] interleavedSamples, int sampleRate, int channels)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        using var writer = new WaveFileWriter(outputPath, format);
        writer.WriteSamples(interleavedSamples, 0, interleavedSamples.Length);
    }
}