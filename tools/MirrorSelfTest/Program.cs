// MultiBT — end-to-end mirror self-test. COMPLETELY SILENT: it makes no sound.
//
// WHY THIS EXISTS
// ---------------
// "Click Start Sync and the other devices still don't make sound" cannot be settled by reading
// code, and cannot be checked by ear in an automated environment. What CAN be checked is whether
// every stage of the REAL chain moves data. This tool uses the production classes in the
// production order (AudioSource -> synchronous fan-out in the capture callback -> OutputChannel ->
// WasapiPlayer), so a failure here is a failure of the real path, not of a test double.
//
// HOW IT STAYS SILENT
// -------------------
//  * The SOURCE device is fed DIGITAL SILENCE (all-zero samples). That is not a dodge to avoid
//    noise: WASAPI loopback delivers NOTHING while a device renders nothing (docs/PITFALLS.md A9),
//    so something must render or there is no capture to test at all. Rendering zeros drives the
//    loopback while emitting no signal.
//  * Every OUTPUT channel is opened with gain 0.0, so even if audio did arrive nothing is audible.
//
// WHAT IT PROVES
// --------------
//   1. loopback capture on the source endpoint delivers bytes
//   2. each output device accepts the chain (format negotiation, raw-mode fallback, Init)
//   3. the ring buffer actually FILLS, i.e. the resampler is producing rather than starving
//   4. the drift controller runs without resyncing or saturating

using System.Diagnostics;
using System.Runtime.InteropServices;
using MultiBT.Core.Audio;
using MultiBT.Core.Devices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MultiBT.MirrorSelfTest;

/// <summary>
/// A strictly inaudible but REAL signal: a 440 Hz sine at about −66 dBFS.
/// </summary>
/// <remarks>
/// <para>
/// An earlier version of this tool drove the loopback with DIGITAL SILENCE, and that made it unable
/// to answer the only question that matters. Silence is a valid test that "data moves" — the ring
/// fills, the resampler produces, the player pulls — but the OUTPUT is zero either way, so it cannot
/// distinguish "the chain is working" from "the chain is faithfully transporting zeros".
/// </para>
/// <para>
/// A −66 dBFS tone is genuinely inaudible while being unambiguously non-zero in the samples, which
/// makes it possible to assert that real audio survives the entire path into the player.
/// </para>
/// </remarks>
internal static class QuietTone
{
    /// <summary>Amplitude ≈ −66 dBFS.</summary>
    public const double Amplitude = 0.0005;

    public static IWaveProvider Create(WaveFormat format)
    {
        var generator = new SignalGenerator(format.SampleRate, format.Channels)
        {
            Frequency = 440,
            Gain = (float)Amplitude,
            Type = SignalGeneratorType.Sin,
        };

        return new SampleToWaveProvider(generator);
    }

    /// <summary>Whether a captured buffer contains any non-zero sample.</summary>
    public static bool HasNonZeroSample(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            foreach (float sample in MemoryMarshal.Cast<byte, float>(buffer))
            {
                if (Math.Abs(sample) > 1e-7f)
                {
                    return true;
                }
            }

            return false;
        }

        if (format.BitsPerSample == 16)
        {
            foreach (short sample in MemoryMarshal.Cast<byte, short>(buffer))
            {
                if (sample != 0)
                {
                    return true;
                }
            }

            return false;
        }

        foreach (byte value in buffer)
        {
            if (value != 0)
            {
                return true;
            }
        }

        return false;
    }
}

internal static class Program
{
    private const int DefaultRunSeconds = 6;

    /// <summary>
    /// Chain gain used as the start-up-ramp probe: about −60 dB, i.e. inaudible in any normal room,
    /// but non-zero so the ramp from silence to the configured level can actually be observed.
    /// </summary>
    private const double RampProbeGain = 0.001;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int runSeconds = DefaultRunSeconds;
        string? sourceSelector = null;
        bool noCorrection = false;
        var positionSpecs = new List<(string Match, MultiBT.Core.Sync.DevicePosition Position, string Origin)>();

