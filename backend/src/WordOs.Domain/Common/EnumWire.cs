using System.Text;

namespace WordOs.Domain.Common;

/// <summary>
/// Converts an enum name to the SCREAMING_SNAKE value the REST contract uses.
/// </summary>
/// <remarks>
/// <c>ToString().ToUpperInvariant()</c> is not enough and is quietly wrong for
/// every multi-word name: <c>LetterTiles</c> becomes <c>LETTERTILES</c>, not
/// <c>LETTER_TILES</c>. A client matching on the documented value would simply
/// never match, and nothing would throw — a test caught it.
///
/// Storage is separate: EF persists the C# name via <c>HasConversion&lt;string&gt;</c>,
/// which round-trips. This is only for what crosses the wire.
/// </remarks>
public static class EnumWire
{
    public static string ToWire<TEnum>(this TEnum value) where TEnum : struct, Enum =>
        ToScreamingSnake(value.ToString()!);

    public static string ToScreamingSnake(string name)
    {
        var builder = new StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c)) builder.Append('_');
            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }
}
