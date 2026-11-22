using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RocmAudioSplitter.Core.Audio;

/// <summary>
/// Captura exclusiva do áudio de um processo (e toda a sua árvore de subprocessos)
/// no Windows 10 (build 20348+) / Windows 11 via ActivateAudioInterfaceAsync
/// com VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly int _targetProcessId;
    private readonly bool _includeProcessTree;
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private Thread? _captureThread;
    private CancellationTokenSource? _cts;

    public WaveFormat WaveFormat { get; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public ProcessLoopbackCapture(int targetProcessId, bool includeProcessTree = true)
    {
        _targetProcessId = targetProcessId;
        _includeProcessTree = includeProcessTree;
        // O endpoint virtual de Process Loopback opera em PCM Float32 48kHz Stereo por padrão
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    }

    public void StartRecording()
    {
        if (_captureThread is not null) return;

        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = (uint)_targetProcessId,
                ProcessLoopbackMode = _includeProcessTree
                    ? PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                    : PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
            }
        };

        int structSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        nint paramsPtr = Marshal.AllocHGlobal(structSize);

        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            var propVariant = new PROPVARIANT
            {
                vt = 65, // VT_BLOB
                blobSize = (uint)structSize,
                blobData = paramsPtr
            };

            nint propVarPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT>());
            try
            {
                Marshal.StructureToPtr(propVariant, propVarPtr, false);

                var handler = new ActivateCompletionHandler();
                Guid iid = IID_IAudioClient;
                int hr = ActivateAudioInterfaceAsync(
                    VirtualAudioDeviceProcessLoopback,
                    ref iid,
                    propVarPtr,
                    handler,
                    out _);

                Marshal.ThrowExceptionForHR(hr);

                object activatedInterface = handler.WaitForInterface(TimeSpan.FromSeconds(5));
                _audioClient = (IAudioClient)activatedInterface;

                // Inicializa IAudioClient em modo Shared + Loopback (0x00020000) + EventCallback (0x00040000)
                nint wfxPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());
                try
                {
                    var wfx = CreateFloatStereo48kFormat();
                    Marshal.StructureToPtr(wfx, wfxPtr, false);

                    const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
                    const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
                    long hundredNsBuffer = 200_000; // 20ms

                    int initHr = _audioClient.Initialize(
                        AudioClientShareMode.Shared,
                        AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                        hundredNsBuffer,
                        0,
                        wfxPtr,
                        nint.Zero);

                    Marshal.ThrowExceptionForHR(initHr);
                }
                finally
                {
                    Marshal.FreeHGlobal(wfxPtr);
                }

                using var readyEvent = new AutoResetEvent(false);
                Marshal.ThrowExceptionForHR(_audioClient.SetEventHandle(readyEvent.SafeWaitHandle.DangerousGetHandle()));

                Guid captureIid = IID_IAudioCaptureClient;
                Marshal.ThrowExceptionForHR(_audioClient.GetService(ref captureIid, out object captureObj));
                _captureClient = (IAudioCaptureClient)captureObj;

                Marshal.ThrowExceptionForHR(_audioClient.Start());

                _cts = new CancellationTokenSource();
                _captureThread = new Thread(() => CaptureLoop(_cts.Token))
                {
                    Name = $"WASAPI-ProcessLoopback-PID-{_targetProcessId}",
                    IsBackground = true,
                    Priority = ThreadPriority.Highest
                };
                _captureThread.Start();
            }
            finally
            {
                Marshal.FreeHGlobal(propVarPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private void CaptureLoop(CancellationToken token)
    {
        int bytesPerFrame = WaveFormat.BlockAlign; // 8 bytes (2ch * float32)
        byte[] managedBuffer = new byte[48000 * bytesPerFrame];

        while (!token.IsCancellationRequested && _captureClient is not null)
        {
            int hr = _captureClient.GetNextPacketSize(out uint packetFrames);
            if (hr < 0 || packetFrames == 0)
            {
                Thread.Sleep(2);
                continue;
            }

            while (packetFrames > 0 && !token.IsCancellationRequested)
            {
                hr = _captureClient.GetBuffer(
                    out nint dataPtr,
                    out uint numFramesRead,
                    out uint flags,
                    out _,
                    out _);

                if (hr < 0) break;

                try
                {
                    int byteCount = (int)numFramesRead * bytesPerFrame;
                    if (byteCount > managedBuffer.Length)
                    {
                        managedBuffer = new byte[byteCount];
                    }

                    const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || dataPtr == nint.Zero)
                    {
                        Array.Clear(managedBuffer, 0, byteCount);
                    }
                    else
                    {
                        Marshal.Copy(dataPtr, managedBuffer, 0, byteCount);
                    }

                    DataAvailable?.Invoke(this, new WaveInEventArgs(managedBuffer, byteCount));
                }
                finally
                {
                    _captureClient.ReleaseBuffer(numFramesRead);
                }

                _captureClient.GetNextPacketSize(out packetFrames);
            }
        }
    }

    public void StopRecording()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _captureThread?.Join(500);
            _cts.Dispose();
            _cts = null;
            _captureThread = null;
        }

        _audioClient?.Stop();
    }

    public void Dispose()
    {
        StopRecording();
        if (_captureClient is not null)
        {
            Marshal.ReleaseComObject(_captureClient);
            _captureClient = null;
        }
        if (_audioClient is not null)
        {
            Marshal.ReleaseComObject(_audioClient);
            _audioClient = null;
        }
    }

    private static WAVEFORMATEXTENSIBLE CreateFloatStereo48kFormat()
    {
        return new WAVEFORMATEXTENSIBLE
        {
            wFormatTag = 0xFFFE, // WAVE_FORMAT_EXTENSIBLE
            nChannels = 2,
            nSamplesPerSec = 48000,
            nAvgBytesPerSec = 48000 * 8,
            nBlockAlign = 8,
            wBitsPerSample = 32,
            cbSize = 22,
            wValidBitsPerSample = 32,
            dwChannelMask = 0x3, // SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT
            SubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71") // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        };
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        nint activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    private enum AUDIOCLIENT_ACTIVATION_TYPE
    {
        AUDIOCLIENT_ACTIVATION_TYPE_DEFAULT = 0,
        AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
    }

    private enum PROCESS_LOOPBACK_MODE
    {
        PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0,
        PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        [FieldOffset(0)] public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        [FieldOffset(4)] public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint blobSize;
        [FieldOffset(16)] public nint blobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEXTENSIBLE
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(AudioClientShareMode shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, nint pFormat, nint audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    private sealed class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<object> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object activatedInterface);
                if (hr < 0)
                    _tcs.TrySetException(Marshal.GetExceptionForHR(hr)!);
                else
                    _tcs.TrySetResult(activatedInterface);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }

        public object WaitForInterface(TimeSpan timeout)
        {
            if (!_tcs.Task.Wait(timeout))
                throw new TimeoutException("Timeout aguardando ActivateAudioInterfaceAsync para Process Loopback.");
            return _tcs.Task.GetAwaiter().GetResult();
        }
    }
}