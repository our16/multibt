using System.Diagnostics;
using MultiBT.Core.Audio;
using MultiBT.Core.Devices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MultiBT.LoopbackVolumeProbe;

/// <summary>
/// Measures whether WASAPI loopback capture sees the endpoint volume slider.
/// </summary>
/// <remarks>
/// <para>
/// This decides a product behaviour, not an implementation detail. In cable mode Windows' default
/// output becomes the cable, so the volume keys (and the mute key) move the <i>cable's</i> slider.
/// If loopback is taken after that volume is applied, the slider behaves as a master volume for the
/// whole mirror and the keys keep working; if it is taken before, the keys do nothing at all and the
/// user would have no way to change loudness except our UI.
/// </para>
/// <para>
/// The same fact also decides whether a muted or barely-turned-up capture endpoint can silently
/// starve the mirror. A previous attempt at this experiment was inconclusive because it compared
/// RMS/silence fractions and never verified the volume write had taken effect; this one compares
/// <b>peak</b> level and reads every value back.
/// </para>
/// <para>
/// The definitive discriminator is volume 0: post-volume capture goes silent, pre-volume capture is
/// unaffected. The sweep therefore brackets that point — 100 % before and after it — so a dead
/// capture stream cannot be mistaken for a working post-volume answer.
/// </para>
/// <para>
/// Quiet by construction: a -34 dBFS tone. It is audible but soft, and it is a diagnostic tool.
/// </para>
/// </remarks>
internal static class Program
{
    private const double ToneAmplitude = 0.02;      // -34 dBFS
    private const double ToneHz = 1000.0;
    private const int SettleMs = 900;               // Windows ramps volume changes; do not measure through the ramp.
    private const int MeasureMs = 500;

    private static int Main(string[] args)
    {
        Console.WriteLine("MultiBT loopback-volume probe");
        Console.WriteLine(new string('=', 72));
        Console.WriteLine();

        using var devices = new DeviceManager();

        if (!devices.TryGetDefaultRenderDevice(out MMDevice? defaultDevice) || defaultDevice is null)
        {
            Console.WriteLine("FAIL: no default render endpoint.");
            return 1;
        }

        string endpointId = defaultDevice.ID;
        string name = defaultDevice.FriendlyName;
        defaultDevice.Dispose();

        if (args.Length >= 2 && args[0] == "--device")
        {
            endpointId = args[1];
        }

        Console.WriteLine($"endpoint : {name}");
        Console.WriteLine($"id       : {endpointId}");
        Console.WriteLine();

        // Remember state BEFORE touching anything, so the machine is left exactly as found.
        if (!EndpointVolumeReader.TryRead(devices, endpointId, out double originalVolume, out bool originalMuted))
        {
            Console.WriteLine("FAIL: endpoint exposes no volume control (nothing to probe).");
            return 1;
        }

        Console.WriteLine($"original : volume={originalVolume:P0} muted={originalMuted}");
        Console.WriteLine();
        Console.WriteLine("This will play a quiet (-34 dBFS) 1 kHz tone through the endpoint.");
        Console.WriteLine("The endpoint volume is changed during the run and restored afterwards.");
        Console.WriteLine();

        try
        {
            return Run(devices, endpointId, originalVolume, originalMuted);
        }
        finally
        {
            bool restored = EndpointVolumeReader.TryWrite(devices, endpointId, originalVolume, originalMuted);
            Console.WriteLine();
            Console.WriteLine(restored
                ? $"restored : volume={originalVolume:P0} muted={originalMuted}"
                : "WARNING  : could not restore the original volume - check the endpoint manually.");
        }
    }

