using System.Runtime.InteropServices;

namespace MultiBT.Core.Devices;

/// <summary>
/// Changes the Windows default audio endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Uses the <b>undocumented</b> <c>IPolicyConfig</c> COM interface — the same one the Sound control
/// panel and every "switch default device" utility uses. It has been stable from Windows 7 through
/// 11, needs no elevation and no packaging, but it is not a contract Microsoft supports. Every call
/// is therefore defensive and the caller must remain correct when it fails.
/// </para>
/// <para>
/// <b>Why MultiBT needs it:</b> the mirror captures what the SOURCE endpoint is <i>rendering</i>,
/// and Windows only plays system audio to the DEFAULT endpoint. So selecting a primary device
/// without also making it the Windows default yields silence on every output — the most confusing
/// failure this app can present, and one that looks exactly like broken audio code. Switching the
/// default together with the primary removes that failure mode entirely.
/// </para>
/// </remarks>
public static class DefaultEndpointSwitcher
{
    private static readonly ERole[] AllRoles = [ERole.eConsole, ERole.eMultimedia, ERole.eCommunications];

    /// <summary>
    /// Makes the given endpoint the Windows default for all three roles.
    /// </summary>
    /// <param name="endpointId">The WASAPI endpoint id to make default.</param>
    /// <param name="error">Set to a description when the call fails; otherwise null.</param>
    /// <returns><c>true</c> when every role was switched.</returns>
    /// <remarks>
    /// All three roles are switched together to match what Windows itself does when the user picks a
    /// device in Settings. Switching only <c>eMultimedia</c> would leave a device that is default for
    /// music but not for system sounds, which reads as "sometimes it works".
    /// </remarks>
    public static bool TrySetDefault(string endpointId, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(endpointId))
        {
            error = "empty endpoint id";
            return false;
        }

        object? client = null;

        try
        {
            client = new CPolicyConfigClient();
            var policy = (IPolicyConfig)client;

            foreach (ERole role in AllRoles)
            {
                int hr = policy.SetDefaultEndpoint(endpointId, role);

                if (hr != 0)
                {
                    error = $"SetDefaultEndpoint({role}) failed: 0x{hr:X8}";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            // COM activation or the vtable call itself failed. Never fatal: switching the Windows
            // default is a convenience on top of the mirror, not a requirement for it.
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (client is not null && Marshal.IsComObject(client))
            {
                Marshal.ReleaseComObject(client);
            }
        }
    }

    /// <summary>Audio endpoint roles. Values must match the native <c>ERole</c> order.</summary>
    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2,
    }

    /// <summary>
    /// The in-box implementation of <c>IPolicyConfig</c>.
    /// </summary>
    /// <remarks>Not a documented coclass; resolved by CLSID.</remarks>
    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private sealed class CPolicyConfigClient
    {
    }

    /// <summary>
    /// <c>IPolicyConfig</c> — undocumented.
    /// </summary>
    /// <remarks>
    /// <b>The member order is the vtable order and MUST NOT be changed.</b> COM dispatches by slot,
    /// so reordering or omitting a method silently calls the wrong function. Every preceding member
    /// has to be declared even though this project only uses <c>SetDefaultEndpoint</c>, and each is
    /// marked <c>PreserveSig</c> so HRESULTs come back as return values rather than exceptions.
    /// </remarks>
    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, out IntPtr format);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out IntPtr format);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr endpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ref long period);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, out IntPtr mode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool fxStore, ref IntPtr key, out IntPtr value);

        [PreserveSig]
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool fxStore, ref IntPtr key, ref IntPtr value);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ERole role);

        [PreserveSig]
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }
}
