using System;
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

        static readonly TerminalTheme BuiltInTheme = new(
            AnsiPalette.BuildDefaultPalette(),
            AnsiPalette.FallbackDefaultForeground,
            AnsiPalette.FallbackDefaultBackground,
            AnsiPalette.FallbackCursorColor);

        public static TerminalTheme GetCurrentTheme() => BuiltInTheme;

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
    }
}