        for (int i = 0; i < args.Length; i++)
        {
            if (int.TryParse(args[i], out int parsed) && parsed > 0)
            {
                runSeconds = parsed;
            }
            else if (args[i] is "--source" && i + 1 < args.Length)
            {
                sourceSelector = args[++i];
            }
            else if (args[i] is "--no-correction")
            {
                // Measures the chain's RAW rate balance: with the drift controller held off, each ring's fill climbs
                // or falls at the real rate error, in ppm. That is the number that distinguishes a genuine clock
                // difference between devices from a bug in our own rate handling -- a pinned correction cannot.
                noCorrection = true;
            }
            else if (args[i] is "--position" && i + 1 < args.Length)
            {
                // "<name substring>=<right>,<front>,<up>"
                string spec = args[++i];
                int eq = spec.IndexOf('=');

                if (eq <= 0)
                {
                    Console.WriteLine($"FATAL: --position wants '<name>=<right>,<front>,<up>'; got '{spec}'.");
                    return 1;
                }

                string[] parts = spec[(eq + 1)..].Split(',');

                if (parts.Length != 3
                    || !double.TryParse(parts[0], out double right)
                    || !double.TryParse(parts[1], out double front)
                    || !double.TryParse(parts[2], out double up))
                {
                    Console.WriteLine($"FATAL: --position wants three comma-separated metres; got '{spec}'.");
                    return 1;
                }

                positionSpecs.Add((
                    spec[..eq],
                    new MultiBT.Core.Sync.DevicePosition(right, front, up),
                    $"right {right:0.###}, front {front:0.###}, up {up:0.###}"));
            }
            else if (args[i] is "--direction" && i + 1 < args.Length)
            {
                // "<name substring>=<index>". Goes through the same call the direction picker in the UI makes,
                // so what this proves is the picker's own mapping and not a hand-written position that happens
                // to agree with it.
                string spec = args[++i];
                int eq = spec.IndexOf('=');

                if (eq <= 0 || !int.TryParse(spec[(eq + 1)..], out int directionIndex))
                {
                    Console.WriteLine(
                        $"FATAL: --direction wants '<name>=<0-{MultiBT.Core.Sync.SpatialMixer.DirectionCount - 1}>';"
                        + $" got '{spec}'.");
                    return 1;
                }

                positionSpecs.Add((
                    spec[..eq],
                    MultiBT.Core.Sync.SpatialMixer.DirectionPosition(directionIndex),
                    $"direction {directionIndex}"));
            }
        }

        Console.WriteLine("MultiBT mirror self-test (SILENT: plays digital silence, output gain 0)");
        Console.WriteLine(new string('=', 78));

        int failures = 0;
        using var devices = new DeviceManager();

        // ---------------------------------------------------------------- source device
        //
        // Defaults to the Windows default output, which is the normal configuration. `--source <text>`
        // selects another render endpoint by name instead, which is how the virtual-cable path gets
        // tested WITHOUT repointing the machine's default output: this tool renders into whatever source
        // it picks, so the cable receives audio and its loopback carries it, exactly as it would in
        // normal use - while everything the user is actually listening to stays where it is.
        string sourceId;
        string sourceName;
        WaveFormat probeFormat;

