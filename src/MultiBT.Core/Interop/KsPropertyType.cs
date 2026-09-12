namespace MultiBT.Core.Interop;

/// <summary>
/// KSPROPERTY flags for the Flags member.
/// </summary>
[Flags]
public enum KsPropertyType : uint
{
    /// <summary>Get operation.</summary>
    Get = 0x00000000,

    /// <summary>Set operation.</summary>
    Set = 0x00000001,

    /// <summary>Base handler.</summary>
    BaseSupport = 0x00010000,

    /// <summary>Basic support.</summary>
    BasicSupport = 0x00020000,
}
