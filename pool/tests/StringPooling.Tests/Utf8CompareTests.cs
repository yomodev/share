using StringPooling;

namespace StringPooling.Tests;

public class Utf8CompareTests
{
    [Fact] public void Equal_ascii() => Assert.True(Utf8Compare.Equal("abc"u8, "abc"));
    [Fact] public void Different_ascii_same_length() => Assert.False(Utf8Compare.Equal("abc"u8, "abd"));
    [Fact] public void String_longer_than_bytes() => Assert.False(Utf8Compare.Equal("a"u8, "ab"));
    [Fact] public void Equal_non_ascii() => Assert.True(Utf8Compare.Equal("héllo"u8, "héllo"));
    [Fact] public void Different_non_ascii() => Assert.False(Utf8Compare.Equal("héllo"u8, "hello"));
    [Fact] public void Two_byte_char_vs_one_char_string() => Assert.False(Utf8Compare.Equal("é"u8, "e"));

    [Fact]
    public void Invalid_byte_equals_replacement_char()
    {
        // 0xFF decodes to U+FFFD: same length as the input, yet not ASCII.
        Assert.True(Utf8Compare.Equal(new byte[] { 0xFF }, "\uFFFD"));
        Assert.False(Utf8Compare.Equal(new byte[] { 0xFF }, "?"));
    }

    [Fact]
    public void Long_non_ascii_uses_rented_buffer()
    {
        var s = new string('é', 500);
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        Assert.True(Utf8Compare.Equal(bytes, s));
        Assert.False(Utf8Compare.Equal(bytes, new string('é', 499) + "e"));
    }
}