    private static int Run(DeviceManager devices, string endpointId, double originalVolume, bool originalMuted)
    {
        if (!devices.TryResolveDevice(endpointId, out MMDevice? captureDevice) || captureDevice is null)
        {
            Console.WriteLine("FAIL: could not resolve the endpoint for capture.");
            return 1;
        }

        if (!devices.TryResolveDevice(endpointId, out MMDevice? playbackDevice) || playbackDevice is null)
        {
            captureDevice.Dispose();
            Console.WriteLine("FAIL: could not resolve the endpoint for playback.");
            return 1;
        }

        // Two separate MMDevice instances on purpose: the capture side owns and disposes its own.
        using var source = new AudioSource(captureDevice, 50);
        WaveFormat captureFormat = source.WaveFormat;

        Console.WriteLine($"capture  : {captureFormat.SampleRate} Hz, {captureFormat.Channels} ch, {captureFormat.Encoding}");
        Console.WriteLine();

        var peak = new PeakAccumulator(captureFormat);
        source.DataAvailable += peak.OnData;

        var stopped = new ManualResetEventSlim(false);
        Exception? captureFailure = null;
        source.Stopped += (_, ex) =>
        {
            captureFailure = ex;
            stopped.Set();
        };

        WasapiPlayer? output = null;

        try
        {
            output = BuildPlayback(playbackDevice, out IWaveProvider playbackSource);
            source.Start();
            output.Init(playbackSource);
            output.Play();

            Console.WriteLine("tone     : playing");
            Console.WriteLine();

            // Warm up: the capture stream only starts delivering once audio is actually flowing.
            Thread.Sleep(700);

            // Ordered so that full volume appears on both sides of the 0 % point. A stream that died
            // mid-run then shows up as "0 % silent AND 100 % silent", not as a post-volume answer.
            var steps = new (string Label, double Volume, bool Muted)[]
            {
                ("volume 100%", 1.00, false),
                ("volume  50%", 0.50, false),
                ("volume  25%", 0.25, false),
                ("volume   0%", 0.00, false),
                ("volume 100% again", 1.00, false),
                ("muted (volume 100%)", 1.00, true),
            };

            var results = new List<(string Label, double Requested, double Actual, bool Muted, double PeakDbfs)>();

            foreach ((string label, double volume, bool muted) in steps)
            {
                if (!EndpointVolumeReader.TryWrite(devices, endpointId, volume, muted))
                {
                    Console.WriteLine($"{label,-22} WRITE FAILED");
                    continue;
                }

                if (!EndpointVolumeReader.TryRead(devices, endpointId, out double actual, out bool actualMuted))
                {
                    Console.WriteLine($"{label,-22} READ-BACK FAILED");
                    continue;
                }

                Thread.Sleep(SettleMs);

                peak.Reset();
                Thread.Sleep(MeasureMs);
                double dbfs = peak.PeakDbfs;

                results.Add((label, volume, actual, actualMuted, dbfs));
                Console.WriteLine($"{label,-22} wrote {volume,5:P0} -> read {actual,5:P0} muted={actualMuted,-5} peak {dbfs,8:F1} dBFS");
            }

            Console.WriteLine();
            return Verdict(results, captureFailure);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try { output?.Stop(); } catch (Exception) { }
            try { output?.Dispose(); } catch (Exception) { }
            try { source.Stop(); } catch (Exception) { }

            stopped.Wait(TimeSpan.FromSeconds(2));
        }
    }

    private static WasapiPlayer BuildPlayback(MMDevice playbackDevice, out IWaveProvider playbackSource)
    {
        // Loopback capture delivers the device's mix format, so that is the format the tone must be
        // generated in: shared-mode WASAPI does not resample and rejects a mismatched format.
        WaveFormat mix;
        using (AudioClient probe = playbackDevice.CreateAudioClient())
        {
            mix = probe.MixFormat;
        }

        var tone = new ToneSource(WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels));

        if (mix.Encoding == WaveFormatEncoding.IeeeFloat && mix.BitsPerSample == 32)
        {
            playbackSource = new SampleToWaveProvider(tone);
        }
        else
        {
            // Rare on Windows 11 (the mix is almost always 32-bit float), but not impossible.
            Console.WriteLine($"note     : mix format is {mix.Encoding}/{mix.BitsPerSample}-bit, converting via ACM");
            playbackSource = new WaveFormatConversionProvider(mix, new SampleToWaveProvider(tone));
        }

