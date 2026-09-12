namespace MultiBT.App.Recommendations;

/// <summary>
/// A virtual audio device MultiBT recommends but does not ship.
/// </summary>
/// <param name="NameKey">Localisation key for the product's display name.</param>
/// <param name="NoteKey">Localisation key for the one-line note shown beside it.</param>
/// <param name="Url">The vendor's own page. This is the one that opens when the entry is clicked.</param>
/// <param name="RepositoryUrl">
/// Source repository, when the product has a public one. Offered as a submenu beside the vendor page, so the
/// recommended link stays a single obvious click and the source is one hover away.
/// </param>
public sealed record VirtualAudioRecommendation(
    string NameKey,
    string NoteKey,
    string Url,
    string? RepositoryUrl = null);

/// <summary>
/// The virtual audio devices MultiBT points users at.
/// </summary>
/// <remarks>
/// <para>
/// <b>We recommend; we do not distribute.</b> Nothing here downloads, bundles or hosts a driver. Each entry
/// opens the vendor's own page and the user installs from there, so the licence terms are between them and
/// the vendor — VB-Audio's, for instance, does not permit redistributing its installers, which rules out
/// bundling outright. That is also why this list lives in the UI layer and contains only links: the audio
/// engine still knows nothing about any particular product (see docs/DECISIONS.md, ADR-003).
/// </para>
/// <para>
/// The notes are deliberately short and factual, and they state the real catch where there is one. A list
/// that only says "free!" would send someone to install a product that needs Windows test signing, or to
/// discover the trial voice reminder only after wiring it through every speaker.
/// </para>
/// </remarks>
public static class VirtualAudioRecommendations
{
    /// <summary>Recommended devices, best first for MultiBT's use.</summary>
    public static IReadOnlyList<VirtualAudioRecommendation> All { get; } =
    [
        // One inaudible cable, free, installs and is signed for normal Windows. This is the simplest
        // thing that makes every speaker controllable, and it is what MultiBT is tested against.
        new("Recommend.VbCable", "Recommend.VbCable.Note", "https://vb-audio.com/Cable/"),

        // Free as well, and it brings several virtual inputs plus a mixer. Heavier than this app needs,
        // but a sensible choice for anyone who already runs it.
        new("Recommend.VoiceMeeter", "Recommend.VoiceMeeter.Note", "https://vb-audio.com/VoiceMeeter/index.htm"),

        // The multi-cable option. Worth stating that the free tier is one cable and that the trial version
        // announces itself through the audio, because that reminder would be mirrored to every speaker.
        new("Recommend.VirtualAudioCable", "Recommend.VirtualAudioCable.Note", "https://vac.muzychenko.net/en/"),

        // Open source (MIT) and free of charge, but installation needs Windows test signing, so it is
        // listed last with that stated plainly rather than presented as an easy alternative.
        new(
            "Recommend.MttDriver",
            "Recommend.MttDriver.Note",
            "https://github.com/VirtualDrivers/Virtual-Audio-Driver",
            "https://github.com/VirtualDrivers/Virtual-Audio-Driver"),
    ];
}
