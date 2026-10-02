using System.Media;

namespace FieldToolkit.Tools.Cpr;

/// <summary>Short synthesized sounds for the metronome (no audio files to ship; works offline).</summary>
internal sealed class ClickSounds : IDisposable
{
    private readonly SoundPlayer _click = new(Tone(1400, 0.035, 0.9));
    private readonly SoundPlayer _breaths = new(Tone(700, 0.35, 0.8));

    public ClickSounds()
    {
        _click.Load();
        _breaths.Load();
    }

    public void Click() => _click.Play();

    public void Breaths() => _breaths.Play();

    public void Dispose()
    {
        _click.Dispose();
        _breaths.Dispose();
    }

    /// <summary>A 16-bit mono PCM WAV of a sine tone with a fast decay.</summary>
    private static MemoryStream Tone(double frequency, double seconds, double volume)
    {
        const int rate = 44100;
        var samples = (int)(rate * seconds);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + samples * 2);
        w.Write("WAVEfmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            var t = i / (double)rate;
            var envelope = Math.Exp(-t / (seconds / 4));
            w.Write((short)(Math.Sin(2 * Math.PI * frequency * t) * envelope * volume * short.MaxValue));
        }
        ms.Position = 0;
        return ms;
    }
}
