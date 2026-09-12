using System.Runtime.InteropServices;

namespace MultiBT.Core.Interop;

/// <summary>
/// KSEVENT structure for event operations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct KSEVENT
{
    /// <summary>Event set GUID.</summary>
    public Guid Set;

    /// <summary>Event identifier.</summary>
    public int Id;

    /// <summary>Event flags.</summary>
    public int Flags;
}
