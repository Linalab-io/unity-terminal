using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Linalab.Terminal.Editor
{
    public enum TerminalShellProfile
    {
        Auto = 0,
        Zsh = 1,
        Bash = 2,
        Custom = 3
    }

    public static class TerminalSettings
    {
        const string Prefix = "Linalab.Terminal.";
        const string FontSizeKey = Prefix + "FontSize";
        const string FontFamilyKey = Prefix + "FontFamily";
        const string ScrollbackLimitKey = Prefix + "ScrollbackLimit";
        const string ShellProfileKey = Prefix + "ShellProfile";
        const string ShellPathOverrideKey = Prefix + "ShellPathOverride";
        const string CursorBlinkRateKey = Prefix + "CursorBlinkRate";
        const string VerboseLoggingKey = Prefix + "VerboseLogging";
        const string TmuxAutoAttachKey = Prefix + "TmuxAutoAttach";
        const string TmuxSessionNameOverrideKey = Prefix + "TmuxSessionNameOverride";

        public static int FontSize
        {
            get => EditorPrefs.GetInt(FontSizeKey, 13);
            set => EditorPrefs.SetInt(FontSizeKey, Mathf.Clamp(value, 8, 32));
        }

        public static string FontFamily
        {
            get => EditorPrefs.GetString(FontFamilyKey, string.Empty);
            set => EditorPrefs.SetString(FontFamilyKey, value ?? string.Empty);
        }

        public static string GetEffectiveFontFamily()
        {
            var fontFamily = FontFamily;
            if (!string.IsNullOrWhiteSpace(fontFamily))
            {
                return fontFamily;
            }

            var ghosttyFontFamily = TerminalThemeResolver.GetGhosttyFontFamily();
            if (!string.IsNullOrWhiteSpace(ghosttyFontFamily))
            {
                return ghosttyFontFamily;
            }

            if (ShellProfile == TerminalShellProfile.Zsh || ResolveShellPath().EndsWith("zsh", StringComparison.OrdinalIgnoreCase))
            {
                return InferFontFamilyFromZsh();
            }

            return string.Empty;
        }

        public static int ScrollbackLimit
        {
            get => EditorPrefs.GetInt(ScrollbackLimitKey, 5000);
            set => EditorPrefs.SetInt(ScrollbackLimitKey, Mathf.Clamp(value, 100, 50000));
        }

        public static TerminalShellProfile ShellProfile
        {
            get => (TerminalShellProfile)EditorPrefs.GetInt(ShellProfileKey, (int)TerminalShellProfile.Auto);
            set => EditorPrefs.SetInt(ShellProfileKey, (int)value);
        }

        public static string ShellPathOverride
        {
            get => EditorPrefs.GetString(ShellPathOverrideKey, string.Empty);
            set => EditorPrefs.SetString(ShellPathOverrideKey, value ?? string.Empty);
        }

        public static float CursorBlinkRate
        {
            get => EditorPrefs.GetFloat(CursorBlinkRateKey, 0.53f);
            set => EditorPrefs.SetFloat(CursorBlinkRateKey, Mathf.Clamp(value, 0.1f, 2f));
        }

        public static bool VerboseLogging
        {
            get => EditorPrefs.GetBool(VerboseLoggingKey, false);
            set => EditorPrefs.SetBool(VerboseLoggingKey, value);
        }

        public static bool ToggleVerboseLogging()
        {
            VerboseLogging = !VerboseLogging;
            return VerboseLogging;
        }

        public static bool TmuxAutoAttach
        {
            get => EditorPrefs.GetBool(TmuxAutoAttachKey, false);
            set => EditorPrefs.SetBool(TmuxAutoAttachKey, value);
        }

        public static bool ToggleTmuxAutoAttach()
        {
            TmuxAutoAttach = !TmuxAutoAttach;
            return TmuxAutoAttach;
        }

        public static string TmuxSessionNameOverride
        {
            get => EditorPrefs.GetString(TmuxSessionNameOverrideKey, string.Empty);
            set => EditorPrefs.SetString(TmuxSessionNameOverrideKey, value ?? string.Empty);
        }

        public static string GetCanonicalTmuxSessionName()
        {
            return BuildTmuxSessionName(GetProjectRootDirectory());
        }

        public static string GetTmuxSessionName()
        {
            var overrideName = TmuxSessionNameOverride;
            if (!string.IsNullOrWhiteSpace(overrideName))
            {
                return overrideName;
            }

            return GetCanonicalTmuxSessionName();
        }

        public static string BuildTmuxSessionName(string projectRoot)
        {
            string normalized = string.IsNullOrWhiteSpace(projectRoot)
                ? string.Empty
                : NormalizeDirectoryPath(projectRoot);

            string directoryName = string.IsNullOrEmpty(normalized)
                ? string.Empty
                : new DirectoryInfo(normalized).Name;

            string sanitized = SanitizeTmuxSessionName(directoryName);
            return string.IsNullOrEmpty(sanitized) ? "unity-terminal" : sanitized;
        }

        static string SanitizeTmuxSessionName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('_');
                }
            }

            return builder.ToString();
        }

        public static string ResolveShellPath()
        {
            return ShellProfile switch
            {
                TerminalShellProfile.Zsh => "/bin/zsh",
                TerminalShellProfile.Bash => "/bin/bash",
                TerminalShellProfile.Custom when !string.IsNullOrWhiteSpace(ShellPathOverride) => ShellPathOverride,
                _ => ShellProcess.DetectShell()
            };
        }

        public static string GetProjectRootDirectory()
        {
            var applicationDataDirectory = NormalizeDirectoryPath(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)));
            var dataPathRoot = FindUnityWorkspaceRoot(applicationDataDirectory);
            if (!string.IsNullOrWhiteSpace(dataPathRoot))
            {
                return dataPathRoot;
            }

            if (!string.IsNullOrWhiteSpace(applicationDataDirectory))
            {
                return applicationDataDirectory;
            }

            return string.Empty;
        }

        static string InferFontFamilyFromZsh()
        {
            var zshrcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zshrc");
            if (File.Exists(zshrcPath))
            {
                var zshrc = File.ReadAllText(zshrcPath);
                if (zshrc.Contains("oh-my-posh", StringComparison.OrdinalIgnoreCase)
                    || zshrc.Contains("powerlevel", StringComparison.OrdinalIgnoreCase)
                    || zshrc.Contains("nerd font", StringComparison.OrdinalIgnoreCase))
                {
                    return "MesloLGS NF";
                }
            }

            return string.Empty;
        }

        static string FindUnityWorkspaceRoot(string startDirectory)
        {
            var normalizedStartDirectory = NormalizeDirectoryPath(startDirectory);
            if (string.IsNullOrWhiteSpace(normalizedStartDirectory))
            {
                return string.Empty;
            }

            for (var directory = new DirectoryInfo(normalizedStartDirectory); directory != null; directory = directory.Parent)
            {
                if (IsUnityWorkspaceRoot(directory.FullName))
                {
                    return directory.FullName;
                }
            }

            return string.Empty;
        }

        static string NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        static bool IsUnityWorkspaceRoot(string directory)
        {
            return Directory.Exists(Path.Combine(directory, "Assets"))
                && Directory.Exists(Path.Combine(directory, "Packages"))
                && Directory.Exists(Path.Combine(directory, "ProjectSettings"));
        }

    }
}
