namespace MultiBT.Core.Devices;

/// <summary>
/// Exception thrown when a device is not found or has been removed.
/// </summary>
public sealed class DeviceNotFoundException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DeviceNotFoundException"/> class.</summary>
    public DeviceNotFoundException()
        : base("The device was not found or has been removed.")
    {
    }

    /// <summary>Initializes a new instance with a message.</summary>
    /// <param name="message">The error message.</param>
    public DeviceNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public DeviceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
