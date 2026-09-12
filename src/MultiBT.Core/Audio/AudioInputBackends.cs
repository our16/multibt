using MultiBT.Core.Devices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBT.Core.Audio;

/// <summary>
/// Captures the loopback of an ordinary render endpoint — typically the Windows default device.
/// </summary>
/// <remarks>
/// <para>
/// This is the zero-dependency backend: it needs nothing installed and works on any machine. Its one
/// limitation is inherent to the approach rather than to this class. Windows renders system audio directly
/// to the device being captured, so that device always plays natively, with no delay and no volume control
/// available to us. It can therefore be an OUTPUT of the mirror only by capturing its own playback, which
/// is a feedback loop.
/// </para>
/// <para>
/// Use <see cref="VirtualCableBackend"/> when every speaker must be controllable; use this when no cable is
/// installed and "most devices synchronised" is better than nothing.
/// </para>
/// </remarks>
public sealed class NativeLoopbackBackend : IAudioInputBackend
{
    private readonly AudioSource _source;
    private bool _disposed;

    /// <param name="renderDevice">
    /// Freshly resolved render endpoint to capture. This backend takes ownership and disposes it.
    /// </param>
    /// <param name="bufferLengthMs">Capture buffer length; 50 ms keeps latency low without waking too often.</param>
    public NativeLoopbackBackend(MMDevice renderDevice, int bufferLengthMs = 50)
    {
        ArgumentNullException.ThrowIfNull(renderDevice);

        DeviceName = renderDevice.FriendlyName;

        try
        {
            _source = new AudioSource(renderDevice, bufferLengthMs);
        }
        catch
        {
            // AudioSource takes ownership of the endpoint only once it is constructed. If it throws,
            // nothing owns the device yet, so this is the last chance to release it — otherwise the
            // capture endpoint stays open for the lifetime of the process.
            renderDevice.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public AudioInputKind Kind => AudioInputKind.NativeLoopback;

    /// <summary>Name of the endpoint being captured, for diagnostics.</summary>
    public string DeviceName { get; }

    /// <inheritdoc />
    public string Description => $"loopback of '{DeviceName}'";

    /// <inheritdoc />
    public WaveFormat Format => _source.WaveFormat;

    /// <inheritdoc />
    public event AudioDataAvailableHandler? DataAvailable
    {
        add => _source.DataAvailable += value;
        remove => _source.DataAvailable -= value;
    }

    /// <inheritdoc />
    public event EventHandler<Exception?>? Stopped
    {
        add => _source.Stopped += value;
        remove => _source.Stopped -= value;
    }

    /// <inheritdoc />
    public void Start() => _source.Start();

    /// <inheritdoc />
    public void Stop()
    {
        if (!_disposed)
        {
            _source.Stop();
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
        _source.Dispose();
    }
}

/// <summary>
/// Captures the loopback of a third-party virtual cable's render endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Mechanically this is <see cref="NativeLoopbackBackend"/> — under WASAPI, both are loopback capture of
/// some render endpoint, because that is the only way user mode can see another endpoint's audio. The
/// difference is <i>which</i> endpoint and why it is the right one: a cable is inaudible, so Windows can
/// render everything into it and EVERY real speaker can then be one of our outputs, each with its own delay
/// and volume.
/// </para>
/// <para>
/// It is a separate type rather than a flag because the two carry different guarantees and different failure
/// modes. This one validates that the endpoint really is a cable: pointing it at a speaker would double that
/// speaker's audio (native playback plus our delayed copy), and that is worth refusing up front rather than
/// debugging later.
/// </para>
/// <para>
/// <b>No brand is baked in.</b> Validation goes through <see cref="VirtualCableDetector"/>, which recognises
/// VB-CABLE, VoiceMeeter and Virtual Audio Cable. Replacing the cable, or eventually replacing it with our
/// own driver, changes which backend is constructed and nothing else.
/// </para>
/// </remarks>
public sealed class VirtualCableBackend : IAudioInputBackend
{
    private readonly NativeLoopbackBackend _inner;

    /// <param name="cableRenderDevice">
    /// Freshly resolved RENDER endpoint of a virtual cable, e.g. "CABLE Input (VB-Audio Virtual Cable)".
    /// This backend takes ownership and disposes it.
    /// </param>
    /// <param name="bufferLengthMs">Capture buffer length.</param>
    /// <exception cref="ArgumentException">
    /// The endpoint does not look like a virtual cable's render side.
    /// </exception>
    public VirtualCableBackend(MMDevice cableRenderDevice, int bufferLengthMs = 50)
    {
        ArgumentNullException.ThrowIfNull(cableRenderDevice);

        if (!VirtualCableDetector.IsVirtualCableRenderEndpoint(
                cableRenderDevice.FriendlyName,
                cableRenderDevice.DeviceFriendlyName))
        {
            cableRenderDevice.Dispose();

            throw new ArgumentException(
                $"'{cableRenderDevice.FriendlyName}' is not a virtual cable's render endpoint. Capturing a "
                + "real speaker here would play its audio twice (natively and through the mirror), so this "
                + "is refused rather than debugged later.",
                nameof(cableRenderDevice));
        }

        DeviceName = cableRenderDevice.FriendlyName;
        _inner = new NativeLoopbackBackend(cableRenderDevice, bufferLengthMs);
    }

    /// <inheritdoc />
    public AudioInputKind Kind => AudioInputKind.VirtualCable;

    /// <summary>Name of the cable endpoint being captured, for diagnostics.</summary>
    public string DeviceName { get; }

    /// <inheritdoc />
    public string Description => $"virtual cable '{DeviceName}'";

    /// <inheritdoc />
    public WaveFormat Format => _inner.Format;

    /// <inheritdoc />
    public event AudioDataAvailableHandler? DataAvailable
    {
        add => _inner.DataAvailable += value;
        remove => _inner.DataAvailable -= value;
    }

    /// <inheritdoc />
    public event EventHandler<Exception?>? Stopped
    {
        add => _inner.Stopped += value;
        remove => _inner.Stopped -= value;
    }

    /// <inheritdoc />
    public void Start() => _inner.Start();

    /// <inheritdoc />
    public void Stop() => _inner.Stop();

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Backend for a dedicated MultiBT virtual render endpoint. <b>Not implemented.</b>
/// </summary>
/// <remarks>
/// <para>
/// This type exists so the shape of the future option is recorded in code rather than only in a document,
/// and so that adding it later is a matter of implementing one class instead of restructuring the engine.
/// </para>
/// <para>
/// <b>How it would differ from the other two</b> — and why the interface is not shaped around loopback: a
/// dedicated driver would expose a CAPTURE endpoint, so user mode would read it with an ordinary capture
/// stream rather than a loopback stream. That is a genuinely different mechanism, not a different endpoint.
/// </para>
/// <para>
/// <b>Why it is not being built.</b> Compiling a kernel driver is the cheap half. Installing one on a user
/// machine needs either test signing (reboot plus a desktop watermark, unacceptable for users) or
/// attestation signing, which needs an EV code-signing certificate and a Partner Center account — a
/// recurring cost for no capability gain, because everything that makes MultiBT what it is lives in user
/// mode and works identically on someone else's cable. See docs/DECISIONS.md, ADR-001.
/// </para>
/// </remarks>
public sealed class VirtualAudioDriverBackend : IAudioInputBackend
{
    private const string NotBuiltMessage =
        "The MultiBT virtual audio driver backend is not implemented. MultiBT does not ship its own kernel "
        + "driver; see docs/DECISIONS.md (ADR-001). Use NativeLoopbackBackend or VirtualCableBackend.";

    /// <inheritdoc />
    public AudioInputKind Kind => AudioInputKind.VirtualAudioDriver;

    /// <inheritdoc />
    public string Description => "MultiBT virtual audio driver (not implemented)";

    /// <inheritdoc />
    public WaveFormat Format => throw new NotSupportedException(NotBuiltMessage);

    /// <inheritdoc />
    public event AudioDataAvailableHandler? DataAvailable
    {
        add => throw new NotSupportedException(NotBuiltMessage);
        remove { }
    }

    /// <inheritdoc />
    public event EventHandler<Exception?>? Stopped
    {
        add => throw new NotSupportedException(NotBuiltMessage);
        remove { }
    }

    /// <inheritdoc />
    public void Start() => throw new NotSupportedException(NotBuiltMessage);

    /// <inheritdoc />
    public void Stop()
    {
        // No-op rather than throwing: teardown paths call Stop() unconditionally and must not fault.
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
