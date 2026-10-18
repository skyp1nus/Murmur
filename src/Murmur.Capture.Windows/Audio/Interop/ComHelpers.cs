using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Murmur.Capture.Windows.Audio.Interop;

/// <summary>RCW creation/release and HRESULT checks shared by the WASAPI code.</summary>
internal static class ComHelpers
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>
    /// Wraps an interface pointer returned through an out-parameter into a unique RCW and drops the out-parameter's
    /// reference (the RCW holds its own). Release the result with <see cref="Release"/>.
    /// </summary>
    public static T Wrap<T>(nint pointer, string what)
        where T : class
    {
        if (pointer == 0)
        {
            throw new COMException($"{what} returned a null interface pointer.");
        }

        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>Releases the native object behind a unique RCW now instead of at finalization.</summary>
    public static void Release(object? rcw)
    {
        if (rcw is ComObject com)
        {
            com.FinalRelease();
        }
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new COMException($"{what} failed: {Describe(hr)}", hr);
        }
    }

    public static string Describe(int hr) => hr switch
    {
        WasapiConstants.AudclntEDeviceInvalidated => "AUDCLNT_E_DEVICE_INVALIDATED (0x88890004): the device was removed or disabled",
        WasapiConstants.AudclntEServiceNotRunning => "AUDCLNT_E_SERVICE_NOT_RUNNING (0x88890010): the Windows Audio service is not running",
        WasapiConstants.AudclntEResourcesInvalidated => "AUDCLNT_E_RESOURCES_INVALIDATED (0x88890026)",
        WasapiConstants.ENotFound => "E_NOTFOUND (0x80070490): no such device",
        WasapiConstants.EAccessDenied => "E_ACCESSDENIED (0x80070005): access denied",
        _ => $"HRESULT 0x{hr:X8}",
    };

    /// <summary>
    /// Joins the multithreaded apartment for the current thread. S_FALSE (already initialized) and
    /// RPC_E_CHANGED_MODE (the thread is an STA, e.g. a UI thread; COM is still usable) are accepted.
    /// Dispose to balance a successful call with CoUninitialize.
    /// </summary>
    public static ComApartmentScope EnterMta() => new(NativeMethods.CoInitializeEx(0, WasapiConstants.CoinitMultithreaded));

    internal readonly struct ComApartmentScope : IDisposable
    {
        private readonly bool _mustUninitialize;

        public ComApartmentScope(int hr)
        {
            if (hr < 0 && hr != WasapiConstants.RpcEChangedMode)
            {
                Check(hr, "CoInitializeEx(COINIT_MULTITHREADED)");
            }

            _mustUninitialize = hr >= 0;
        }

        public void Dispose()
        {
            if (_mustUninitialize)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }
}
