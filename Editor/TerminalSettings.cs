using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    public enum TerminalAppProfile
    {
        SystemDefault = 0,
        Ghostty = 1,
        Terminal = 2,
        ITerm = 3,
        Warp = 4,
        WezTerm = 5,
        Alacritty = 6,
        Kitty = 7
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
        const string AutoAttachTmuxKey = Prefix + "AutoAttachTmux";
        const string TerminalAppProfileKey = Prefix + "TerminalAppProfile";

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
            string fontFamily = FontFamily;
            if (!string.IsNullOrWhiteSpace(fontFamily))
            {
                return fontFamily;
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

        public static bool AutoAttachTmux
        {
            get => EditorPrefs.GetBool(AutoAttachTmuxKey, false);
            set => EditorPrefs.SetBool(AutoAttachTmuxKey, value);
        }

        public static TerminalAppProfile TerminalApp
        {
            get => (TerminalAppProfile)EditorPrefs.GetInt(TerminalAppProfileKey, (int)DetectDefaultInstalledTerminalApp());
            set => EditorPrefs.SetInt(TerminalAppProfileKey, (int)value);
        }

        public static TerminalAppProfile DetectDefaultInstalledTerminalApp()
        {
            return IsTerminalAppInstalled(TerminalAppProfile.Ghostty)
                ? TerminalAppProfile.Ghostty
                : TerminalAppProfile.SystemDefault;
        }

        public static TerminalAppProfile[] GetInstalledTerminalApps()
        {
            var candidates = new[]
            {
                TerminalAppProfile.SystemDefault,
                TerminalAppProfile.Ghostty,
                TerminalAppProfile.Terminal,
                TerminalAppProfile.ITerm,
                TerminalAppProfile.Warp,
                TerminalAppProfile.WezTerm,
                TerminalAppProfile.Alacritty,
                TerminalAppProfile.Kitty
            };

            var installed = new System.Collections.Generic.List<TerminalAppProfile>(candidates.Length);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] == TerminalAppProfile.SystemDefault || IsTerminalAppInstalled(candidates[i]))
                {
                    installed.Add(candidates[i]);
                }
            }

            return installed.ToArray();
        }

        public static string GetTerminalAppDisplayName(TerminalAppProfile profile)
        {
            return profile switch
            {
                TerminalAppProfile.SystemDefault => "System Default",
                TerminalAppProfile.Ghostty => "Ghostty",
                TerminalAppProfile.Terminal => "Terminal.app",
                TerminalAppProfile.ITerm => "iTerm",
                TerminalAppProfile.Warp => "Warp",
                TerminalAppProfile.WezTerm => "WezTerm",
                TerminalAppProfile.Alacritty => "Alacritty",
                TerminalAppProfile.Kitty => "Kitty",
                _ => profile.ToString()
            };
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
            string applicationDataDirectory = NormalizeDirectoryPath(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)));
            string dataPathRoot = FindUnityWorkspaceRoot(applicationDataDirectory);
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

        public static string GetTmuxSessionName(string workspaceDirectory)
        {
            string source = string.IsNullOrWhiteSpace(workspaceDirectory)
                ? "unity-terminal"
                : Path.GetFileName(workspaceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (string.IsNullOrWhiteSpace(source))
            {
                source = "unity-terminal";
            }

            var builder = new StringBuilder(source.Length);
            foreach (char character in source)
            {
                builder.Append(char.IsLetterOrDigit(character) || character == '-' || character == '_' ? char.ToLowerInvariant(character) : '-');
            }

            string sanitized = builder.ToString().Trim('-');
            string baseName = string.IsNullOrEmpty(sanitized) ? "unity-terminal" : sanitized;
            string normalizedPath = string.IsNullOrWhiteSpace(workspaceDirectory) ? baseName : Path.GetFullPath(workspaceDirectory).ToLowerInvariant();

            using var sha1 = SHA1.Create();
            byte[] hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath));
            var hex = new StringBuilder(hashBytes.Length * 2);
            for (int i = 0; i < hashBytes.Length; i++)
            {
                hex.Append(hashBytes[i].ToString("x2"));
            }

            string suffix = hex.ToString(0, 8);
            return $"{baseName}-{suffix}";
        }

        static string InferFontFamilyFromZsh()
        {
            string zshrcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zshrc");
            if (File.Exists(zshrcPath))
            {
                string zshrc = File.ReadAllText(zshrcPath);
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
            string normalizedStartDirectory = NormalizeDirectoryPath(startDirectory);
            if (string.IsNullOrWhiteSpace(normalizedStartDirectory))
            {
                return string.Empty;
            }

            for (DirectoryInfo directory = new DirectoryInfo(normalizedStartDirectory); directory != null; directory = directory.Parent)
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

        static bool IsTerminalAppInstalled(TerminalAppProfile profile)
        {
            if (profile == TerminalAppProfile.SystemDefault)
            {
                return true;
            }

            string appName = profile switch
            {
                TerminalAppProfile.Ghostty => "Ghostty.app",
                TerminalAppProfile.Terminal => "Terminal.app",
                TerminalAppProfile.ITerm => "iTerm.app",
                TerminalAppProfile.Warp => "Warp.app",
                TerminalAppProfile.WezTerm => "WezTerm.app",
                TerminalAppProfile.Alacritty => "Alacritty.app",
                TerminalAppProfile.Kitty => "kitty.app",
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(appName))
            {
                return false;
            }

            string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Directory.Exists(Path.Combine("/Applications", appName))
                || Directory.Exists(Path.Combine(homeDirectory, "Applications", appName));
        }
    }
}
