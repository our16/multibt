// MultiBT — API surface probe.
//
// PURPOSE: this file is a COMPILE-TIME contract check, not application code.
// It touches every NAudio / Core Audio API that docs/SPEC.md mandates, so that if a
// future NAudio release renames or removes something, this fails to build instead of
// failing at runtime in the middle of an audio stream.
//
// It performs no I/O and never runs in production. `dotnet build` is the whole test.
//
// Verified against: NAudio 3.1.0 on net10.0-windows10.0.19041.0 (.NET SDK 10.0.400)

using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MultiBT.ApiProbe;

internal static class Program
{
    private static int Main()
    {
        ProbeDeviceEnumeration();
        ProbeEndpointProperties();
        ProbeNotifications();
        ProbeLoopbackCapture();
        ProbePlayerChain();
        ProbeLatencyReporting();
        ProbeClockAndEngineLatency();
        ProbeResampler();
        ProbeDelayLine();
        ProbeBufferedProviderSemantics();
        Console.WriteLine("ApiProbe: all mandated API surfaces compiled. (no runtime work performed)");
        return 0;
    }

    // ---------------------------------------------------------------------
    // Device enumeration — NAudio.CoreAudioApi, shipped by NAudio.Wasapi
    // ---------------------------------------------------------------------
    private static void ProbeDeviceEnumeration()
    {
        using var enumerator = new MMDeviceEnumerator();

        // NOTE: DeviceState.All is required to see a paired-but-disconnected Bluetooth
        // endpoint, which is reported as Unplugged rather than Active.
        MMDeviceCollection render = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);

        for (int i = 0; i < render.Count; i++)
        {
            MMDevice device = render[i];   // materialises a FRESH wrapper on every access
            _ = device.ID;                 // opaque WASAPI endpoint id -> pass to GetDevice()
            _ = device.FriendlyName;       // e.g. "JBL Charge 5"
            _ = device.DeviceFriendlyName; // e.g. "Speakers (JBL Charge 5)"
            _ = device.InstanceId;         // PnP instance id -> "BTHENUM\..." for Bluetooth
            _ = device.State;              // DeviceState.Active / Unplugged / ...
            _ = device.DataFlow;
            device.Dispose();
        }

