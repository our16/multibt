namespace MultiBT.Core.Sync;

/// <summary>
/// What "synchronised" means for a profile. This is a user-facing trade-off, not an internal
/// detail: on a machine with both a Bluetooth speaker and a wired DAC, these two goals are
/// mutually exclusive.
/// </summary>
/// <remarks>
/// Aligning everything to the slowest device sets the SYSTEM end-to-end latency to
/// <c>max(measured)</c>. With a Bluetooth speaker at 200–400 ms that exceeds the
/// ITU-R BT.1359-1 audio-lag detectability threshold of ~125 ms, so lip-sync breaks.
/// The UI must surface the resulting system latency and let the user choose.
/// See docs/SPEC.md §6.7 and docs/PITFALLS.md B3.
/// </remarks>
public enum SyncMode
{
    /// <summary>
    /// Delay every device so all wavefronts land together. Correct for music.
    /// System latency becomes <c>max(measured)</c>.
    /// </summary>
    AlignAll = 0,

    /// <summary>
    /// Align only the wired group internally and leave Bluetooth at its natural latency.
    /// Correct for video: the wired outputs stay early enough to stay in lip-sync.
    /// </summary>
    WiredOnly = 1,
}
