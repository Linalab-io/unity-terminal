using System;
using UnityEngine;

namespace Linalab.Terminal.Editor
{
    [Flags]
    public enum CellFlags : byte
    {
        None = 0,
        Bold = 1 << 0,
        Dim = 1 << 1,
        Italic = 1 << 2,
        Underline = 1 << 3,
        Blink = 1 << 4,
        Inverse = 1 << 5,
        Hidden = 1 << 6,
        Strikethrough = 1 << 7
    }

    public readonly struct TerminalColor : IEquatable<TerminalColor>
    {
        public enum Kind : byte
        {
            Default,
            Named,
            Indexed,
            Rgb
        }

        public readonly Kind ColorKind;
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;

        TerminalColor(Kind kind, byte r, byte g, byte b)
        {
            ColorKind = kind;
            R = r;
            G = g;
            B = b;
        }

        public static readonly TerminalColor DefaultColor = new(Kind.Default, 0, 0, 0);

        public static TerminalColor Named(byte index) => new(Kind.Named, index, 0, 0);

        public static TerminalColor Indexed(byte index) => new(Kind.Indexed, index, 0, 0);

        public static TerminalColor FromRgb(byte r, byte g, byte b) => new(Kind.Rgb, r, g, b);

        public Color ToUnityColor(Color[] palette, Color defaultColor)
        {
            return ColorKind switch
            {
                Kind.Default => defaultColor,
                Kind.Named => R < palette.Length ? palette[R] : defaultColor,
                Kind.Indexed => R < palette.Length ? palette[R] : defaultColor,
                Kind.Rgb => new Color(R / 255f, G / 255f, B / 255f, 1f),
                _ => defaultColor
            };
        }

        public bool Equals(TerminalColor other)
        {
            return ColorKind == other.ColorKind && R == other.R && G == other.G && B == other.B;
        }

        public override bool Equals(object obj) => obj is TerminalColor other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(ColorKind, R, G, B);

        public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);

        public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);
    }

    public struct TerminalCell
    {
        public char Codepoint;
        public TerminalColor Foreground;
        public TerminalColor Background;
        public CellFlags Flags;

        public static readonly TerminalCell Empty = new()
        {
            Codepoint = ' ',
            Foreground = TerminalColor.DefaultColor,
            Background = TerminalColor.DefaultColor,
            Flags = CellFlags.None
        };
    }

    public struct CursorState
    {
        public int Row;
        public int Col;
        public bool Visible;
        public bool PendingWrap;

        public static CursorState Create()
        {
            return new CursorState
            {
                Row = 0,
                Col = 0,
                Visible = true,
                PendingWrap = false
            };
        }

        public void Clamp(int rows, int cols)
        {
            Row = Math.Clamp(Row, 0, Math.Max(0, rows - 1));
            Col = Math.Clamp(Col, 0, Math.Max(0, cols - 1));
        }
    }

    public static class AnsiPalette
    {
        static Color[] _palette;

        public static Color[] Colors => _palette ??= BuildPalette();

        static Color[] BuildPalette()
        {
            var palette = new Color[256];
            palette[0] = FromHex(0x000000);
            palette[1] = FromHex(0xCD3131);
            palette[2] = FromHex(0x0DBC79);
            palette[3] = FromHex(0xE5E510);
            palette[4] = FromHex(0x2472C8);
            palette[5] = FromHex(0xBC3FBC);
            palette[6] = FromHex(0x11A8CD);
            palette[7] = FromHex(0xE5E5E5);
            palette[8] = FromHex(0x666666);
            palette[9] = FromHex(0xF14C4C);
            palette[10] = FromHex(0x23D18B);
            palette[11] = FromHex(0xF5F543);
            palette[12] = FromHex(0x3B8EEA);
            palette[13] = FromHex(0xD670D6);
            palette[14] = FromHex(0x29B8DB);
            palette[15] = FromHex(0xFFFFFF);

            for (int i = 0; i < 216; i++)
            {
                int r = i / 36;
                int g = (i / 6) % 6;
                int b = i % 6;
                palette[16 + i] = new Color(
                    r == 0 ? 0f : (55 + (40 * r)) / 255f,
                    g == 0 ? 0f : (55 + (40 * g)) / 255f,
                    b == 0 ? 0f : (55 + (40 * b)) / 255f,
                    1f);
            }

            for (int i = 0; i < 24; i++)
            {
                float value = (8 + (10 * i)) / 255f;
                palette[232 + i] = new Color(value, value, value, 1f);
            }

            return palette;
        }

        static Color FromHex(int hex)
        {
            return new Color(
                ((hex >> 16) & 0xFF) / 255f,
                ((hex >> 8) & 0xFF) / 255f,
                (hex & 0xFF) / 255f,
                1f);
        }

        public static readonly Color DefaultForeground = FromHex(0xCCCCCC);
        public static readonly Color DefaultBackground = FromHex(0x1E1E2E);
        public static readonly Color CursorColor = FromHex(0xF8F8F2);
    }
}
