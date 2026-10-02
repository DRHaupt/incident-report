using System.Speech.Synthesis;

namespace FieldMap.Services;

/// <summary>Spoken directions with the Windows text-to-speech voices installed on the laptop (works offline).</summary>
public sealed class Speech : IDisposable
{
    private SpeechSynthesizer? _synth;
    private bool _failed;

    public bool Available
    {
        get
        {
            Ensure();
            return _synth != null;
        }
    }

    private void Ensure()
    {
        if (_synth != null || _failed)
            return;
        try
        {
            var s = new SpeechSynthesizer();
            s.SetOutputToDefaultAudioDevice();
            var english = s.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (english != null)
                s.SelectVoice(english.VoiceInfo.Name);
            s.Rate = 0;
            s.Volume = 100;
            _synth = s;
        }
        catch (Exception)
        {
            _failed = true; // no audio device or no voices: directions stay on screen only
        }
    }

    public void Say(string text)
    {
        Ensure();
        if (_synth == null)
            return;
        try
        {
            _synth.SpeakAsyncCancelAll();
            _synth.SpeakAsync(text);
        }
        catch (Exception)
        {
            // Speech is a convenience; never let it break navigation.
        }
    }

    public void Stop()
    {
        try
        {
            _synth?.SpeakAsyncCancelAll();
        }
        catch (Exception)
        {
        }
    }

    public void Dispose() => _synth?.Dispose();
}