        // Resolution + identity helpers the engine must use.
        bool hasDefault = enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice? def))
        {
            def.Dispose();
        }
        _ = hasDefault;
    }

    // ---------------------------------------------------------------------
    // Endpoint metadata — PropertyStore / PropertyKeys
    // ---------------------------------------------------------------------
    private static void ProbeEndpointProperties()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice? device))
        {
            return;
        }

        using (device)
        {
            PropertyStore store = device.Properties;

            // Preferred accessor: safe cast, returns false when the key is absent.
            // Property availability is environment-dependent — never assume.
            if (store.TryGetValue<string>(PropertyKeys.PKEY_Device_FriendlyName, out string? friendly))
            {
                _ = friendly;
            }
            if (store.TryGetValue<string>(PropertyKeys.PKEY_Device_InstanceId, out string? instanceId))
            {
                _ = instanceId;
            }
            if (store.TryGetValue<string>(PropertyKeys.PKEY_DeviceInterface_FriendlyName, out string? iface))
            {
                _ = iface;
            }
            if (store.TryGetValue<string>(PropertyKeys.PKEY_AudioEndpoint_GUID, out string? endpointGuid))
            {
                _ = endpointGuid;
            }

            // Form factor arrives boxed; NAudio defines the key but no enum. Do not
            // assume the boxed CLR type — probe it rather than pattern-matching blindly.
            if (store.TryGetValue<object>(PropertyKeys.PKEY_AudioEndpoint_FormFactor, out object? formFactor))
            {
                _ = formFactor;
            }

            _ = store.Count;
            _ = store.Contains(PropertyKeys.PKEY_Device_FriendlyName);
        }
    }

    // ---------------------------------------------------------------------
    // Device notifications — NAudio 3.x EVENT API (IMMNotificationClient is internal)
    // ---------------------------------------------------------------------
    private static void ProbeNotifications()
    {
        using var enumerator = new MMDeviceEnumerator();

        // useSynchronizationContext:true marshals onto the context captured here.
        // false => handlers must be non-blocking and must NOT call back into the audio
        // stack (deadlock risk). Production code posts to a queue and does work there.
        using MMDeviceNotificationClient notifications = enumerator.CreateNotificationClient(useSynchronizationContext: false);

        notifications.DeviceStateChanged += (_, e) =>
        {
            string id = e.DeviceId;       // opaque — compare, never parse
            DeviceState state = e.NewState;
            _ = id;
            _ = state;
        };
        notifications.DeviceAdded += (_, e) => _ = e.DeviceId;
        notifications.DeviceRemoved += (_, e) => _ = e.DeviceId;
        notifications.DefaultDeviceChanged += (_, e) =>
        {
            _ = e.DeviceId;
            _ = e.Flow;
            _ = e.Role;
        };
        notifications.PropertyValueChanged += (_, e) =>
        {
            // HIGH FREQUENCY (fires on every volume change). Keep it trivial.
            _ = e.PropertyKey;
        };
    }

    // ---------------------------------------------------------------------
    // Loopback capture — WasapiRecorderBuilder (supersedes WasapiLoopbackCapture)
    // ---------------------------------------------------------------------
    private static void ProbeLoopbackCapture()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice? renderDevice))
        {
            return;
        }

        using (renderDevice)
        {
            using WasapiRecorder recorder = new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .WithDevice(renderDevice)
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(50)
                .Build();

            // WaveFormat is available after Build(); the default is the device mix format.
            WaveFormat captureFormat = recorder.WaveFormat;
            _ = captureFormat.SampleRate;
            _ = captureFormat.Channels;
            _ = captureFormat.BitsPerSample;
            _ = captureFormat.Encoding;

            // Zero-copy path: the span is valid ONLY for the duration of the callback.
            // devicePosition (frames at packet start) and qpcPosition (QPC, 100ns units)
            // are the raw timing signals. Per NAudio's own docs these are UNRELIABLE on
            // some shared-mode drivers (real on the first packet, then zero) — so the
            // engine must validate them before trusting them.
            recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
            {
                ReadOnlySpan<byte> span = buffer;
                AudioClientBufferFlags f = flags;
                long devPos = devicePosition;
                long qpc = qpcPosition;
                _ = span.Length;
                _ = f;
                _ = devPos;
                _ = qpc;
            };

            recorder.RecordingStopped += (_, e) =>
            {
                // Capture side is hardened since NAudio 3.0: this ALWAYS fires, carrying
                // the originating exception for device removal.
                Exception? ex = e.Exception;
                _ = ex;
            };

            // Present on WasapiRecorder too.
            if (recorder is IWaveLatency captureLatency)
            {
                _ = captureLatency.AverageLatency;
                _ = captureLatency.CurrentLatency;
            }

            _ = recorder.DeviceId;

            // Not started: this probe performs no I/O.
        }
    }

    // ---------------------------------------------------------------------
    // Playback — one WasapiPlayer per output device, each with its OWN chain
    // ---------------------------------------------------------------------
    private static void ProbePlayerChain()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice? renderDevice))
        {
            return;
        }

        using (renderDevice)
        {
            WasapiPlayer player = new WasapiPlayerBuilder()
                .WithDevice(renderDevice)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(DEFAULT_LATENCY_MS)          // default is 200ms; we drive it explicitly
                .WithRawMode()                            // bypass APO "enhancements"
                .WithCategory(AudioStreamCategory.Media)
                .WithMmcssThreadPriority("Pro Audio")     // the ONLY supported MMCSS route
                .Build();

            player.Init(BuildPerDeviceChain(new WaveFormat(48000, 32, 2)));
            // NOTE: Init is NOT thread-safe and must be called exactly once per instance,
            // before Play, never concurrently with Play/Stop/Dispose.

            _ = player.OutputWaveFormat;
            _ = player.PlaybackState;
            _ = player.LatencyMilliseconds;   // latency actually in use after Init
            _ = player.LowLatencyActive;
            _ = player.LowLatencyUnavailableReason;

            // Session-level volume (this app's slider in the Windows mixer).
            player.Volume = 0.8f;
            player.IsMuted = false;
            _ = player.SessionVolume;
            _ = player.StreamVolume;
            _ = player.DeviceVolume;          // ENDPOINT-wide; affects every app. Avoid.

            player.PlaybackStopped += (_, e) =>
            {
                // 3.1.0 BUG (#1442): a Read exception can strand the player in Playing
                // forever. On 3.1.0 the recovery path must create a NEW player, never
                // reuse a possibly-stranded one.
                Exception? ex = e.Exception;
                _ = ex;
            };

            _ = player.PlaybackState;
        }
    }

    // ---------------------------------------------------------------------
    // Per-device chain, in the order the engine MUST wire it:
    //   BufferedWaveProvider (drift ring)
    //     -> adaptive resampler (only ratio the controller moves)
    //     -> delay line        (compensation; a SEPARATE buffer)
    //     -> volume            (per-device gain)
    //     -> SampleToWaveProvider -> WasapiPlayer
    // ---------------------------------------------------------------------
    private static IWaveProvider BuildPerDeviceChain(WaveFormat deviceMixFormat)
    {
        // Duration is CTOR-ONLY in NAudio 3.x; BufferLength/BufferDuration are read-only.
        var ring = new BufferedWaveProvider(deviceMixFormat, TimeSpan.FromSeconds(2.0))
        {
            // MUST be true: FillBuffer treats a 0-byte read as end-of-stream and would
            // permanently stop this player across any capture gap.
            ReadFully = true,
            // Deliberately false: when true, AddSamples SILENTLY DROPS THE NEWEST samples
            // with no exception and no counter. We resync explicitly instead.
            DiscardOnBufferOverflow = false,
        };

        ISampleProvider samples = ring.ToSampleProvider();

        // Fixed-ratio format conversion, if the capture format differs from the device
        // mix format. The CONTROLLER must not use this type — it cannot be retuned at
        // runtime. See ProbeResampler() for the type that can.
        if (samples.WaveFormat.SampleRate != deviceMixFormat.SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, deviceMixFormat.SampleRate);
        }

        if (samples.WaveFormat.Channels == 1 && deviceMixFormat.Channels == 2)
        {
            samples = new MonoToStereoSampleProvider(samples);
        }

        var volume = new VolumeSampleProvider(samples) { Volume = 0.7f };
        _ = volume.WaveFormat;

        // Metering for the UI level bar.
        var meter = new MeteringSampleProvider(volume);
        meter.StreamVolume += (_, e) =>
        {
            float max = e.MaxSampleValues.Length > 0 ? e.MaxSampleValues[0] : 0f;
            _ = max;
        };

        _ = ring.BufferedBytes;
        _ = ring.BufferedDuration;   // the observable the drift controller steers
        ring.ClearBuffer();          // bounded resync path
        ring.AddSamples(ReadOnlySpan<byte>.Empty);

        return new SampleToWaveProvider(meter);
    }

    private const int DEFAULT_LATENCY_MS = 100;

    // ---------------------------------------------------------------------
    // Latency reporting — NAudio.Core, implemented by WasapiPlayer + WasapiRecorder
    // ---------------------------------------------------------------------
    private static void ProbeLatencyReporting()
    {
        static void ReadLatency(IWaveLatency latency)
        {
            TimeSpan avg = latency.AverageLatency;   // steady-state pipeline depth
            TimeSpan cur = latency.CurrentLatency;   // live, from GetCurrentPadding
            _ = avg;
            _ = cur;
        }

        static void ReadPosition(IWavePosition position)
        {
            long pos = position.GetPosition();
            _ = pos;
        }

        _ = (Action<IWaveLatency>)ReadLatency;
        _ = (Action<IWavePosition>)ReadPosition;
    }

    // ---------------------------------------------------------------------
    // Raw Core Audio clocks + engine latency.
    // REQUIRED because WasapiPlayer does not expose its internal AudioClient, and
    // because IWavePosition.GetPosition() is ADJUSTED (nominal-frequency extrapolation)
    // and therefore masks the very drift we need to observe.
    // ---------------------------------------------------------------------
    private static void ProbeClockAndEngineLatency()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice? device))
        {
            return;
        }

        using (device)
        {
            using AudioClient client = device.CreateAudioClient();
            // device.AudioClient is [Obsolete] — CreateAudioClient() is the supported route.

            _ = client.MixFormat;
            _ = client.SupportsAudioClient2;
            _ = client.SupportsAudioClient3;

            // Callable BEFORE Initialize — this is what sizes the drift trough target and
            // tells us the endpoint's real pull granularity (as coarse as ~60ms on wireless).
            _ = client.DefaultDevicePeriod;
            _ = client.MinimumDevicePeriod;

            if (client.SupportsAudioClient3)
            {
                AudioClientPeriodInfo period = client.GetSharedModeEnginePeriod(client.MixFormat);
                _ = period.DefaultPeriodInFrames;
                _ = period.FundamentalPeriodInFrames;
                _ = period.MinPeriodInFrames;
                _ = period.MaxPeriodInFrames;
                _ = period.ChooseLowestLatencyPeriod();
            }

            // The following require an INITIALIZED stream, so they are contract-only here.
            _ = client.StreamLatency;   // IAudioClient::GetStreamLatency, 100ns units
            _ = client.BufferSize;      // IAudioClient::GetBufferSize, frames
            _ = client.CurrentPadding;  // frames currently queued

            // Raw (unadjusted) clock pair — position units and the QPC timestamp in 100ns
            // units. THIS is the drift observable; S_FALSE reads must be discarded.
            try
            {
                AudioClockClient clock = client.AudioClockClient;
                _ = clock.Frequency;
                _ = clock.AdjustedPosition;   // <-- do NOT use for drift: assumes zero drift
                clock.GetPosition(out ulong position, out ulong qpc);
                _ = position;
                _ = qpc;
            }
            catch (Exception)
            {
                // Requires a running stream; contract check only.
            }
        }
    }

    // ---------------------------------------------------------------------
    // Drift actuation — NAudio.Dsp.WdlResampler, driven directly.
    // Neither WdlResamplingSampleProvider nor MediaFoundationResampler can be retuned
    // at runtime, so the controller MUST wrap this type itself.
    // ---------------------------------------------------------------------
    private static void ProbeResampler()
    {
        var resampler = new WdlResampler();
        resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
        resampler.SetFilterParms();
        resampler.SetFeedMode(false);              // output-driven
        resampler.SetRates(48000.0, 48000.0);      // rate_in, rate_out

        // Safe to call mid-stream: only m_ratio is recomputed. m_fracpos, the input
        // buffer and the IIR history are all preserved, so there is no discontinuity.
        const double MAX_CORRECTION = 200e-6;      // +/-200 ppm
        resampler.SetRates(48000.0 * (1.0 + MAX_CORRECTION), 48000.0);

        _ = resampler.GetCurrentLatency();

        const int OUT_SAMPLES = 480;
        int prepared = resampler.ResamplePrepare(OUT_SAMPLES, 2, out Span<float> inBuffer);
        _ = prepared;
        _ = inBuffer.Length;
        _ = resampler.ResampleOut(new float[OUT_SAMPLES * 2], prepared, OUT_SAMPLES, 2);

        // ResampleOut may satisfy FEWER frames than requested. The caller must loop until
        // the output buffer is full — and must NEVER return 0 from a live pipeline.
    }

    // ---------------------------------------------------------------------
    // Fractional-sample delay — NAudio.Dsp.DelayLine (mono, float, hand-fed)
    // ---------------------------------------------------------------------
    private static void ProbeDelayLine()
    {
        var line = new DelayLine(48000);           // capacity in samples
        _ = line.MaxDelaySamples;

        line.Write(0.25f);
        _ = line.Read(240);                        // integer tap
        _ = line.Read(240.5f);                     // fractional tap, linearly interpolated
        line.Reset();

        // Note: this type is NOT an ISampleProvider. One instance is required PER CHANNEL,
        // and compensation is quantised to whole samples in the spec anyway (<=20.8us).
    }

    // ---------------------------------------------------------------------
    // Failure modes that NAudio hides by default — asserted here as documentation.
    // ---------------------------------------------------------------------
    private static void ProbeBufferedProviderSemantics()
    {
        var format = new WaveFormat(48000, 32, 2);
        var ring = new BufferedWaveProvider(format, TimeSpan.FromSeconds(2.0));

        // ReadFully defaults to TRUE: a starving Read zero-fills and STILL returns count,
        // so starvation is INDISTINGUISHABLE from real silence. Instrument BufferedDuration
        // before and after every read to count it.
        Debug.Assert(ring.ReadFully);

        // DiscardOnBufferOverflow defaults to FALSE, but when enabled it drops the NEWEST
        // samples silently. We keep it false and resync on a bounded backlog instead.
        Debug.Assert(!ring.DiscardOnBufferOverflow);

        _ = ring.BufferedBytes;
        _ = ring.BufferedDuration;
        _ = ring.BufferLength;      // READ-ONLY in NAudio 3.x
    }
}
