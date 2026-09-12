using System.Runtime.InteropServices;

namespace MultiBT.Core.Interop;

/// <summary>
/// COM interface for KS property, method, and event operations.
/// </summary>
[ComImport]
[Guid("28F78BE1-5D75-11D2-9AEE-0020AFD7986F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IKsControl
{
    /// <summary>
    /// Gets or sets a property on a KS filter or pin.
    /// </summary>
    int KsProperty(
        ref KSPROPERTY property,
        uint propertyLength,
        out IntPtr propertyData,
        uint dataLength);

    /// <summary>
    /// Gets or sets a method on a KS filter or pin.
    /// </summary>
    int KsMethod(
        ref KSMETHOD method,
        uint methodLength,
        out IntPtr methodData,
        uint dataLength);

    /// <summary>
    /// Gets or sets an event on a KS filter or pin.
    /// </summary>
    int KsEvent(
        ref KSEVENT ksevent,
        uint eventLength,
        out IntPtr eventData,
        uint dataLength);
}