        if (sourceSelector is null)
        {
            if (!devices.TryGetDefaultRenderDevice(out MMDevice? probe) || probe is null)
            {
                Console.WriteLine("FATAL: no default render device; nothing to capture.");
                return 1;
            }

            using (probe)
            {
                sourceId = probe.ID;
                sourceName = probe.FriendlyName;
                using AudioClient client = probe.CreateAudioClient();
                probeFormat = client.MixFormat;
            }
        }
        else
        {
            AudioEndpointInfo? match = devices
                .EnumerateRenderEndpoints(includeInactive: false)
                .FirstOrDefault(e => e.FriendlyName.Contains(sourceSelector, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                Console.WriteLine($"FATAL: no active render endpoint matches '--source {sourceSelector}'.");
                Console.WriteLine("       Active endpoints:");

                foreach (AudioEndpointInfo e in devices.EnumerateRenderEndpoints(includeInactive: false))
                {
                    Console.WriteLine($"         {e.FriendlyName}");
                }

                return 1;
            }

            using MMDevice selected = Resolve(devices, match.EndpointId);
            sourceId = selected.ID;
            sourceName = selected.FriendlyName;
            using AudioClient client = selected.CreateAudioClient();
            probeFormat = client.MixFormat;

            Console.WriteLine($"[source] selected by '--source {sourceSelector}'");
        }

        Console.WriteLine();
        Console.WriteLine($"[source] {sourceName}");
        Console.WriteLine($"         {probeFormat.SampleRate} Hz, {probeFormat.Channels} ch, "
                          + $"{probeFormat.BitsPerSample} bit, {probeFormat.Encoding}");

        // ---------------------------------------------------------------- drive the loopback
        // The mirror captures what the source endpoint is RENDERING. If Windows is not playing to
        // it, the loopback is silent no matter how correct the rest of the engine is. Rendering
        // zeros makes that condition true without making noise.
        WasapiPlayer? silencePlayer = null;

        try
        {
            using MMDevice silenceDevice = Resolve(devices, sourceId);

            silencePlayer = new WasapiPlayerBuilder()
                .WithDevice(silenceDevice)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(100)
                .WithCategory(AudioStreamCategory.Media)
                .Build();

            silencePlayer.Init(QuietTone.Create(probeFormat));
            silencePlayer.Play();

            Console.WriteLine();
            Console.WriteLine($"[drive] rendering a {QuietTone.Amplitude * 100:0.00}% (−66 dBFS, inaudible) 440 Hz tone");
            Console.WriteLine("        to the source device, so the loopback carries REAL audio rather than zeros");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        FAIL: could not render to the source device: {ex.Message}");
            failures++;
        }

        // ---------------------------------------------------------------- capture (not started)
        var channels = new List<(OutputChannel Channel, MMDevice Device, string Name, string EndpointId, double GainAtStart)>();
        long fedBytes = 0;
        long fedCallbacks = 0;
        long signalCallbacks = 0;

        using MMDevice captureDevice = Resolve(devices, sourceId);
        using var audioSource = new AudioSource(captureDevice);

        // The capture format comes from the recorder itself, exactly as AudioEngine does it, so the
        // chains are built at a rate that is guaranteed to match what will actually arrive.
        WaveFormat captureFormat = audioSource.WaveFormat;

        Console.WriteLine();
        Console.WriteLine($"[capture] loopback format {captureFormat.SampleRate} Hz, {captureFormat.Channels} ch, "
                          + $"{captureFormat.BitsPerSample} bit, {captureFormat.Encoding}");

        // ---------------------------------------------------------------- build output chains
        List<AudioEndpointInfo> targets = devices
            .EnumerateRenderEndpoints(includeInactive: false)
            .Where(e => !string.Equals(e.EndpointId, sourceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine();
        Console.WriteLine($"[outputs] {targets.Count} active endpoint(s) besides the source");

        if (targets.Count == 0)
        {
            Console.WriteLine("         NOTE: no second active endpoint exists, so there is nothing to mirror to.");
            failures++;
        }

        foreach (AudioEndpointInfo target in targets)
        {
            Console.WriteLine();
            Console.WriteLine($"  -> {target.FriendlyName}  [{target.Transport}]");

            MMDevice? targetDevice = null;

            // Resolved ONCE for all channels, before any is built: the distance delay is relative to the
            // NEAREST device, so a placement computed device-by-device as the loop ran would give whichever
            // device happened to be first a different reference from the rest.
            IReadOnlyDictionary<string, MultiBT.Core.Sync.SpatialPlacement> channelPlacements =
                MultiBT.Core.Sync.SpatialMixer.ComputePlacements(
                    targets
                        .SelectMany(t => positionSpecs
                            .Where(s => t.FriendlyName.Contains(s.Match, StringComparison.OrdinalIgnoreCase))
                            .Select(s => (t.EndpointId, s.Position)))
                        .GroupBy(p => p.EndpointId)
                        .ToDictionary(g => g.Key, g => g.First().Position, StringComparer.Ordinal));

            try
            {
                targetDevice = Resolve(devices, target.EndpointId);

                WaveFormat deviceFormat;
                using (AudioClient client = targetDevice.CreateAudioClient())
                {
                    deviceFormat = client.MixFormat;
                }

                Console.WriteLine($"     device format {deviceFormat.SampleRate} Hz, {deviceFormat.Channels} ch");

                if (deviceFormat.SampleRate != captureFormat.SampleRate)
                {
                    Console.WriteLine($"     rate conversion {captureFormat.SampleRate} -> {deviceFormat.SampleRate} Hz");
                }

                // A deliberately tiny chain gain so the start-up ramp can be observed without being
                // audible: the point of the ramp is that playback begins SILENT and rises to the
                // configured level, so starting silent and staying silent would prove nothing.
                var channel = new OutputChannel(
                    target.EndpointId,
                    targetDevice,
                    captureFormat,
                    EngineTunables.DefaultLatencyMs,
                    RampProbeGain);

                channel.Start();

                if (noCorrection)
                {
                    channel.DriftCorrectionEnabled = false;
                }

                // Sampled immediately: the ramp must begin from silence.
                double gainAtStart = channel.CurrentGain;

                if (channelPlacements.TryGetValue(target.EndpointId, out MultiBT.Core.Sync.SpatialPlacement placement))
                {
                    channel.SetSpatialGains(placement.LeftGain, placement.RightGain);
                    channel.SetSpatialDelayMs(placement.DelayMs);

                    // Named so the run says which request produced these numbers: "--direction 2" and
                    // "--position 1,0,0" are the same placement but very different claims.
                    string origin = positionSpecs
                        .Where(s => target.FriendlyName.Contains(s.Match, StringComparison.OrdinalIgnoreCase))
                        .Select(s => s.Origin)
                        .FirstOrDefault() ?? "an unmatched spec";

                    Console.WriteLine($"     position applied from {origin}: L={placement.LeftGain:0.###} R={placement.RightGain:0.###} "
                                      + $"distance delay={placement.DelayMs:0.##} ms");
                }

                channels.Add((channel, targetDevice, target.FriendlyName, target.EndpointId, gainAtStart));
                targetDevice = null;   // ownership transferred to the channel

                Console.WriteLine($"     Init OK  requested={EngineTunables.DefaultLatencyMs} ms  "
                                  + $"granted={channel.EngineLatencyGrantedMs} ms  "
                                  + $"avgLat={channel.AverageLatencyMs:0.#} ms");
                Console.WriteLine($"     state={Describe(channel)}"
                                  + (channel.InitializationNote is null ? string.Empty : $"   note: {channel.InitializationNote}"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"     FAIL: could not open the chain: {ex.GetType().Name}: {ex.Message}");
                targetDevice?.Dispose();
                failures++;
            }
        }

        // ---------------------------------------------------------------- wire the fan-out
        // Subscribed AFTER the chains exist and BEFORE capture starts, and it fans out SYNCHRONOUSLY
        // inside the callback — exactly like AudioEngine.OnDataAvailable. The span is only valid for
        // the duration of the call, so anything that stores it for later is wrong.
        audioSource.DataAvailable += buffer =>
        {
            Interlocked.Add(ref fedBytes, buffer.Length);
            Interlocked.Increment(ref fedCallbacks);

            // Is the capture carrying REAL audio, or is everything zeros?
            if (QuietTone.HasNonZeroSample(buffer, captureFormat))
            {
                Interlocked.Increment(ref signalCallbacks);
            }

            for (int i = 0; i < channels.Count; i++)
            {
                try
                {
                    channels[i].Channel.AddSamples(buffer);
                }
                catch (Exception ex)
                {
                    channels[i].Channel.ReportFault(ex);
                }
            }
        };

        audioSource.Start();

        // ---------------------------------------------------------------- loopback volume order
        //
        // DECISIVE EXPERIMENT: is loopback captured BEFORE or AFTER the source endpoint's volume?
        //
        //   PRE  -> zeroing the endpoint volume silences the native direct path while the capture keeps
        //           full level. The capture-source device then becomes fully controllable (delay AND
        //           volume) with NO virtual audio cable -- which is the only way "the primary device
        //           must not sound on its own, so we can control it" can be satisfied in software alone.
        //   POST -> zeroing the volume zeroes the capture too, so that trick is impossible and a SILENT
        //           sink (a spare unused output, or a virtual cable) is required instead.
        //
        // The original volume is restored in a finally block; the source is silent for ~1 second.
        Console.WriteLine();
        Console.WriteLine("[volume order] pre- or post-endpoint-volume?");

        if (EndpointVolumeReader.TryRead(devices, sourceId, out double originalVolume, out bool originalMuted))
        {
            long bytesBefore = Interlocked.Read(ref fedBytes);
            long signalBefore = Interlocked.Read(ref signalCallbacks);

            try
            {
                EndpointVolumeReader.TryWrite(devices, sourceId, volumeScalar: 0.0);
                Thread.Sleep(400);

                // CONFIRM the write took effect. Without this the whole experiment rests on an
                // unverified write, and a silently-failed write would look exactly like PRE-volume.
                EndpointVolumeReader.TryRead(devices, sourceId, out double appliedVolume, out bool appliedMuted);
                Console.WriteLine($"          volume actually applied: {appliedVolume * 100:0.#}% (muted={appliedMuted})");

                if (appliedVolume > 0.02)
                {
                    Console.WriteLine("          -> INCONCLUSIVE: the volume write did not take effect.");
                }

                Thread.Sleep(900);

                long deltaBytes = Interlocked.Read(ref fedBytes) - bytesBefore;
                long deltaSignal = Interlocked.Read(ref signalCallbacks) - signalBefore;

                Console.WriteLine($"          at 0% volume: {deltaBytes} bytes arrived, {deltaSignal} carrying signal");

                if (deltaBytes == 0)
                {
                    Console.WriteLine("          -> POST-VOLUME (capture stops entirely at 0%). Silent sink required.");
                }
                else if (deltaSignal > 0)
                {
                    Console.WriteLine("          -> PRE-VOLUME. The source device CAN be fully controlled");
                    Console.WriteLine("             (delay + volume) with no virtual audio cable.");
                }
                else
                {
                    Console.WriteLine("          -> POST-VOLUME (bytes arrive but are silent). Silent sink required.");
                }
            }
            finally
            {
                EndpointVolumeReader.TryWrite(devices, sourceId, volumeScalar: originalVolume, muted: originalMuted);
                Console.WriteLine($"          (restored source volume to {originalVolume * 100:0}%)");
            }
        }
        else
        {
            Console.WriteLine("          SKIPPED: source endpoint volume not readable.");
        }
        // ---------------------------------------------------------------- run
        if (channels.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"[run] mirroring for {runSeconds}s at {EngineTunables.ControlTickHz} Hz control...");

            var elapsed = Stopwatch.StartNew();
            var sinceTick = Stopwatch.StartNew();
            double tickMs = 1000.0 / EngineTunables.ControlTickHz;

            while (elapsed.Elapsed.TotalSeconds < runSeconds)
            {
                if (sinceTick.Elapsed.TotalMilliseconds >= tickMs)
                {
                    sinceTick.Restart();

                    for (int i = 0; i < channels.Count; i++)
                    {
                        channels[i].Channel.UpdateDrift();
                    }
                }

                Thread.Sleep(10);
            }

            Console.WriteLine($"      capture callbacks={Interlocked.Read(ref fedCallbacks)}, "
                              + $"carrying real signal={Interlocked.Read(ref signalCallbacks)}, "
                              + $"bytes offered to every channel={Interlocked.Read(ref fedBytes)}");

            if (Interlocked.Read(ref signalCallbacks) == 0)
            {
                Console.WriteLine("      FAIL: the captured stream is ALL ZEROS while the source is rendering a tone.");
                Console.WriteLine("            The mirror has no audio to carry, so no output can sound no matter how");
                Console.WriteLine("            healthy the rest of the chain looks. Check the capture source device.");
                failures++;
            }
            else
            {
                Console.WriteLine("      OK: capture carries real audio (not just zero-fill).");
            }

            Console.WriteLine();
            Console.WriteLine("[results]");

            for (int i = 0; i < channels.Count; i++)
            {
                ChannelDiagnostics d = channels[i].Channel.GetDiagnostics();

                Console.WriteLine($"  {channels[i].Name}");
                Console.WriteLine($"     {d.Summarize()}");
                Console.WriteLine($"     starved={d.StarvedReads}  overflow={d.OverflowDrops}  resync={d.ResyncCount}  "
                                  + $"appliedDelay={d.AppliedDelayMs:0.#} ms  grantedLat={d.EngineLatencyMs} ms");
                Console.WriteLine($"     endpoint volume={(double.IsNaN(d.EndpointVolumeScalar) ? "n/a" : $"{d.EndpointVolumeScalar * 100:0}%")}"
                                  + $"  muted={d.EndpointMuted}");
                // Left and right separately: this is the evidence that positioning works, and the one thing the
                // aggregate level cannot show, since both pan directions give the same total.
                double? left = channels[i].Channel.LeftPeak;
                double? right = channels[i].Channel.RightPeak;

                Console.WriteLine(left is null || right is null
                    ? "     channels: not stereo, so panning cannot be measured or applied"
                    : $"     channels: L={Dbfs(left.Value)}  R={Dbfs(right.Value)}"
                      + (left.Value > 0.000001 && right.Value > 0.000001
                          ? $"  L/R ratio={(left.Value / right.Value):0.000}"
                          : string.Empty));

                Console.WriteLine($"     player state={Describe(channels[i].Channel)}");
                Console.WriteLine($"     start-up ramp: gain at Start = {channels[i].GainAtStart:0.######} "
                                  + $"(target {RampProbeGain:0.###}) -> now {channels[i].Channel.CurrentGain:0.######}");

                if (channels[i].GainAtStart > RampProbeGain * 0.5)
                {
                    // The whole point is "do not startle me": if playback opens at the configured level
                    // instead of ramping up from silence, that is a regression.
                    Console.WriteLine("     FAIL: playback did NOT begin from silence — the start-up ramp is missing.");
                    failures++;
                }
                else if (Math.Abs(channels[i].Channel.CurrentGain - RampProbeGain) > 1e-6)
                {
                    Console.WriteLine("     FAIL: the start-up ramp did not reach the configured level.");
                    failures++;
                }
                else
                {
                    Console.WriteLine("     OK: ramped up from silence to the configured level.");
                }

                // Now mute this channel for the remainder of the checks so nothing is audible.
                channels[i].Channel.SetGain(0.0);

                bool fed = Interlocked.Read(ref fedBytes) > 0;
                bool carryingAudio = d.HasSignal;
                bool healthy = d.StarvedReads == 0 && channels[i].Channel.Fault is null;

                if (!fed || !healthy)
                {
                    Console.WriteLine("     FAIL: the chain is not consuming data.");
                    failures++;
                }
                else if (!carryingAudio)
                {
                    // This is the distinction the app previously could not make.
                    Console.WriteLine("     FAIL: audio reaches the ring but NOT the player — every block is silent.");
                    failures++;
                }
                else if (d.BlockedAtEndpoint)
                {
                    // Our samples are provably correct, so the fix is at the endpoint, not the engine.
                    Console.WriteLine("     FAIL: audio reaches the player correctly, but the WINDOWS ENDPOINT is "
                                      + (d.EndpointMuted ? "MUTED" : $"at {d.EndpointVolumeScalar * 100:0}% volume")
                                      + " — so it cannot be audible. Unmute / raise the device volume.");
                    failures++;
                }
                else
                {
                    Console.WriteLine("     OK: real audio reaches the player at the right level, endpoint is audible.");
                }

                // Verify the per-device volume actually reaches the signal path.
                //
                // The gain slider used to update only the view model, which is indistinguishable from
                // "the slider does nothing". Reading the live provider back proves the value landed.
                // Only tiny values are used, so nothing is audible:
                //   * 0.001 exercises the normal path,
                //   * -5.0 exercises the CLAMP -- it clamps to 0, i.e. silence, whereas clamping a
                //     too-large value would briefly open the channel at full volume.
                channels[i].Channel.SetGain(0.001);
                bool gainLanded = Math.Abs(channels[i].Channel.CurrentGain - 0.001) < 1e-9;

                channels[i].Channel.SetGain(-5.0);
                bool gainClamped = Math.Abs(channels[i].Channel.CurrentGain) < 1e-9;

                channels[i].Channel.SetGain(0.0);

                Console.WriteLine($"     volume control: set 0.001 -> {gainLanded}, clamp(-5.0) -> {gainClamped}");
                if (!gainLanded || !gainClamped)
                {
                    Console.WriteLine("     FAIL: the per-device volume does not reach the audio chain.");
                    failures++;
                }

                // Verify the device's OWN Windows volume is readable and writable.
                // Written back to the SAME value, so nothing on the machine changes.
                if (EndpointVolumeReader.TryRead(devices, channels[i].EndpointId, out double currentVolume, out bool currentMuted))
                {
                    bool wrote = EndpointVolumeReader.TryWrite(
                        devices,
                        channels[i].EndpointId,
                        volumeScalar: currentVolume,
                        muted: currentMuted);

                    bool readBack = EndpointVolumeReader.TryRead(devices, channels[i].EndpointId, out double after, out _);

                    Console.WriteLine($"     device volume: read {currentVolume * 100:0}% (muted={currentMuted}), "
                                      + $"write-back ok={wrote}, read-back ok={readBack}");

                    if (!wrote || !readBack || Math.Abs(after - currentVolume) > 0.02)
                    {
                        Console.WriteLine("     FAIL: the device's Windows volume is not controllable.");
                        failures++;
                    }
                }
                else
                {
                    Console.WriteLine("     device volume: NOT readable — the slider will be disabled for it.");
                }
            }
        }

        // ---------------------------------------------------------------- teardown
        audioSource.Stop();

        foreach ((OutputChannel channel, MMDevice device, _, _, _) in channels)
        {
            TryFadeAndDispose(channel);
            device.Dispose();
        }

        if (silencePlayer is not null)
        {
            try
            {
                silencePlayer.Stop();
                silencePlayer.Dispose();
            }
            catch (Exception)
            {
                // best effort
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(failures == 0
            ? "RESULT: the full mirror path moves data end to end on this machine."
            : $"RESULT: {failures} problem(s) found — see above.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>A linear peak as dBFS, with silence reported as -inf rather than as a large negative number.</summary>
    private static string Dbfs(double linear) =>
        linear <= 0.0 ? "-inf dBFS" : $"{20.0 * Math.Log10(linear):0.0} dBFS";

    private static MMDevice Resolve(DeviceManager devices, string endpointId) =>
        devices.TryResolveDevice(endpointId, out MMDevice? device) && device is not null
            ? device
            : throw new InvalidOperationException($"could not resolve endpoint '{endpointId}'");

    private static string Describe(OutputChannel channel) =>
        channel.Fault is null ? "no fault" : $"FAULT {channel.Fault.GetType().Name}: {channel.Fault.Message}";

    private static void TryFadeAndDispose(OutputChannel channel)
    {
        try
        {
            channel.FadeOutAsync(TimeSpan.FromMilliseconds(EngineTunables.FadeMs)).GetAwaiter().GetResult();
            channel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
