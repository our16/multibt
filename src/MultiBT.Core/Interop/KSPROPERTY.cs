using System.Runtime.InteropServices;

namespace MultiBT.Core.Interop;

/// <summary>
/// KSPROPERTY structure for property operations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct KSPROPERTY
{
    /// <summary>Property set GUID (KSPROPSETID_BtAudio).</summary>
    public Guid Set;

    /// <summary>Property identifier.</summary>
    public int Id;

    /// <summary>Property flags.</summary>
    public int Flags;
}
