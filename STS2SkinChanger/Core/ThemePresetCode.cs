using System.Buffers.Binary;
using System.Security.Cryptography;

namespace STS2SkinChanger.Core;

// SCT1 is a frozen wire schema: 14 RGB colors, 15 IEEE float32 values,
// 8 signed bytes, 1 boolean, then a four-byte SHA-256 checksum. All numbers
// are little-endian. Future fields require a new version, never reordering V1.
internal static class ThemePresetCode
{
    public const string Prefix = "SCT1.";
    public const int CodeLength = 159;
    public const int MaxInputLength = 2048;
    private const int PayloadLength = 111;
    private const int PacketLength = PayloadLength + 4;

    public static bool LooksLikeCode(string? text)
    {
        var value = (text ?? "").TrimStart();
        if (!value.StartsWith("SCT", StringComparison.OrdinalIgnoreCase)) return false;
        var i = 3;
        while (i < value.Length && value[i] is >= '0' and <= '9') i++;
        return i > 3 && i < value.Length && value[i] == '.';
    }

    public static string Encode(ModThemeSettings settings)
    {
        var s = settings.Normalize();
        var data = new byte[PacketLength];
        var offset = 0;
        void C(string color)
        {
            Convert.FromHexString(color[1..]).CopyTo(data, offset);
            offset += 3;
        }
        void F(float value) { BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value); offset += 4; }
        void I(int value) => data[offset++] = unchecked((byte)(sbyte)value);
        C(s.PanelColor);
        C(s.SelectionColor);
        C(s.SelectionHoverColor);
        C(s.ButtonColor);
        C(s.HoverColor);
        C(s.DropdownColor);
        C(s.DropdownHoverColor);
        C(s.DropdownSelectionColor);
        C(s.DropdownSelectionHoverColor);
        C(s.DropdownBorderColor);
        C(s.TextColor);
        C(s.AccentColor);
        C(s.BorderColor);
        C(s.TextShadowColor);
        F(s.PanelOpacity);
        F(s.PanelBlur);
        F(s.SelectionOpacity);
        F(s.SelectionBlur);
        F(s.SelectionHoverOpacity);
        F(s.SelectionHoverBlur);
        F(s.ButtonOpacity);
        F(s.ButtonBlur);
        F(s.DropdownOpacity);
        F(s.DropdownBlur);
        F(s.DropdownHoverOpacity);
        F(s.DropdownSelectionOpacity);
        F(s.DropdownSelectionHoverOpacity);
        F(s.FontScale);
        F(s.TextShadowOpacity);
        I(s.DropdownBorderWidth);
        I(s.DropdownCornerRadius);
        I(s.BorderWidth);
        I(s.CornerRadius);
        I(s.TextOutline);
        I(s.TextShadowOffsetX);
        I(s.TextShadowOffsetY);
        I(s.TextShadowSize);
        data[offset] = s.TextShadowEnabled ? (byte)1 : (byte)0;
        SHA256.HashData(data.AsSpan(0, PayloadLength)).AsSpan(0, 4).CopyTo(data.AsSpan(PayloadLength));
        return Prefix + Base64(data);
    }

    public static ModThemeSettings Decode(string? text)
    {
        if (text == null || text.Length > MaxInputLength) throw InvalidCode();
        var code = (text ?? "").Trim();
        if (code.Length != CodeLength || !code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw InvalidCode();
        var body = code[Prefix.Length..];
        if (body.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw InvalidCode();
        byte[] data;
        try { data = Convert.FromBase64String(body.Replace('-', '+').Replace('_', '/') + "=="); }
        catch (FormatException) { throw InvalidCode(); }
        if (data.Length != PacketLength || Base64(data) != body ||
            !SHA256.HashData(data.AsSpan(0, PayloadLength)).AsSpan(0, 4).SequenceEqual(data.AsSpan(PayloadLength)) ||
            data[PayloadLength - 1] > 1) throw InvalidCode();
        var offset = 0;
        string C() { var result = "#" + Convert.ToHexString(data.AsSpan(offset, 3)); offset += 3; return result; }
        float F() { var result = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset)); offset += 4; return result; }
        int I() => unchecked((sbyte)data[offset++]);
        var result = new ModThemeSettings
        {
            PanelColor = C(),
            SelectionColor = C(),
            SelectionHoverColor = C(),
            ButtonColor = C(),
            HoverColor = C(),
            DropdownColor = C(),
            DropdownHoverColor = C(),
            DropdownSelectionColor = C(),
            DropdownSelectionHoverColor = C(),
            DropdownBorderColor = C(),
            TextColor = C(),
            AccentColor = C(),
            BorderColor = C(),
            TextShadowColor = C(),
            PanelOpacity = F(),
            PanelBlur = F(),
            SelectionOpacity = F(),
            SelectionBlur = F(),
            SelectionHoverOpacity = F(),
            SelectionHoverBlur = F(),
            ButtonOpacity = F(),
            ButtonBlur = F(),
            DropdownOpacity = F(),
            DropdownBlur = F(),
            DropdownHoverOpacity = F(),
            DropdownSelectionOpacity = F(),
            DropdownSelectionHoverOpacity = F(),
            FontScale = F(),
            TextShadowOpacity = F(),
            DropdownBorderWidth = I(),
            DropdownCornerRadius = I(),
            BorderWidth = I(),
            CornerRadius = I(),
            TextOutline = I(),
            TextShadowOffsetX = I(),
            TextShadowOffsetY = I(),
            TextShadowSize = I(),
            TextShadowEnabled = data[offset] == 1
        };
        // Import only the documented parameter ranges; never silently turn a
        // malformed float, boolean or out-of-range field into another theme.
        if (result.Normalize() != result) throw InvalidCode();
        return result;
    }

    private static string Base64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static FormatException InvalidCode() => new("Invalid, damaged or unsupported theme preset code.");
}
