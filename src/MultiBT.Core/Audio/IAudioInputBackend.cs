using NAudio.Wave;

namespace MultiBT.Core.Audio;

/// <summary>Where captured audio comes from.</summary>
public enum AudioInputKind
{
    /// <summary>Loopback of an ordinary render endpoint — the Windows default or any chosen device.</summary>
    NativeLoopback = 0,

    /// <summary>Loopback of a third-party virtual cable's render endpoint (VB-CABLE, VoiceMeeter, VAC).</summary>
    VirtualCable = 1,

    /// <summary>A capture endpoint exposed by a dedicated driver. Not implemented yet.</summary>
    VirtualAudioDriver = 2,
}

/// <summary>
/// The engine's only source of PCM.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this interface exists.</b> The engine must not know whether its audio arrives from the Windows
/// default device, from someone else's virtual cable, or from a cable we ship ourselves. Those are three
/// products with three different install stories, and the engine's job — fan-out, per-device delay, drift
/// correction, profiles, recovery — is identical in all three cases. Binding the engine to any one of them
/// would mean rewriting it if that choice ever changed.
/// </para>
/// <para>
/// <b>The interface abstracts "give me PCM", not "do a loopback".</b> That distinction is the whole point.
/// Today every backend happens to be implemented with WASAPI loopback, because that is the only way to see
/// another endpoint's audio from user mode. A future dedicated driver is genuinely different: it would
/// expose a CAPTURE endpoint and be read with an ordinary capture stream, not a loopback one. An interface
/// shaped around loopback would have to be broken to accept it.
/// </para>
/// <para>
/// <b>Nothing here is vendor-specific.</b> There is deliberately no VB-CABLE type in this contract, no brand
/// name in a signature, and no assumption that a cable is installed. Cable detection belongs to
/// <see cref="Devices.VirtualCableDetector"/> and to the code that chooses a backend, never to the engine.
/// </para>
/// </remarks>
public interface IAudioInputBackend : IDisposable
{
    /// <summary>Short human-readable description, for diagnostics.</summary>
    string Description { get; }

    /// <summary>Which kind of source this is.</summary>
    AudioInputKind Kind { get; }

    /// <summary>
    /// The format PCM will arrive in. Valid before <see cref="Start"/>, so the per-device chains can be
    /// built at the right rate.
    /// </summary>
    WaveFormat Format { get; }

    /// <summary>
    /// Raised per captured packet, on the backend's own thread. The span is valid ONLY for the duration of
    /// the call — the engine fans it out synchronously and never retains it.
    /// </summary>
    event AudioDataAvailableHandler? DataAvailable;

    /// <summary>Raised when capture stops, carrying the originating exception when there was one.</summary>
    event EventHandler<Exception?>? Stopped;

    /// <summary>Begins capture.</summary>
    void Start();

    /// <summary>Stops capture. Must be safe to call more than once.</summary>
    void Stop();
}
