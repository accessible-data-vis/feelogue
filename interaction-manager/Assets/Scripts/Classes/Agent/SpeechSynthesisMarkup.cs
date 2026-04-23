using System;
using System.Security;

public static class SpeechSynthesisMarkup
{
    public static string Wrap(string text)
    {
        return $"<speak>{text}</speak>";
    }

    public static string Pause(int milliseconds)
    {
        return $"<break time=\"{milliseconds}ms\"/>";
    }

    public static string Emphasize(string text, string level)
    {
        return $"<emphasis level=\"{level}\">{SecurityElement.Escape(text)}</emphasis>";
    }

    public static string Rate(string text, string speed)
    {
        return $"<prosody rate=\"{speed}\">{SecurityElement.Escape(text)}</prosody>";
    }

    // Settings-aware formatters
    public static string FormatNumber(string number, SpeechSettings settings)
    {
        if (settings == null) return number;

        return settings.numberPronunciation switch
        {
            NumberStyle.Cardinal => $"<say-as interpret-as=\"cardinal\">{SecurityElement.Escape(number)}</say-as>",
            NumberStyle.Digits => $"<say-as interpret-as=\"characters\">{SecurityElement.Escape(number)}</say-as>",
            _ => number
        };
    }

    public static string FormatDataValue(string value, SpeechSettings settings)
    {
        if (settings == null) return value;

        if (settings.emphasizeValues)
            return Emphasize(value, settings.emphasisLevel);

        return value;
    }
}
