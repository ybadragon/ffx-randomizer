using System.Text;

namespace FfxRandomizer.Core.GameData.Text;

/// <summary>
/// Decodes FFX's in-game text encoding to plain strings.
/// The character table comes from the game itself (jppc/ffx_encoding/ffxsjistbl_XX.bin): a UTF-8 file whose
/// n-th character is the glyph for code n + 0x30. Codes beyond one byte are written as a section byte
/// 0x2B-0x2F followed by a low byte, giving code (section - 0x2B) * 0xD0 + low; a 0x04 prefix shifts the next
/// code up by 0x410. Bytes below 0x30 are control codes, which <see cref="Decode"/> drops.
/// </summary>
public sealed class FfxCharset
{
    private const int FirstCode = 0x30;
    private const int SectionStart = 0x2B;
    private const int SectionEnd = 0x2F;
    private const int SectionSize = 0xD0;
    private const int ExtendedPrefix = 0x04;
    private const int ExtendedShift = 0x410;
    private const int LineBreak = 0x03;
    private const int PlayerName = 0x13;

    private readonly string _glyphs;

    public FfxCharset(string glyphs) => _glyphs = glyphs;

    /// <summary>Builds a charset from the bytes of an ffxsjistbl_XX.bin file.</summary>
    public static FfxCharset FromTableFile(byte[] tableFile)
    {
        string glyphs = Encoding.UTF8.GetString(tableFile);
        if (glyphs.Length > 0 && glyphs[0] == '﻿') glyphs = glyphs[1..];
        return new FfxCharset(glyphs);
    }

    /// <summary>Path of the character table for a language folder code such as "us", "de" or "jp".</summary>
    public static string TablePath(string language) =>
        $"{GamePaths.Master}jppc/ffx_encoding/ffxsjistbl_{CharsetFor(language)}.bin";

    /// <summary>The Latin languages share the "us" table; Japanese, Chinese and Korean have their own.</summary>
    public static string CharsetFor(string language) => language switch
    {
        "jp" or "ch" or "kr" => language,
        _ => "us",
    };

    /// <summary>
    /// Decodes the zero-terminated string at <paramref name="offset"/>. Control codes are dropped, except line
    /// breaks (which become spaces) and player-character name codes (which are replaced through
    /// <paramref name="playerName"/> when given). Returns null when no visible text remains.
    /// </summary>
    public string? Decode(ReadOnlySpan<byte> data, int offset, Func<int, string?>? playerName = null)
    {
        if ((uint)offset >= (uint)data.Length) return null;

        var text = new StringBuilder();
        int shift = 0;
        int i = offset;
        while (i < data.Length && data[i] != 0)
        {
            int b = data[i++];
            int currentShift = shift;
            shift = 0;

            if (b >= FirstCode)
            {
                AppendGlyph(text, b + currentShift);
            }
            else if (b >= SectionStart && b <= SectionEnd)
            {
                if (i >= data.Length) break;
                AppendGlyph(text, (b - SectionStart) * SectionSize + data[i++] + currentShift);
            }
            else if (b == ExtendedPrefix)
            {
                shift = ExtendedShift;
            }
            else if (b == LineBreak)
            {
                text.Append(' ');
            }
            else if (TakesArgument(b))
            {
                if (i >= data.Length) break;
                int argument = data[i++];
                if (b == PlayerName && playerName is not null && argument >= FirstCode)
                    text.Append(playerName(argument - FirstCode));
            }
        }
        return Clean(text);
    }

    /// <summary>
    /// Encodes plain text (the inverse of <see cref="Decode"/> for visible characters, no terminator). A character the
    /// font lacks falls back to its fullwidth form (the Japanese, Chinese and Korean fonts have fullwidth Latin only);
    /// one with neither throws, so missing glyphs are caught when the text is written rather than shown as blanks.
    /// </summary>
    public byte[] Encode(string text)
    {
        var bytes = new List<byte>();
        foreach (char c in text)
        {
            int index = _glyphs.IndexOf(c);
            if (index < 0 && Fullwidth(c) is { } wide) index = _glyphs.IndexOf(wide);
            if (index < 0) throw new ArgumentException($"The game font has no glyph for '{c}'.", nameof(text));
            int code = index + FirstCode;
            if (code >= ExtendedShift + FirstCode)
            {
                bytes.Add(ExtendedPrefix);
                code -= ExtendedShift;
            }
            int section = (code - FirstCode) / SectionSize;
            if (section == 0) bytes.Add((byte)code);
            else
            {
                bytes.Add((byte)(SectionStart + section));
                bytes.Add((byte)(code - section * SectionSize));
            }
        }
        return [.. bytes];
    }

    /// <summary>Whether every character of <paramref name="text"/> can be encoded.</summary>
    public bool CanEncode(string text) =>
        text.All(c => _glyphs.Contains(c) || (Fullwidth(c) is { } wide && _glyphs.Contains(wide)));

    private static char? Fullwidth(char c) => c switch
    {
        ' ' => '　',
        >= '!' and <= '~' => (char)(c - '!' + '！'),
        _ => null,
    };

    /// <summary>Control bytes that stand alone; every other byte below 0x30 is followed by one argument byte.</summary>
    private static bool TakesArgument(int controlByte) =>
        controlByte is not (0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x0C or 0x0F or 0x11);

    private void AppendGlyph(StringBuilder text, int code)
    {
        int index = code - FirstCode;
        if ((uint)index < (uint)_glyphs.Length && !char.IsControl(_glyphs[index]))
            text.Append(_glyphs[index]);
    }

    private static string? Clean(StringBuilder text)
    {
        var result = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text.ToString())
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace) result.Append(' ');
            pendingSpace = false;
            result.Append(c);
        }
        return result.Length == 0 ? null : result.ToString();
    }
}
