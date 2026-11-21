using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace PoE1Access;

/// <summary>
/// An interpolated phrase kept as its composite format and arguments, so a translation can
/// reorder them: $"Life below {pct} percent" is "Life below {0} percent" with one argument.
/// Taken from the Path of Exile 2 accessibility mod. See <see cref="Loc"/>.
/// </summary>
[InterpolatedStringHandler]
public sealed class Phrase
{
    private readonly StringBuilder _format;
    private readonly object[] _arguments;
    private int _count;

    public Phrase(int literalLength, int formattedCount)
    {
        _format = new StringBuilder(literalLength + formattedCount * 4);
        _arguments = new object[formattedCount];
    }

    /// <summary>The composite format the phrase tables are keyed by: "Life below {0} percent".</summary>
    public string Format => _format.ToString();

    public void AppendLiteral(string text) => _format.Append(text.Replace("{", "{{").Replace("}", "}}"));

    public void AppendFormatted<T>(T value) => Hole(value, null, null);

    public void AppendFormatted<T>(T value, string format) => Hole(value, null, format);

    public void AppendFormatted<T>(T value, int alignment) => Hole(value, alignment, null);

    public void AppendFormatted<T>(T value, int alignment, string format) => Hole(value, alignment, format);

    /// <summary>The phrase in the given composite format (a translation of <see cref="Format"/>), else as written.</summary>
    public string Text(string translated = null)
    {
        if (translated != null)
        {
            try { return string.Format(translated, _arguments); }
            catch (FormatException) { }   // a broken translation falls back to the English phrase
        }
        return string.Format(Format, _arguments);
    }

    private void Hole(object value, int? alignment, string holeFormat)
    {
        _format.Append('{').Append(_count);
        if (alignment is int width) _format.Append(',').Append(width);
        if (!string.IsNullOrEmpty(holeFormat)) _format.Append(':').Append(holeFormat);
        _format.Append('}');
        _arguments[_count++] = value;
    }
}