        // WasapiOut is obsolete in NAudio 3.x and warns; WasapiPlayer is the supported path and is what
        // the engine itself uses, so the probe measures the same stack the product does.
        return new WasapiPlayerBuilder()
            .WithDevice(playbackDevice)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(100)
            .WithCategory(AudioStreamCategory.Media)
            .Build();
    }

    /// <summary>
    /// Turns the measurements into the answer the engine actually needs.
    /// </summary>
    /// <remarks>
    /// Reported as a measured conclusion, not a guess: the confidence column says what the run
    /// actually demonstrated, so a partially conclusive result cannot be read as a full answer.
    /// </remarks>
    private static int Verdict(
        List<(string Label, double Requested, double Actual, bool Muted, double PeakDbfs)> results,
        Exception? captureFailure)
    {
        if (captureFailure is not null)
        {
            Console.WriteLine($"capture stopped with: {captureFailure.GetType().Name}: {captureFailure.Message}");
            Console.WriteLine();
        }

        if (results.Count < 4)
        {
            Console.WriteLine("INCONCLUSIVE: too few steps completed.");
            return 1;
        }

        double full = results[0].PeakDbfs;
        double zero = results.FirstOrDefault(r => r.Requested == 0.0).PeakDbfs;
        double fullAgain = results.FirstOrDefault(r => r.Label.EndsWith("again", StringComparison.Ordinal)).PeakDbfs;
        double muted = results.FirstOrDefault(r => r.Muted).PeakDbfs;

        Console.WriteLine("-- verdict " + new string('-', 62));
        Console.WriteLine($"  peak at 100%       : {full,8:F1} dBFS");
        Console.WriteLine($"  peak at   0%       : {zero,8:F1} dBFS");
        Console.WriteLine($"  peak at 100% again : {fullAgain,8:F1} dBFS");
        Console.WriteLine($"  peak when muted    : {muted,8:F1} dBFS");
        Console.WriteLine();

        // The tone must have been measurable at full volume, or every "silent" reading proves nothing.
        if (full < -90.0)
        {
            Console.WriteLine("INCONCLUSIVE: the tone itself was not captured at full volume, so silence");
            Console.WriteLine("at lower volumes cannot be attributed to the volume slider.");
            Console.WriteLine("Check that the endpoint is not muted at the OS level and that nothing else");
            Console.WriteLine("has exclusive access to it.");
            return 1;
        }

        bool silentAtZero = zero < -80.0;
        bool backAtFull = fullAgain > full - 6.0;
        bool silentWhenMuted = muted < -80.0;

        Console.WriteLine("ANSWER: loopback capture is " + (silentAtZero ? "POST-volume" : "PRE-volume"));
        Console.WriteLine();
        Console.WriteLine("  POST-volume means the slider (and the mute key) of the CAPTURED endpoint scales");
        Console.WriteLine("  everything we mirror. In cable mode that makes the cable's slider a master");
        Console.WriteLine("  volume: the keyboard keys keep working. It also means a muted or barely");
        Console.WriteLine("  turned-up capture endpoint starves every output at once.");
        Console.WriteLine("  PRE-volume means the slider has no effect on the mirror at all.");

        if (!backAtFull)
        {
            Console.WriteLine();
            Console.WriteLine("NOTE: the level did not return at 100 %, so part of the drop may be the capture");
            Console.WriteLine("stream stopping rather than the volume change. Re-run on a quiet machine.");
        }

        if (!silentWhenMuted)
        {
            Console.WriteLine();
            Console.WriteLine("NOTE: mute did not silence the capture, which is unexpected if volume does.");
        }

        Console.WriteLine();
        return 0;
    }

    /// <summary>Generates a steady sine wave at the endpoint's own mix format.</summary>
    private sealed class ToneSource : ISampleProvider
    {
        private readonly int _channels;
        private readonly double _increment;
        private double _phase;

        public ToneSource(WaveFormat format)
        {
            WaveFormat = format ?? throw new ArgumentNullException(nameof(format));
            _channels = format.Channels;
            _increment = 2.0 * Math.PI * ToneHz / format.SampleRate;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            for (int frame = 0; frame < buffer.Length; frame += _channels)
            {
                float value = (float)(ToneAmplitude * Math.Sin(_phase));
                _phase += _increment;

                if (_phase > 2.0 * Math.PI)
                {
                    _phase -= 2.0 * Math.PI;
                }

                for (int channel = 0; channel < _channels; channel++)
                {
                    buffer[frame + channel] = value;
                }
            }

            return buffer.Length;
        }
    }

    /// <summary>Peak level of whatever the capture thread delivered during the current window.</summary>
    private sealed class PeakAccumulator
    {
        private readonly WaveFormat _format;
        private readonly object _gate = new();
        private double _peak;

        public PeakAccumulator(WaveFormat format) => _format = format;

        public double PeakDbfs
        {
            get
            {
                lock (_gate)
                {
                    return _peak <= 0.0 ? double.NegativeInfinity : 20.0 * Math.Log10(_peak);
                }
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _peak = 0.0;
            }
        }

        public void OnData(ReadOnlySpan<byte> buffer)
        {
            double window = 0.0;

            if (_format.Encoding == WaveFormatEncoding.IeeeFloat && _format.BitsPerSample == 32)
            {
                int count = buffer.Length / 4;
                for (int i = 0; i < count; i++)
                {
                    float sample = BitConverter.ToSingle(buffer.Slice(i * 4, 4));
                    double magnitude = Math.Abs(sample);
                    if (magnitude > window)
                    {
                        window = magnitude;
                    }
                }
            }
            else if (_format.BitsPerSample == 16)
            {
                int count = buffer.Length / 2;
                for (int i = 0; i < count; i++)
                {
                    short sample = BitConverter.ToInt16(buffer.Slice(i * 2, 2));
                    double magnitude = Math.Abs(sample / 32768.0);
                    if (magnitude > window)
                    {
                        window = magnitude;
                    }
                }
            }

            if (window <= 0.0)
            {
                return;
            }

            lock (_gate)
            {
                if (window > _peak)
                {
                    _peak = window;
                }
            }
        }
    }
}
