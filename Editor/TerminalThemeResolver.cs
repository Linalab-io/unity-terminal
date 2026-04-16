using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Linalab.Terminal.Editor
{
    public sealed class TerminalTheme
    {
        public Color[] Palette { get; }
        public Color DefaultForeground { get; }
        public Color DefaultBackground { get; }
        public Color CursorColor { get; }

        public TerminalTheme(Color[] palette, Color defaultForeground, Color defaultBackground, Color cursorColor)
        {
            Palette = palette;
            DefaultForeground = defaultForeground;
            DefaultBackground = defaultBackground;
            CursorColor = cursorColor;
        }
    }

    public static class TerminalThemeResolver
    {
        static readonly string[] GhosttyConfigPaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "com.mitchellh.ghostty", "config.ghostty"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "com.mitchellh.ghostty", "config"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ghostty", "config.ghostty"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ghostty", "config")
        };

        static TerminalTheme _cachedTheme;
        static string _cachedSignature;

        public static TerminalTheme GetCurrentTheme()
        {
            var signature = BuildSignature();
            if (_cachedTheme != null && string.Equals(_cachedSignature, signature, StringComparison.Ordinal))
            {
                return _cachedTheme;
            }

            _cachedSignature = signature;
            _cachedTheme = ResolveTheme();
            return _cachedTheme;
        }

        public static string GetGhosttyFontFamily()
        {
            var configPath = GetGhosttyConfigPath();
            if (string.IsNullOrEmpty(configPath))
            {
                return string.Empty;
            }

            var lines = File.ReadAllLines(configPath);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                if (!string.Equals(key, "font-family", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return line.Substring(separatorIndex + 1).Trim().Trim('"', '\'');
            }

            return string.Empty;
        }

        static TerminalTheme ResolveTheme()
        {
            return new TerminalTheme(AnsiPalette.BuildDefaultPalette(), AnsiPalette.FallbackDefaultForeground, AnsiPalette.FallbackDefaultBackground, AnsiPalette.FallbackCursorColor);
        }

        static string BuildSignature()
        {
            return "default-theme";
        }

        static TerminalTheme TryLoadGhosttyTheme()
        {
            var configPath = GetGhosttyConfigPath();
            if (string.IsNullOrEmpty(configPath))
            {
                return null;
            }

            var themeData = new GhosttyThemeData();
            ParseGhosttyConfigFile(configPath, themeData, parseThemeDirective: true);

            if (!string.IsNullOrEmpty(themeData.ThemeName))
            {
                var themePath = ResolveGhosttyThemePath(themeData.ThemeName);
                if (!string.IsNullOrEmpty(themePath))
                {
                    ParseGhosttyConfigFile(themePath, themeData, parseThemeDirective: false);
                    ParseGhosttyConfigFile(configPath, themeData, parseThemeDirective: false);
                }
            }

            return themeData.BuildTheme();
        }

        static string GetGhosttyConfigPath()
        {
            for (var i = 0; i < GhosttyConfigPaths.Length; i++)
            {
                if (File.Exists(GhosttyConfigPaths[i]))
                {
                    return GhosttyConfigPaths[i];
                }
            }

            return string.Empty;
        }

        static string ResolveGhosttyThemePath(string themeName)
        {
            var fileName = themeName.EndsWith(".theme", StringComparison.OrdinalIgnoreCase) ? themeName : $"{themeName}.theme";
            var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] candidatePaths =
            {
                Path.Combine(homeDirectory, ".config", "ghostty", "themes", fileName),
                Path.Combine("/Applications", "Ghostty.app", "Contents", "Resources", "ghostty", "themes", fileName),
                Path.Combine(homeDirectory, "Applications", "Ghostty.app", "Contents", "Resources", "ghostty", "themes", fileName)
            };

            for (var i = 0; i < candidatePaths.Length; i++)
            {
                if (File.Exists(candidatePaths[i]))
                {
                    return candidatePaths[i];
                }
            }

            return string.Empty;
        }

        static void ParseGhosttyConfigFile(string path, GhosttyThemeData data, bool parseThemeDirective)
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim();
                if (parseThemeDirective && string.Equals(key, "theme", StringComparison.OrdinalIgnoreCase))
                {
                    data.ThemeName = value;
                    continue;
                }

                data.Apply(key, value);
            }
        }

        sealed class GhosttyThemeData
        {
            readonly Color[] _palette = AnsiPalette.BuildDefaultPalette();

            public string ThemeName { get; set; }
            public Color? Foreground { get; private set; }
            public Color? Background { get; private set; }
            public Color? CursorColor { get; private set; }

            public void Apply(string key, string value)
            {
                if (TryParsePalette(key, value, out var index, out Color paletteColor))
                {
                    if (index >= 0 && index < _palette.Length)
                    {
                        _palette[index] = paletteColor;
                    }

                    return;
                }

                if (!TryParseColor(value, out Color color))
                {
                    return;
                }

                if (string.Equals(key, "foreground", StringComparison.OrdinalIgnoreCase))
                {
                    Foreground = color;
                }
                else if (string.Equals(key, "background", StringComparison.OrdinalIgnoreCase))
                {
                    Background = color;
                }
                else if (string.Equals(key, "cursor-color", StringComparison.OrdinalIgnoreCase))
                {
                    CursorColor = color;
                }
            }

            public TerminalTheme BuildTheme()
            {
                return new TerminalTheme(
                    _palette,
                    Foreground ?? AnsiPalette.FallbackDefaultForeground,
                    Background ?? AnsiPalette.FallbackDefaultBackground,
                    CursorColor ?? AnsiPalette.FallbackCursorColor);
            }

            static bool TryParsePalette(string key, string value, out int index, out Color color)
            {
                index = -1;
                color = default;

                if (string.Equals(key, "palette", StringComparison.OrdinalIgnoreCase))
                {
                    var separatorIndex = value.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        return false;
                    }

                    return int.TryParse(value.Substring(0, separatorIndex).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                        && TryParseColor(value.Substring(separatorIndex + 1).Trim(), out color);
                }

                if (key.StartsWith("color", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(key.Substring("color".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                {
                    return TryParseColor(value, out color);
                }

                return false;
            }

            static bool TryParseColor(string value, out Color color)
            {
                var normalized = value.Trim().Trim('"', '\'');
                if (normalized.StartsWith("#", StringComparison.Ordinal))
                {
                    normalized = normalized.Substring(1);
                }

                if (normalized.Length != 6 || !uint.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                {
                    color = default;
                    return false;
                }

                color = new Color(
                    ((rgb >> 16) & 0xFF) / 255f,
                    ((rgb >> 8) & 0xFF) / 255f,
                    (rgb & 0xFF) / 255f,
                    1f);
                return true;
            }
        }
    }
}
