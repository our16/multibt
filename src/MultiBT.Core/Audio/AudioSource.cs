using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBT.Core.Audio;

/// <summary>
/// Receives captured audio. The span is valid ONLY for the duration of the call — the
/// callback must copy or consume it synchronously and must never retain it.
/// </summary>
/// <remarks>
/// A custom delegate is required rather than <c>Action&lt;ReadOnlySpan&lt;byte&gt;&gt;</c>,
/// because a <c>ref struct</c> cannot be a generic type argument.
/// </remarks>
public delegate void AudioDataAvailableHandler(ReadOnlySpan<byte> buffer);

/// <summary>
/// WASAPI loopback capture of one render endpoint — the single source that feeds every output.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>WasapiRecorder</c> + <c>WasapiRecorderBuilder</c>, which supersede
/// <c>WasapiLoopbackCapture</c> in NAudio 3.x. See docs/PITFALLS.md A3.
/// </para>
/// <para>
/// <b>Silence is not delivered.</b> A loopback capture only raises <c>DataAvailable</c> while
/// audio is actually playing through the device. Everything downstream must therefore be able
/// to produce silence on its own — which is why every per-output ring buffer sets
/// <c>ReadFully = true</c>. Without that, a capture gap makes the ring return 0 bytes, and the
/// playback path treats a 0-byte read as end-of-stream and stops that channel permanently.
/// See docs/PITFALLS.md A9.
/// </para>
/// <para>
/// <b>The device passed here must be freshly resolved.</b> Do not cache the <c>MMDevice</c>:
/// after sleep/resume a cached COM object is dead even though the endpoint still enumerates.
/// </para>
/// </remarks>
public sealed class AudioSource : IDisposable
{
    private readonly WasapiRecorder _recorder;
    private bool _disposed;

    /// <param name="renderDevice">
    /// Freshly resolved render endpoint to mirror. Resolve it via
    /// <c>DeviceManager</c> at activation time, not once at startup.
    /// </param>
    /// <param name="bufferLengthMs">Capture buffer length. 50 ms keeps latency low without waking too often.</param>
    public AudioSource(MMDevice renderDevice, int bufferLengthMs = 50)
    {
        ArgumentNullException.ThrowIfNull(renderDevice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferLengthMs);

        _recorder = new WasapiRecorderBuilder()
            .WithLoopbackCapture()
            .WithDevice(renderDevice)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(bufferLengthMs)
            .WithMmcssThreadPriority("Pro Audio")
            .Build();

        // Capture format is the source device's mix format. Do not force it to something
        // "uniform": in shared mode the engine converts, and we only handle the per-DEVICE
        // differences inside each output chain.
        WaveFormat = _recorder.WaveFormat;

        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
    }

    /// <summary>Capture format (the source device's mix format).</summary>
    public WaveFormat WaveFormat { get; }

    /// <summary>Raised for each captured packet, on the capture thread.</summary>
    public event AudioDataAvailableHandler? DataAvailable;

    /// <summary>
    /// Raised when capture stops — always, including on device removal, carrying the
    /// originating exception when there was one.
    /// </summary>
    public event EventHandler<Exception?>? Stopped;

    /// <summary>Begins capture.</summary>
    public void Start() => _recorder.StartRecording();

    /// <summary>Stops capture.</summary>
    public void Stop()
    {
        if (!_disposed)
        {
            _recorder.StopRecording();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recorder.DataAvailable -= OnDataAvailable;
        _recorder.RecordingStopped -= OnRecordingStopped;
        _recorder.Dispose();
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        // devicePosition / qpcPosition are deliberately ignored here.
        //
        // They describe the CAPTURE endpoint's clock, which says nothing about the OUTPUT
        // devices we are trying to synchronise, and NAudio documents them as unreliable on
        // some shared-mode drivers ("real on the first packet, then zero"). Drift must be
        // measured on the render side. See docs/PITFALLS.md A10 and SPEC.md §6.6.
        DataAvailable?.Invoke(buffer);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) =>
        Stopped?.Invoke(this, e.Exception);
}
