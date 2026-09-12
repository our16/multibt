using System.Runtime.InteropServices;

namespace MultiBT.Core.Interop;

/// <summary>
/// COM interface for IPart.
/// </summary>
[ComImport]
[Guid("AE2B88CE-1043-4C5D-ACD4-8C1A27C690F2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPart
{
    /// <summary>
    /// Gets the name of this part.
    /// </summary>
    void GetName(out string name);

    /// <summary>
    /// Gets the local id of this part.
    /// </summary>
    void GetLocalId(out uint localId);

    /// <summary>
    /// Gets the global id of this part.
    /// </summary>
    void GetGlobalId(out string globalId);

    /// <summary>
    /// Gets the part type.
    /// </summary>
    void GetPartType(out PartType partType);

    /// <summary>
    /// Gets the sub-type for this part.
    /// </summary>
    void GetSubType(out Guid subType);

    /// <summary>
    /// Gets the control interface count for this part.
    /// </summary>
    int GetControlInterfaceCount();

    /// <summary>
    /// Gets a control interface by index.
    /// </summary>
    void GetControlInterface(int index, out IControlInterface controlInterface);

    /// <summary>
    /// Gets the topology object that this part belongs to.
    /// </summary>
    IDeviceTopology GetTopologyObject();

    /// <summary>
    /// Activates the given interface on this part.
    /// </summary>
    void Activate(uint classContext, ref Guid interfaceId, out IntPtr interfacePointer);

    /// <summary>
    /// Registers a control change callback.
    /// </summary>
    void RegisterControlChangeNotify([MarshalAs(UnmanagedType.IUnknown)] object notify);

    /// <summary>
    /// Unregisters a control change callback.
    /// </summary>
    void UnregisterControlChangeNotify([MarshalAs(UnmanagedType.IUnknown)] object notify);
}

/// <summary>
/// Part type enumeration.
/// </summary>
public enum PartType
{
    /// <summary>Unknown part type.</summary>
    Other = 0,

    /// <summary>Connector part.</summary>
    Connector = 1,

    /// <summary>Subunit part.</summary>
    Subunit = 2,
}

/// <summary>
/// COM interface for IControlInterface.
/// </summary>
[ComImport]
[Guid("D794360C-34F8-4ED0-A3EC-12DF57E2F4E4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IControlInterface
{
    /// <summary>
    /// Gets the name of this control interface.
    /// </summary>
    void GetName(out string name);

    /// <summary>
    /// Gets the local id of this control interface.
    /// </summary>
    void GetLocalId(out uint localId);

    /// <summary>
    /// Gets the global id of this control interface.
    /// </summary>
    void GetGlobalId(out string globalId);

    /// <summary>
    /// Gets the control interface type.
    /// </summary>
    void GetControlInterfaceType(out Guid controlInterfaceType);

    /// <summary>
    /// Gets the sub-type for this control interface.
    /// </summary>
    void GetControlInterfaceSubType(out Guid controlInterfaceSubType);

    /// <summary>
    /// Determines if this control interface supports a given interface.
    /// </summary>
    bool IsControlInterfaceSupported([MarshalAs(UnmanagedType.LPStruct)] Guid iid);

    /// <summary>
    /// Gets the topology object that this control interface belongs to.
    /// </summary>
    IDeviceTopology GetTopologyObject();
}
