using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Audio.Interop;

/// <summary>wtypes.h <c>PROPERTYKEY { GUID fmtid; DWORD pid; }</c>: 20 bytes, 4-byte aligned.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid formatId, uint propertyId)
{
    public readonly Guid FormatId = formatId;
    public readonly uint PropertyId = propertyId;
}

/// <summary>
/// PropIdl.h <c>PROPVARIANT</c>: <c>VARTYPE vt; WORD wReserved1..3;</c> followed by a union whose largest arms are
/// two pointers (<c>BLOB</c>, <c>CA*</c>) or a <c>DECIMAL</c> that overlays the whole struct. 24 bytes on 64-bit
/// Windows (16 on x86); the value starts at offset 8. Only <c>VT_LPWSTR</c> is read here; always free with
/// <see cref="NativeMethods.PropVariantClear"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort VarType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Value;
    public nint Value2;
}
