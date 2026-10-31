namespace Murmur.Capture.Windows.Video;

/// <summary>
/// Asking a Media Engine for a playback rate other than 1 can be refused (MF_E_UNSUPPORTED_RATE: some codecs and the software
/// renderer do not do 2x). A refused rate is not a failure of the video: the engine goes back to real time and the audio clock
/// keeps the picture in step by seeking.
/// </summary>
internal static class PlaybackRateRequest
{
    /// <summary>
    /// Sets the default rate and then the current rate through <paramref name="setDefault"/> / <paramref name="set"/> (each returns an
    /// HRESULT). When one is refused, both are set back to 1.
    /// </summary>
    /// <returns>The HRESULT of the refusal (negative), or 0 when the rate was accepted.</returns>
    public static int Apply(double rate, Func<double, int> setDefault, Func<double, int> set)
    {
        var hr = setDefault(rate);
        if (hr >= 0)
        {
            hr = set(rate);
        }

        if (hr < 0 && rate != 1)
        {
            setDefault(1);
            set(1);
        }

        return hr < 0 ? hr : 0;
    }
}
