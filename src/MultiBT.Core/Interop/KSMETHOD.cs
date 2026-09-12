using System.Runtime.InteropServices;

namespace MultiBT.Core.Interop;

/// <summary>
/// KSMETHOD structure for method operations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct KSMETHOD
{
    /// <summary>Method set GUID.</summary>
    public Guid Set;

    /// <summary>Method identifier.</summary>
    public int Id;

    /// <summary>Method flags.</summary>
    public int Flags;
}
