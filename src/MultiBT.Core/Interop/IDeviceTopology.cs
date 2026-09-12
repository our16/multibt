using System.Runtime.InteropServices;

namespace MultiBT.Core.Interop;

/// <summary>
/// COM interface for IDeviceTopology.
/// </summary>
[ComImport]
[Guid("2A07407E-6C8D-4C11-BF4C-71740EB43F9F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeviceTopology
{
    /// <summary>
    /// Gets the device interface path for this topology object.
    /// </summary>
    [PreserveSig]
    int GetDeviceId(out string deviceInterfacePath);

    /// <summary>
    /// Gets the number of connectors in this topology object.
    /// </summary>
    int GetConnectorCount();

    /// <summary>
    /// Gets a connector by index.
    /// </summary>
    IConnector GetConnector(int index);
}
