// MultiBT — hardware smoke check.
//
// PURPOSE: exercise the code paths that depend on THIS machine and cannot be unit-tested:
//   * WASAPI render endpoint enumeration, including the unplugged state where a
//     paired-but-disconnected Bluetooth device lives
//   * the endpoint property keys the transport classifier relies on
//   * the NAudio 3.x notification API (MMDeviceNotificationClient, not IMMNotificationClient)
//   * settings load, profile joining, and compensation arithmetic
//
// It opens NO audio streams and plays NO sound, so it is safe to run at any time.
//
// This directly tests the UNCONFIRMED items in docs/PITFALLS.md §D. Run it first on any new
// machine, and read the "logs for review" it prints — the classifier's prefixes are
// convention, not documented guarantees, so the real values must be confirmed by eye.

using MultiBT.Core.Audio;
using MultiBT.Core.Config;
using MultiBT.Core.Devices;
using MultiBT.Core.Sync;
using NAudio.CoreAudioApi;

namespace MultiBT.SmokeCheck;

internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int failures = 0;

        Console.WriteLine("MultiBT smoke check");
        Console.WriteLine(new string('=', 72));

        try
        {
            using var devices = new DeviceManager();

            // 1. Enumeration, including inactive endpoints.
            Console.WriteLine();
            Console.WriteLine("[1] Render endpoints (DeviceState.All)");
            IReadOnlyList<AudioEndpointInfo> endpoints = devices.EnumerateRenderEndpoints();

            if (endpoints.Count == 0)
            {
                Console.WriteLine("    !! no render endpoints found — is an audio device present?");
                failures++;
            }

            foreach (AudioEndpointInfo endpoint in endpoints)
            {
                Console.WriteLine($"    {(endpoint.IsActive ? "active  " : "inactive")} "
                                  + $"{endpoint.FriendlyName}");
                Console.WriteLine($"             transport : {endpoint.Transport}");
                Console.WriteLine($"             instanceId: {endpoint.InstanceId}");
            }

            // 2. Active-only view, to confirm the All/Active difference is real.
            Console.WriteLine();
            Console.WriteLine("[2] Active-only view");
            int activeCount = devices.EnumerateRenderEndpoints(includeInactive: false).Count;
            Console.WriteLine($"    all={endpoints.Count}  active={activeCount}  "
                              + $"inactive={endpoints.Count - activeCount}");
            if (endpoints.Count == activeCount)
            {
                Console.WriteLine("    note: no inactive endpoints right now. A paired-but-disconnected");
                Console.WriteLine("          Bluetooth device would appear ONLY in the All view.");
            }
            else
            {
                Console.WriteLine("    OK: the All view sees endpoints the Active view does not — this is");
                Console.WriteLine("        exactly why enumeration must use DeviceState.All.");
            }

            // 3. Transport classification sanity.
            Console.WriteLine();
            Console.WriteLine("[3] Transport classification");
            var byTransport = endpoints.GroupBy(e => e.Transport).OrderBy(g => g.Key);
            foreach (var group in byTransport)
            {
                Console.WriteLine($"    {group.Key,-9} {group.Count()}");
            }

            List<AudioEndpointInfo> unclassified = endpoints.Where(e => e.Transport == Transport.Other).ToList();
            if (unclassified.Count > 0)
            {
                Console.WriteLine($"    note: {unclassified.Count} endpoint(s) fell back to Other. Their real");
                Console.WriteLine("          instance ids are printed above — check whether any is a Bluetooth");
                Console.WriteLine("          or HDMI device the classifier should have caught.");
            }

            // 3b. RAW property-store dump.
            //
            // This is the diagnostic that answers docs/PITFALLS.md §D3/D4 on any machine. Device
            // identity persistence and Bluetooth detection both hinge on which of these
            // properties is actually populated — and property availability is
            // environment-dependent, so it must be read rather than assumed.
            Console.WriteLine();
            Console.WriteLine("[3b] Raw property store (first 3 endpoints) — verify identity keys exist");
            foreach (AudioEndpointInfo endpoint in endpoints.Take(3))
            {
                if (!devices.TryResolveDevice(endpoint.EndpointId, out var probeDevice) || probeDevice is null)
                {
                    continue;
                }

                using (probeDevice)
                {
                    Console.WriteLine($"    {endpoint.FriendlyName}");
                    Console.WriteLine($"        MMDevice.ID      : {endpoint.EndpointId}");
                    Console.WriteLine($"        MMDevice.InstanceId: '{endpoint.InstanceId}'");

                    bool hasInstanceId = probeDevice.Properties.TryGetValue<string>(
                        PropertyKeys.PKEY_Device_InstanceId, out string? instanceId);
                    Console.WriteLine($"        PKEY_Device_InstanceId -> present={hasInstanceId}, value='{instanceId}'");

                    PropertyStore propertyStore = probeDevice.Properties;
                    Console.WriteLine($"        property count   : {propertyStore.Count}");
                    for (int i = 0; i < propertyStore.Count; i++)
                    {
                        // The indexer THROWS CoreAudioException (0xE000020B) for blob-valued
                        // properties, so it must always be guarded. Reading the key is safe.
                        PropertyKey key = propertyStore.Get(i);
                        string value;
                        try
                        {
                            value = propertyStore[i].Value?.ToString() ?? "<null>";
                        }
                        catch (Exception ex)
                        {
                            value = $"<unreadable: {ex.GetType().Name} 0x{ex.HResult:X8}>";
                        }

                        Console.WriteLine($"            {key.formatId} pid={key.propertyId,-4} = {value}");
                    }
                }
            }

            // 4. Device mix formats and their buffer limits: the input that sizes the drift target.
            Console.WriteLine();
            Console.WriteLine("[4] Mix format + engine period (sizes the drift trough target)");
            foreach (AudioEndpointInfo endpoint in endpoints.Where(e => e.IsActive).Take(8))
            {
                if (!devices.TryResolveDevice(endpoint.EndpointId, out var device) || device is null)
                {
                    continue;
                }

                using (device)
                using (var client = device.CreateAudioClient())
                {
                    double defaultPeriodMs = client.DefaultDevicePeriod / 10000.0;
                    double minPeriodMs = client.MinimumDevicePeriod / 10000.0;

                    Console.WriteLine($"    {endpoint.FriendlyName}");
                    Console.WriteLine($"        mix      : {client.MixFormat.SampleRate} Hz, "
                                      + $"{client.MixFormat.Channels} ch, {client.MixFormat.BitsPerSample} bit");
                    Console.WriteLine($"        period   : default {defaultPeriodMs:0.00} ms, min {minPeriodMs:0.00} ms");
                    Console.WriteLine($"        client2/3: {client.SupportsAudioClient2}/{client.SupportsAudioClient3}");
                }
            }

            // 5. Notification API: constructing the client subscribes to the COM callbacks.
            Console.WriteLine();
            Console.WriteLine("[5] Device notifications");
            try
            {
                using var probe = new DeviceManager();
                Console.WriteLine("    OK: MMDeviceNotificationClient constructed (NAudio 3.x event API).");
                Console.WriteLine("        NOTE: IMMNotificationClient is internal in 3.x — do not use the 2.x pattern.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    !! failed: {ex.Message}");
                failures++;
            }

            // 6. Settings + compensation arithmetic against real measured-shaped data.
            Console.WriteLine();
            Console.WriteLine("[6] Settings + compensation model");
            var store = new ProfileStore();
            Console.WriteLine($"    settings path: {store.Path}");
            MultiBtSettings settings = store.Load(out string? warning);
            Console.WriteLine(warning is null
                ? "    OK: settings loaded (or defaults created) with no warning."
                : $"    warning: {warning}");

            CompensationTarget[] targets =
            [
                new("living-room-jbl", 185.0, Transport.Bluetooth),
                new("desk-sony", 240.0, Transport.Bluetooth),
                new("projector", 310.0, Transport.Hdmi),
            ];

            IReadOnlyDictionary<string, double> alignAll =
                LatencyModel.ComputeCompensations(targets, SyncMode.AlignAll);
            IReadOnlyDictionary<string, double> wiredOnly =
                LatencyModel.ComputeCompensations(targets, SyncMode.WiredOnly);

            Console.WriteLine("    AlignAll : "
                              + string.Join(", ", alignAll.Select(kv => $"{kv.Key}=+{kv.Value:0}ms")));
            Console.WriteLine("    WiredOnly: "
                              + string.Join(", ", wiredOnly.Select(kv => $"{kv.Key}=+{kv.Value:0}ms")));

            double systemAlignAll = LatencyModel.ComputeSystemLatencyMs(targets, alignAll, SyncMode.AlignAll);
            double systemWiredOnly = LatencyModel.ComputeSystemLatencyMs(targets, wiredOnly, SyncMode.WiredOnly);

            Console.WriteLine($"    system latency: AlignAll={systemAlignAll:0} ms "
                              + $"(lip-sync {(LatencyModel.ExceedsLipSyncThreshold(systemAlignAll) ? "BROKEN" : "ok")}), "
                              + $"WiredOnly={systemWiredOnly:0} ms "
                              + $"(lip-sync {(LatencyModel.ExceedsLipSyncThreshold(systemWiredOnly) ? "BROKEN" : "ok")})");

            // 7. Windows default endpoint switching (undocumented IPolicyConfig).
            //
            // SAFE BY CONSTRUCTION: it sets the CURRENT default device to itself, so nothing on the
            // machine actually changes. That is enough to prove the COM activation and the vtable
            // call work, which is the only part that can silently be wrong.
            Console.WriteLine();
            Console.WriteLine("[7] Default endpoint switching (IPolicyConfig)");

            if (devices.TryGetDefaultRenderDevice(out MMDevice? currentDefault) && currentDefault is not null)
            {
                string defaultId = currentDefault.ID;
                string defaultName = currentDefault.FriendlyName;
                currentDefault.Dispose();

                bool switched = DefaultEndpointSwitcher.TrySetDefault(defaultId, out string? switchError);

                Console.WriteLine($"    current default: {defaultName}");
                Console.WriteLine(switched
                    ? "    OK: SetDefaultEndpoint accepted the call (state unchanged - it was already default)."
                    : $"    FAIL: {switchError}");

                if (!switched)
                {
                    failures++;
                }
            }
            else
            {
                Console.WriteLine("    SKIP: no default render device.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"FATAL: {ex}");
            failures++;
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine(failures == 0
            ? "Result: all checks completed. Review the instance ids and transport labels above."
            : $"Result: {failures} check(s) reported a problem (see above).");

        return failures == 0 ? 0 : 1;
    }
}
