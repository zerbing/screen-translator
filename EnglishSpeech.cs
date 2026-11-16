using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace ScreenTranslator;

internal sealed class EnglishSpeech : IDisposable
{
    private const int Async = 1;
    private const int PurgeBeforeSpeak = 2;
    private const int PlainText = 16;
    private object? voice;

    internal static bool IsAvailable()
    {
        try
        {
            var speaker = CreateEnglishVoice();
            if (speaker is null)
                return false;
            Marshal.ReleaseComObject(speaker);
            return true;
        }
        catch (Exception error) when (error is COMException or RuntimeBinderException)
        {
            return false;
        }
    }

    public bool Speak(string text)
    {
        try
        {
            voice ??= CreateEnglishVoice();
            if (voice is null)
                return false;

            ((dynamic)voice).Speak(text, Async | PurgeBeforeSpeak | PlainText);
            return true;
        }
        catch (Exception error) when (error is COMException or RuntimeBinderException)
        {
            Dispose();
            return false;
        }
    }

    private static object? CreateEnglishVoice()
    {
        var speechType = Type.GetTypeFromProgID("SAPI.SpVoice");
        if (speechType is null || Activator.CreateInstance(speechType) is not { } speaker)
            return null;

        try
        {
            dynamic engine = speaker;
            dynamic voices = engine.GetVoices("Language=409", "");
            if (voices.Count > 0)
            {
                engine.Voice = voices.Item(0);
                return speaker;
            }

            // SAPI stores the voice language as a hexadecimal Windows locale identifier.
            voices = engine.GetVoices("", "");
            for (var i = 0; i < (int)voices.Count; i++)
            {
                dynamic candidate = voices.Item(i);
                string languages = candidate.GetAttribute("Language");
                if (languages.Split(';').Any(language => int.TryParse(language,
                    System.Globalization.NumberStyles.HexNumber, null, out var locale) && (locale & 0x3FF) == 9))
                {
                    engine.Voice = candidate;
                    return speaker;
                }
            }
        }
        catch
        {
            Marshal.ReleaseComObject(speaker);
            throw;
        }

        Marshal.ReleaseComObject(speaker);
        return null;
    }

    public void Dispose()
    {
        if (voice is not null)
        {
            Marshal.ReleaseComObject(voice);
            voice = null;
        }
    }
}
