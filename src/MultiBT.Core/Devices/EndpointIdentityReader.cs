using NAudio.CoreAudioApi;

namespace MultiBT.Core.Devices;

/// <summary>What could be recovered about an endpoint's hardware identity.</summary>
/// <param name="InstanceId">Normalised PnP instance id, or empty when nothing usable was found.</param>
/// <param name="InterfacePath">Normalised device interface path, or empty.</param>
/// <param name="Source">Which property the instance id came from, for diagnostics.</param>
public sealed record EndpointIdentity(string InstanceId, string InterfacePath, string Source)
{
    public bool HasInstanceId => !string.IsNullOrEmpty(InstanceId);
}

/// <summary>
/// Recovers an endpoint's hardware identity, with fallbacks for the properties that are actually
/// populated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists.</b> Reading <c>PKEY_Device_InstanceId</c> is the obvious approach and
/// it does not work. Measured on Windows 11 24H2 (build 26100), that property is <b>absent from
/// the endpoint property store</b>: enumeration returns 20 render endpoints and
/// <c>PKEY_Device_InstanceId</c> is missing on every one of them, so a classifier keyed on it
/// silently degrades and reports every device as <c>Other</c> — including the Bluetooth ones this
/// app exists to target.
/// </para>
/// <para>
/// The instance id IS present, under <c>PKEY_Device_ControllerDeviceId</c>
/// (<c>{b3f8fa53-0004-438e-9003-51a46e139bfc}, 2</c>), but wrapped in a <c>{N}.</c> prefix — e.g.
/// <c>{1}.HDAUDIO\FUNC_01&amp;VEN_10EC&amp;DEV_0897&amp;...\4&amp;29ACFEC2&amp;0&amp;0001</c>.
/// The device interface path appears under pid 11 of the same format id, and both use Windows
/// device-path syntax where <c>#</c> replaces <c>\</c> and a <c>\\?\</c> prefix may be present:
/// <c>{2}.\\?\hdaudio#func_01&amp;ven_10ec&amp;dev_0897&amp;...#...{6994ad04-...}\rearlineoutwave3</c>.
/// </para>
/// <para>
/// So prefix matching on raw values fails in two independent ways (the <c>{N}.</c> prefix and the
/// <c>#</c> separators), and both are handled here rather than at each call site.
/// See docs/PITFALLS.md D3.
/// </para>
/// <para>
/// <b>The property store indexer throws.</b> <c>PropertyStore[i]</c> raises
/// <c>CoreAudioException 0xE000020B</c> for blob-valued properties (several per endpoint), so
/// every read here is guarded. Only <see cref="PropertyStore.TryGetValue{T}"/> with a matching
/// type is safe unconditionally.
/// </para>
/// </remarks>
public static class EndpointIdentityReader
{
    /// <summary><c>PKEY_Device_InstanceId</c>. Documented, but frequently absent in practice.</summary>
    private static readonly PropertyKey InstanceIdKey =
        new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    /// <summary>
    /// <c>PKEY_Device_ControllerDeviceId</c>. Named for the controller, but on this platform its
    /// value is the endpoint's own device instance id (with a <c>{N}.</c> prefix).
    /// </summary>
    private static readonly PropertyKey ControllerDeviceIdKey =
        new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);

    /// <summary>Device interface path, same format id, pid 11.</summary>
    private static readonly PropertyKey DeviceInterfacePathKey =
        new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 11);

    /// <summary><c>PKEY_Device_InterfaceKey</c>. Another interface-path source.</summary>
    private static readonly PropertyKey InterfaceKey =
        new(new Guid("233164c8-1b2c-4c7d-bc68-b671687a2567"), 1);

    /// <summary>
    /// Normalised prefix of the MMDEVAPI software-device wrapper. Every endpoint carries one of
    /// these, and it says nothing about the transport, so it must never win over a hardware path.
    /// </summary>
    private const string MmDevApiWrapperPrefix = "SWD\\MMDEVAPI\\";

    /// <summary>Reads the endpoint identity, trying each property source in order of usefulness.</summary>
    public static EndpointIdentity Read(MMDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        string controllerId = Normalize(ReadString(device, ControllerDeviceIdKey));
        string instanceId = Normalize(ReadString(device, InstanceIdKey));
        string interfacePath = Normalize(ReadString(device, DeviceInterfacePathKey));
        string interfaceKey = Normalize(ReadString(device, InterfaceKey));

        string bestPath = FirstHardwarePath(interfacePath, interfaceKey);
        string bestInstanceId = FirstHardwarePath(instanceId, controllerId);

        return new EndpointIdentity(
            bestInstanceId,
            bestPath,
            !string.IsNullOrEmpty(bestInstanceId)
                ? (IsHardwarePath(instanceId) ? "PKEY_Device_InstanceId" : "PKEY_Device_ControllerDeviceId")
                : "<none>");
    }

    /// <summary>
    /// Normalises a Windows device identifier into a form that can be prefix-matched.
    /// </summary>
    /// <remarks>
    /// Strips the <c>\\?\</c> prefix and the <c>{N}.</c> ordinal prefix, and converts device-path
    /// <c>#</c> separators to <c>\</c>, so that <c>{2}.\?\bthenum#...</c> becomes
    /// <c>bthenum\...</c> and can be detected.
    /// </remarks>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string value = raw.Trim();

        // Strip a leading "{N}." ordinal and/or a "\\?\" extended-path prefix.
        //
        // This needs a loop rather than a single ordered pass: in real values the ordinal comes
        // FIRST and the extended prefix is hidden behind it — "{2}.\\?\hdaudio#..." — so
        // checking for "\\?\" only at the start of the raw value never matches.
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;

            if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                value = value[4..];
                changed = true;
            }

            if (TryStripOrdinalPrefix(value, out string stripped))
            {
                value = stripped;
                changed = true;
            }

            if (!changed)
            {
                break;
            }
        }

        // Device interface paths use '#' where instance ids use '\'.
        value = value.Replace('#', '\\');

        return value.Trim();
    }

    /// <summary>
    /// Strips a leading <c>{N}.</c> ordinal, accepting it only when the braces contain digits —
    /// so a value that legitimately begins with a GUID is left intact.
    /// </summary>
    private static bool TryStripOrdinalPrefix(string value, out string stripped)
    {
        stripped = value;

        if (!value.StartsWith('{'))
        {
            return false;
        }

        int close = value.IndexOf('}');
        if (close < 2 || close + 1 >= value.Length || value[close + 1] != '.')
        {
            return false;
        }

        for (int i = 1; i < close; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }
        }

        stripped = value[(close + 2)..];
        return true;
    }

    /// <summary>Whether a normalised value describes real hardware rather than the MMDEVAPI wrapper.</summary>
    public static bool IsHardwarePath(string? normalized) =>
        !string.IsNullOrEmpty(normalized)
        && !normalized.StartsWith(MmDevApiWrapperPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the first candidate that describes real hardware.</summary>
    private static string FirstHardwarePath(params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (IsHardwarePath(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>Reads a string property, tolerating a missing key or an unreadable value.</summary>
    private static string? ReadString(MMDevice device, PropertyKey key)
    {
        try
        {
            return device.Properties.TryGetValue<string>(key, out string? value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
