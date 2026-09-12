namespace MultiBT.Core.Devices;

using MultiBT.Core.Audio;

/// <summary>
/// Recognises virtual audio cables among render endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a virtual cable is needed at all.</b> Windows renders system audio DIRECTLY to the default
/// output endpoint. A loopback capture is a copy taken off that path, so the default device always
/// plays its audio natively — outside our control, with zero added delay and no volume knob. That is
/// why a "primary" device could never be delayed: it was not being skipped, it was on a different path
/// that does not pass through this app.
/// </para>
/// <para>
/// A virtual cable fixes that by giving Windows somewhere inaudible to render into:
/// </para>
/// <code>
/// Windows default output -> virtual cable (inaudible, exists only to be captured)
///                              | loopback capture
///                              v
///                          MultiBT -> every real speaker, each with delay + volume under our control
/// </code>
/// <para>
/// <b>Detection is by display name, which is a heuristic.</b> There is no API that reports "this
/// endpoint is virtual" — the driver is an ordinary audio driver as far as Core Audio is concerned.
/// So this matches the well-known product names, and deliberately returns false for anything it does
/// not recognise rather than guessing: a wrong positive would divert system audio into a device the
/// user cannot hear, which is a silent failure of the worst kind.
/// </para>
/// </remarks>
public static class VirtualCableDetector
{
    /// <summary>
    /// Name fragments that identify a virtual cable's RENDER side (the endpoint Windows renders into).
    /// </summary>
    private static readonly string[] RenderMarkers =
    [
        "cable input",                  // VB-Audio Virtual Cable
        "voicemeeter input",
        "voicemeeter aux input",
        "voicemeeter vaio3 input",
        "voicemeeter vaio input",
        "virtual audio cable",
        "vb-audio",
        "voicemeeter",
        "virtual cable",
    ];

    /// <summary>
    /// Name fragments that identify a virtual cable's CAPTURE side.
    /// </summary>
    /// <remarks>
    /// These must stay DIRECTION-SPECIFIC. Virtual Audio Cable, for example, names both sides
    /// "Line 1 (Virtual Audio Cable)", so a generic marker here would exclude its perfectly valid render
    /// side. Only names that say "output"/"out" belong in this list.
    /// </remarks>
    private static readonly string[] CaptureMarkers =
    [
        "cable output",                 // VB-Audio Virtual Cable
        "voicemeeter out",
        "voicemeeter aux out",
        "voicemeeter vaio3 out",
    ];

    /// <summary>
    /// Whether an endpoint looks like a virtual cable's render side.
    /// </summary>
    /// <param name="friendlyName">Endpoint display name, e.g. "CABLE Input (VB-Audio Virtual Cable)".</param>
    /// <param name="deviceDescription">Optional device description.</param>
    public static bool IsVirtualCableRenderEndpoint(string? friendlyName, string? deviceDescription = null)
    {
        // Exclude the CAPTURE side first, and deliberately before the render check.
        //
        // "CABLE Output (VB-Audio Virtual Cable)" contains the generic marker "virtual audio cable", so
        // testing render markers first classified the RECORDING endpoint as a usable sink. Windows would
        // then be told to render system audio into a device that cannot play it — total silence, and it
        // would look like broken audio code rather than a mis-detected sink.
        if (Matches(friendlyName, CaptureMarkers) || Matches(deviceDescription, CaptureMarkers))
        {
            return false;
        }

        return Matches(friendlyName, RenderMarkers) || Matches(deviceDescription, RenderMarkers);
    }

    /// <summary>Whether an endpoint looks like a virtual cable's capture (recording) side.</summary>
    public static bool IsVirtualCableCaptureEndpoint(string? friendlyName, string? deviceDescription = null) =>
        Matches(friendlyName, CaptureMarkers) || Matches(deviceDescription, CaptureMarkers);

    /// <summary>
    /// Whether an endpoint is a usable capture sink: a virtual cable's render side.
    /// </summary>
    /// <remarks>
    /// Only ACTIVE endpoints qualify — an unplugged or disabled sink would capture nothing, and the
    /// failure would look like the mirror being broken rather than the sink not existing.
    /// </remarks>
    public static bool IsUsableCaptureSink(AudioEndpointInfo endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.IsActive && IsVirtualCableRenderEndpoint(endpoint.FriendlyName, endpoint.DeviceFriendlyName);
    }

    /// <summary>
    /// Orders endpoints for the input picker: usable ones first, then by name.
    /// </summary>
    /// <remarks>
    /// A virtual cable gets NO special position. It is one legitimate input among others — capturing a real
    /// device is a perfectly reasonable choice — and ranking it first would tell the user it is the correct
    /// answer, which is exactly the coupling this list is supposed to avoid.
    /// </remarks>
    public static IReadOnlyList<AudioEndpointInfo> OrderForSinkPicker(IEnumerable<AudioEndpointInfo> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints
            .OrderByDescending(e => e.IsActive)
            .ThenBy(e => e.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Picks the best available virtual cable, or null when none is installed.</summary>
    public static AudioEndpointInfo? FindBestCaptureSink(IEnumerable<AudioEndpointInfo> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.FirstOrDefault(IsUsableCaptureSink);
    }


    private static bool Matches(string? text, string[] markers)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
