using System.Reflection;
using MultiBT.Core.Audio;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// Pins the input-backend abstraction: the engine must accept PCM from an interface, and no layer of it
/// may name a specific virtual cable.
/// </summary>
/// <remarks>
/// <para>
/// These tests exist because the abstraction is easy to erode by accident. Replacing the cable, or one day
/// shipping a driver, is supposed to change which backend is constructed and nothing else; a single
/// concrete type or brand string inside the engine would quietly make that false. Nothing here needs audio
/// hardware, so the contract is checked on every run rather than only on a machine with a cable installed.
/// </para>
/// </remarks>
public class AudioInputBackendTests
{
    private static readonly string[] CableBrands =
    [
        "cable", "vb-audio", "vbaudio", "voicemeeter", "virtual audio cable", "vac",
    ];

    private static IEnumerable<Type> PublicCoreTypes =>
        typeof(AudioEngine).Assembly.GetExportedTypes();

    [Fact]
    public void EngineTakesAnInterfaceNotAConcreteInput()
    {
        MethodInfo start = typeof(AudioEngine).GetMethod(nameof(AudioEngine.Start))
            ?? throw new InvalidOperationException("AudioEngine.Start not found");

        ParameterInfo parameter = Assert.Single(start.GetParameters());

        // The whole point of the refactor: the engine is handed "something that produces PCM".
        Assert.Equal(typeof(IAudioInputBackend), parameter.ParameterType);
    }

    [Fact]
    public void EveryBackendImplementsTheContract()
    {
        Type contract = typeof(IAudioInputBackend);

        foreach (Type backend in new[]
                 {
                     typeof(NativeLoopbackBackend),
                     typeof(VirtualCableBackend),
                     typeof(VirtualAudioDriverBackend),
                 })
        {
            Assert.True(
                contract.IsAssignableFrom(backend),
                $"{backend.Name} does not implement {contract.Name}");
        }
    }

    [Fact]
    public void EngineTypeNamesNothingVendorSpecific()
    {
        // Members, fields and constructor parameters of the engine itself. Local variable names are not
        // visible to reflection, but a vendor-typed field or a brand-named parameter is exactly the shape
        // the erosion would take, so this is the useful half of the check.
        Type engine = typeof(AudioEngine);

        IEnumerable<string> names = engine
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name)
            .Concat(engine.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Select(p => p.Name ?? string.Empty));

        foreach (string name in names.Distinct())
        {
            foreach (string brand in CableBrands)
            {
                Assert.False(
                    name.Contains(brand, StringComparison.OrdinalIgnoreCase),
                    $"AudioEngine exposes '{name}', which names a specific cable ({brand}). "
                    + "Backend choice belongs to the caller; see docs/DECISIONS.md (ADR-001).");
            }
        }
    }

    [Fact]
    public void NoVendorNamedTypeExistsInTheAudioLayer()
    {
        // A type called VBCableBackend would be a brand in the engine's own namespace. Detection of a
        // cable may exist (VirtualCableDetector), but "which vendor" must never become a type.
        foreach (Type type in PublicCoreTypes.Where(t => t.Namespace == typeof(AudioEngine).Namespace))
        {
            foreach (string brand in new[] { "vbcable", "vbaudio", "voicemeeter" })
            {
                Assert.False(
                    type.Name.Contains(brand, StringComparison.OrdinalIgnoreCase),
                    $"{type.FullName} hard-codes the vendor '{brand}'.");
            }
        }
    }

    [Fact]
    public void KindsAreDistinctAndCoverTheThreeMechanisms()
    {
        AudioInputKind[] kinds = Enum.GetValues<AudioInputKind>();

        // Loopback of a system endpoint, loopback of a third-party cable, and a dedicated capture
        // endpoint from our own driver. They are three mechanisms, not three settings.
        Assert.Equal(3, kinds.Length);
        Assert.Equal(kinds.Length, kinds.Distinct().Count());
        Assert.Contains(AudioInputKind.NativeLoopback, kinds);
        Assert.Contains(AudioInputKind.VirtualCable, kinds);
        Assert.Contains(AudioInputKind.VirtualAudioDriver, kinds);
    }

    [Fact]
    public void UnimplementedDriverBackendFailsLoudlyWhenStarted()
    {
        using var backend = new VirtualAudioDriverBackend();

        Assert.Equal(AudioInputKind.VirtualAudioDriver, backend.Kind);

        // Asking for audio must not silently return nothing: silence would look like a working mirror
        // with no sound, which is far harder to diagnose than an exception naming the reason.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(backend.Start);
        Assert.Contains("ADR-001", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnimplementedDriverBackendIsSafeToTearDown()
    {
        var backend = new VirtualAudioDriverBackend();

        // Stop() and Dispose() are unconditional in teardown paths, including paths that run when a
        // start already failed. Faulting here would replace the real error with a teardown error.
        Exception? stopFailure = Record.Exception(backend.Stop);
        Assert.Null(stopFailure);

        Exception? disposeFailure = Record.Exception(backend.Dispose);
        Assert.Null(disposeFailure);

        // Idempotent: the engine disposes a backend it already stopped.
        Assert.Null(Record.Exception(backend.Dispose));
    }

    [Fact]
    public void UnimplementedDriverBackendDoesNotReportAFormatItCannotProduce()
    {
        using var backend = new VirtualAudioDriverBackend();

        // There is no stream and therefore no format. Returning a plausible default would let the caller
        // build an entire chain against a format nothing ever produces.
        Assert.Throws<NotSupportedException>(() => backend.Format);
    }
}
