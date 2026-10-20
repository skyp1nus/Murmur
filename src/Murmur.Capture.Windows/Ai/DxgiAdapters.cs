using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Ai;

/// <summary>
/// Lists the DXGI adapters (<c>CreateDXGIFactory1</c> + <c>IDXGIFactory::EnumAdapters</c>) with raw vtable calls like the
/// rest of the capture code (no RCWs, Native AOT safe). Never throws: any failure is "no adapters".
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal static unsafe partial class DxgiAdapters
{
    private static readonly Guid IidIDXGIFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");

    /// <summary>DXGI_ERROR_NOT_FOUND: <c>EnumAdapters</c> ran past the last adapter.</summary>
    private const int NotFound = unchecked((int)0x887A0002);

    /// <summary>Safety stop; a PC does not have this many adapters.</summary>
    private const uint MaxAdapters = 16;

    public static IReadOnlyList<GpuAdapterInfo> Enumerate()
    {
        var result = new List<GpuAdapterInfo>(2);
        nint factory = 0;
        try
        {
            var iid = IidIDXGIFactory1;
            if (CreateDXGIFactory1(&iid, &factory) < 0 || factory == 0)
            {
                return result;
            }

            for (uint index = 0; index < MaxAdapters; index++)
            {
                nint adapter = 0;
                var hr = IDXGIFactory.EnumAdapters(factory, index, &adapter);
                if (hr == NotFound || hr < 0 || adapter == 0)
                {
                    break;
                }

                try
                {
                    DxgiAdapterDesc desc;
                    if (IDXGIAdapter.GetDesc(adapter, &desc) >= 0)
                    {
                        result.Add(new GpuAdapterInfo(
                            Describe(desc.Description),
                            desc.VendorId,
                            (long)Math.Min(desc.DedicatedVideoMemory, long.MaxValue)));
                    }
                }
                finally
                {
                    ComCall.Release(ref adapter);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or SEHException)
        {
            // No DXGI (Server Core, a stripped image): the PC offers the CPU build and, through the loader check, Vulkan.
        }
        finally
        {
            ComCall.Release(ref factory);
        }

        return result;
    }

    private static string Describe(char* description)
    {
        var span = new ReadOnlySpan<char>(description, DxgiAdapterDesc.DescriptionChars);
        var end = span.IndexOf('\0');
        return (end < 0 ? span : span[..end]).Trim().ToString();
    }

    /// <summary><c>HRESULT CreateDXGIFactory1(REFIID riid, void **ppFactory)</c>.</summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* riid, nint* factory);
}

internal static unsafe class IDXGIFactory
{
    // HRESULT EnumAdapters(UINT Adapter, IDXGIAdapter **ppAdapter): IUnknown 0..2, IDXGIObject 3..6 (SetPrivateData,
    // SetPrivateDataInterface, GetPrivateData, GetParent), then IDXGIFactory: EnumAdapters = slot 7.
    public static int EnumAdapters(nint self, uint adapter, nint* result) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, nint*, int>)ComCall.Vtbl(self)[7])(self, adapter, result);
}
