namespace Murmur.App.Controls;

/// <summary>A bookmark flag above the waveform: where it stands (a fraction of the width, the waveform's own time-to-x mapping) and what its tooltip says.</summary>
/// <param name="Fraction">Position in [0, 1].</param>
/// <param name="TimeText">"12:41" (Geist Mono in the tooltip).</param>
/// <param name="Note">The note; empty when there is none.</param>
public readonly record struct WaveformFlag(double Fraction, string TimeText, string Note);
