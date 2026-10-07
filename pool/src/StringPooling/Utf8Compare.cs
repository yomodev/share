using System.Buffers;
using System.Text;

namespace StringPooling;

public static class Utf8Compare
{
    /// <summary>
    /// True if decoding <paramref name="utf8"/> with Encoding.UTF8 (replacement semantics) yields exactly <paramref name="s"/>.
    /// </summary>
    public static bool Equal(ReadOnlySpan<byte> utf8, string s)
    {
        // Decoding never produces more chars than input bytes.
        if (s.Length > utf8.Length) return false;

        // Fast path: pure ASCII, no transcoding.
        if (s.Length == utf8.Length && Ascii.Equals(utf8, s)) return true;

        // Slow path: non-ASCII, invalid sequences (e.g. one 0xFF byte becomes one U+FFFD char,
        // so equal lengths do NOT imply ASCII), or a rare hash collision between ASCII strings.
        return DecodeEquals(utf8, s);
    }

    private static bool DecodeEquals(ReadOnlySpan<byte> utf8, string s)
    {
        char[]? rented = null;
        Span<char> buf = stackalloc char[256];
        if (utf8.Length > buf.Length)
        {
            rented = ArrayPool<char>.Shared.Rent(utf8.Length);
            buf = rented;
        }
        try
        {
            int n = Encoding.UTF8.GetChars(utf8, buf);
            return n == s.Length && buf[..n].SequenceEqual(s);
        }
        finally
        {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        }
    }
}
