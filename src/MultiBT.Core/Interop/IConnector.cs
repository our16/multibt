using System.Runtime.InteropServices;

namespace MultiBT.Core.Interop;

/// <summary>
/// COM interface for IConnector.
/// </summary>
[ComImport]
[Guid("9c2c6094-341c-43ec-8C15-C7F393D5CEB4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IConnector
{
    /// <summary>
    /// Gets the type of this connector.
    /// </summary>
    ConnectorType GetType();

    /// <summary>
    /// Gets the data flow direction of this connector.
    /// </summary>
    NAudio.CoreAudioApi.DataFlow GetDataFlow();

    /// <summary>
    /// Determines if this connector is connected.
    /// </summary>
    bool IsConnected();

    /// <summary>
    /// Gets the part connected to this connector.
    /// </summary>
    IPart GetConnectedTo();

    /// <summary>
    /// Gets the connector id.
    /// </summary>
    void GetConnectorIdGuid(out Guid connectorId);

    /// <summary>
    /// Gets the device id associated with this connector.
    /// </summary>
    void GetDeviceIdConnectedTo(out string deviceId);
}

/// <summary>
/// Connector type enumeration.
/// </summary>
public enum ConnectorType
{
    /// <summary>Unknown connector type.</summary>
    Unknown = 0,

    /// <summary>Physical jack.</summary>
    Physical = 1,

    /// <summary>Software device.</summary>
    SoftwareDevice = 2,

    /// <summary>Internal connector.</summary>
    Internal = 3,
}
