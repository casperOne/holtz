using System.Globalization;
using System.Security.Cryptography;

namespace Holtz;

/// <summary>Raised when the audio, the tooling, or the server cannot do the job asked.</summary>
public sealed class TranscribeException(string message) : Exception(message);

/// <summary>Number and time formatting. Always invariant: a comma decimal in an ffmpeg
/// argument or a timestamp is a silent wrong answer on a non-English Windows.</summary>
public static class Fmt
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Shortest round-trip form, like Python's <c>{x:g}</c> for the values used here
    /// (-40 not -40.0, 0.35 not 0.350).</summary>
    public static string G(double value) => value.ToString(Inv);

    public static string F(double value, int decimals) =>
        value.ToString("F" + decimals.ToString(Inv), Inv);

    /// <summary>Seconds to <c>14m10s</c>, for clip filenames.</summary>
    public static string Stamp(double seconds)
    {
        var total = (long)Math.Round(seconds);
        return $"{total / 60:00}m{total % 60:00}s";
    }

    /// <summary>Seconds to <c>MM:SS</c>, which is how a reader cites the tape.</summary>
    public static string Clock(double seconds)
    {
        var total = (long)Math.Round(seconds);
        return $"{total / 60:00}:{total % 60:00}";
    }

    /// <summary>Seconds to <c>MM:SS</c>, or <c>H:MM:SS</c> past an hour.</summary>
    public static string Time(double seconds)
    {
        var total = (long)Math.Round(seconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes:00}:{secs:00}";
    }

    /// <summary>Seconds to <c>HH:MM:SS.mmm</c>. Hours always written, so a long file sorts.</summary>
    public static string VttTimestamp(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var millis = (long)Math.Round(seconds * 1000);
        var hours = millis / 3_600_000;
        millis %= 3_600_000;
        var minutes = millis / 60_000;
        millis %= 60_000;
        var secs = millis / 1000;
        millis %= 1000;
        return $"{hours:00}:{minutes:00}:{secs:00}.{millis:000}";
    }

    public static string Percent(double fraction) => (fraction * 100).ToString("0", Inv) + "%";

    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>Collapse runs of whitespace, as Python's <c>" ".join(s.split())</c>.</summary>
    public static string Squash(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Write text as UTF-8 without a BOM and with <c>\n</c> line endings on every OS,
    /// so a file written on Windows diffs cleanly against one written anywhere else.</summary>
    public static void WriteText(string path, string text) =>
        File.WriteAllText(path, text.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));
}
