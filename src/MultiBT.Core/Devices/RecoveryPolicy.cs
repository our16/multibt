namespace MultiBT.Core.Devices;

/// <summary>
/// Lifecycle state of one output channel.
/// </summary>
/// <remarks>
/// See docs/SPEC.md §7.1 for the transition diagram. The important properties of this machine:
/// a device that disappears keeps its membership of the desired set (so it re-arms itself when
/// it returns), and <see cref="Failed"/> is terminal for automatic recovery — only an explicit
/// user action leaves it.
/// </remarks>
public enum ChannelState
{
    /// <summary>User switched this output off. No work is done.</summary>
    Disabled = 0,

    /// <summary>Desired, but the endpoint is not currently active. Waiting for it to appear.</summary>
    Waiting = 1,

    /// <summary>Endpoint resolved; building the chain and calling Init.</summary>
    Starting = 2,

    /// <summary>Playing.</summary>
    Running = 3,

    /// <summary>Errored or the device vanished; retrying with backoff.</summary>
    Recovering = 4,

    /// <summary>Retries exhausted. Requires a user action to leave this state.</summary>
    Failed = 5,
}

/// <summary>
/// Bounded retry policy for channel recovery, plus the settle delay Windows needs after a
/// Bluetooth device connects.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never an unbounded watchdog.</b> A device that is genuinely gone (switched off, out of
/// range, unpaired) must not make the app retry forever; that burns the radio and the battery
/// and hides the real problem from the user.
/// </para>
/// <para>
/// <b>The settle delay is real behaviour, not padding.</b> After a Bluetooth device connects,
/// Windows needs roughly 3–5 seconds before all of its audio endpoints are initialised.
/// Starting a stream sooner fails, and the failure looks like a bug in our code.
/// </para>
/// </remarks>
public sealed class RecoveryPolicy
{
    /// <summary>Maximum automatic attempts before the channel is marked failed.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Seconds to wait after a Bluetooth device reports connected, before opening a stream.</summary>
    public const double ReconnectSettleSeconds = 4.0;

    private int _attempts;

    /// <summary>Attempts made so far.</summary>
    public int Attempts => _attempts;

    /// <summary>True once retries are exhausted and only a user action can recover the channel.</summary>
    public bool IsExhausted => _attempts >= MaxAttempts;

    /// <summary>
    /// Reserves the next retry and returns the delay to wait, or <c>false</c> when exhausted.
    /// </summary>
    /// <remarks>Backoff is 1 s, 2 s, 4 s.</remarks>
    public bool TryScheduleRetry(out TimeSpan delay)
    {
        if (IsExhausted)
        {
            delay = TimeSpan.Zero;
            return false;
        }

        _attempts++;
        delay = TimeSpan.FromSeconds(Math.Pow(2, _attempts - 1));
        return true;
    }

    /// <summary>Delay to wait before opening a stream on a device that just connected.</summary>
    public static TimeSpan ReconnectSettleDelay => TimeSpan.FromSeconds(ReconnectSettleSeconds);

    /// <summary>Clears the attempt counter after a successful start.</summary>
    public void Reset() => _attempts = 0;
}
