using System.Runtime.InteropServices;
using System.Text;

namespace Murmur.Transcription.Native;

/// <summary>
/// Owns NUL-terminated UTF-8 copies of managed strings for the duration of one native call (config structs).
/// sherpa-onnx copies every config string into a std::string, so the copies can be freed right after Create*.
/// sherpa-onnx opens model files through narrow <c>std::string</c> / <c>std::ifstream</c> paths. On Linux/macOS
/// those are UTF-8 natively. On Windows MSVC interprets narrow paths in the process ANSI code page, so a UTF-8 path
/// such as <c>C:\Users\Владислав\...</c> is correct only because <c>app.manifest</c> sets
/// <c>activeCodePage=UTF-8</c> (Windows 10 1903+). UNVERIFIED-ON-WINDOWS: confirm with the win-x64 CI run or a
/// manual run with a Cyrillic model directory.
/// </summary>
internal sealed unsafe class NativeUtf8 : IDisposable
{
    private readonly List<nint> _allocations = [];

    /// <summary>Returns a pointer valid until <see cref="Dispose"/>; <c>null</c> maps to a NULL pointer.</summary>
    public byte* Add(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        var p = (byte*)NativeMemory.Alloc((nuint)byteCount + 1);
        _allocations.Add((nint)p);
        var written = Encoding.UTF8.GetBytes(value, new Span<byte>(p, byteCount));
        p[written] = 0;
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _allocations)
        {
            NativeMemory.Free((void*)p);
        }

        _allocations.Clear();
    }

    /// <summary>Decodes a NUL-terminated UTF-8 string owned by native code; NULL becomes "".</summary>
    public static string ToManaged(byte* s) =>
        s is null ? string.Empty : Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(s));
}
