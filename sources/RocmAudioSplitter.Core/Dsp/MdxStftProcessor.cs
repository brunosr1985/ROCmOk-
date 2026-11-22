using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace RocmAudioSplitter.Core.Dsp;

/// <summary>
/// Converts stereo time-domain PCM chunks into 4-channel complex STFT spectrograms
/// [1, 4, dim_f, dim_t] expected by UVR MDX-Net ONNX models and reconstructs PCM via iSTFT.
/// </summary>
public sealed class MdxStftProcessor
{
    public int NFft { get; }
    public int HopLength { get; }
    public int DimF { get; }
    public int DimT { get; }
    public int ChunkSamplesPerChannel => (DimT - 1) * HopLength;

    private readonly float[] _window;

    public MdxStftProcessor(int dimF, int dimT, int hopLength = 1024)
    {
        DimF = dimF;
        DimT = dimT;
        NFft = dimF * 2; // UVR MDX-Net crops Nyquist bin, keeping bins [0 .. NFft/2 - 1]
        HopLength = hopLength;
        _window = CreatePeriodicHannWindow(NFft);
    }

    public DenseTensor<float> ForwardStft(ReadOnlySpan<float> leftChannel, ReadOnlySpan<float> rightChannel)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 4, DimF, DimT });
        int pad = NFft / 2;

        Complex32[] fftL = new Complex32[NFft];
        Complex32[] fftR = new Complex32[NFft];

        for (int t = 0; t < DimT; t++)
        {
            int center = t * HopLength;
            for (int n = 0; n < NFft; n++)
            {
                int sampleIdx = center - pad + n;
                float sampleL = GetReflectPaddedSample(leftChannel, sampleIdx) * _window[n];
                float sampleR = GetReflectPaddedSample(rightChannel, sampleIdx) * _window[n];

                fftL[n] = new Complex32(sampleL, 0f);
                fftR[n] = new Complex32(sampleR, 0f);
            }

            Fourier.Forward(fftL, FourierOptions.NoScaling);
            Fourier.Forward(fftR, FourierOptions.NoScaling);

            for (int f = 0; f < DimF; f++)
            {
                tensor[0, 0, f, t] = fftL[f].Real;
                tensor[0, 1, f, t] = fftL[f].Imaginary;
                tensor[0, 2, f, t] = fftR[f].Real;
                tensor[0, 3, f, t] = fftR[f].Imaginary;
            }
        }

        return tensor;
    }

    public void InverseStft(Tensor<float> specTensor, Span<float> outLeft, Span<float> outRight)
    {
        int outLen = outLeft.Length;
        int pad = NFft / 2;
        float[] windowNorm = new float[outLen];

        Complex32[] ifftL = new Complex32[NFft];
        Complex32[] ifftR = new Complex32[NFft];

        for (int t = 0; t < DimT; t++)
        {
            Array.Clear(ifftL);
            Array.Clear(ifftR);

            for (int f = 0; f < DimF; f++)
            {
                float lRe = specTensor[0, 0, f, t];
                float lIm = specTensor[0, 1, f, t];
                float rRe = specTensor[0, 2, f, t];
                float rIm = specTensor[0, 3, f, t];

                ifftL[f] = new Complex32(lRe, lIm);
                ifftR[f] = new Complex32(rRe, rIm);

                // Hermitian symmetry for real-valued iFFT reconstruction
                if (f > 0 && f < NFft / 2)
                {
                    ifftL[NFft - f] = new Complex32(lRe, -lIm);
                    ifftR[NFft - f] = new Complex32(rRe, -rIm);
                }
            }

            Fourier.Inverse(ifftL, FourierOptions.AsymmetricScaling);
            Fourier.Inverse(ifftR, FourierOptions.AsymmetricScaling);

            int center = t * HopLength;
            for (int n = 0; n < NFft; n++)
            {
                int dstIdx = center - pad + n;
                if ((uint)dstIdx < (uint)outLen)
                {
                    float w = _window[n];
                    outLeft[dstIdx] += ifftL[n].Real * w;
                    outRight[dstIdx] += ifftR[n].Real * w;
                    windowNorm[dstIdx] += w * w;
                }
            }
        }

        for (int i = 0; i < outLen; i++)
        {
            float norm = windowNorm[i] > 1e-8f ? windowNorm[i] : 1f;
            outLeft[i] /= norm;
            outRight[i] /= norm;
        }
    }

    private static float GetReflectPaddedSample(ReadOnlySpan<float> signal, int index)
    {
        if (signal.Length <= 1) return 0f;
        while (index < 0 || index >= signal.Length)
        {
            if (index < 0) index = -index;
            if (index >= signal.Length) index = 2 * signal.Length - 2 - index;
        }
        return signal[index];
    }

    private static float[] CreatePeriodicHannWindow(int length)
    {
        float[] window = new float[length];
        for (int i = 0; i < length; i++)
        {
            window[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / length));
        }
        return window;
    }
}