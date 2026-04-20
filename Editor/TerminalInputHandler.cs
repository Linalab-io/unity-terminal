using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Linalab.Terminal.Editor
{
    public enum TerminalMouseTrackingMode
    {
        None,
        ButtonPress,
        ButtonDrag,
        AnyMotion
    }

    public enum TerminalMouseEncoding
    {
        Default,
        Utf8,
        Sgr,
        Urxvt
    }

    public static class TerminalInputHandler
    {
        public static string TranslateKeyEvent(Event evt)
        {
            return TranslateKeyEvent(evt, Input.compositionString);
        }

        public static string BuildDropInput(string projectRootDirectory, System.Collections.Generic.IEnumerable<string> paths)
        {
            if (paths == null)
            {
                return null;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var normalizedPath = NormalizeAttachmentPath(projectRootDirectory, path);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append('@');
                builder.Append(normalizedPath);
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        public static string BuildDropInput(string projectRootDirectory, string path)
        {
            return BuildDropInput(projectRootDirectory, new[] { path });
        }

        public static string GetPasteInput(string projectRootDirectory)
        {
            var imageInput = GetClipboardImageDropInput(projectRootDirectory);
            if (!string.IsNullOrEmpty(imageInput))
            {
                return imageInput;
            }

            return GetPasteText();
        }

        public static string GetClipboardImageDropInput(string projectRootDirectory)
        {
            if (Application.platform != RuntimePlatform.OSXEditor)
            {
                return null;
            }

            if (!HasMacClipboardImage())
            {
                return null;
            }

            try
            {
                var tempFilePath = Path.Combine(Path.GetTempPath(), $"unity-terminal-clipboard-{Guid.NewGuid():N}.tiff");
                if (!TryWriteMacClipboardImage(tempFilePath))
                {
                    TryDeleteTempFile(tempFilePath);
                    return null;
                }

                var input = BuildDropInput(projectRootDirectory, tempFilePath);
                if (string.IsNullOrEmpty(input))
                {
                    TryDeleteTempFile(tempFilePath);
                    return null;
                }

                return input;
            }
            catch
            {
                return null;
            }
        }

        public static string TranslateKeyEvent(char character, KeyCode keyCode, bool control, bool shift, string composition)
        {
            if (TryTranslateControlLetter(keyCode, control, out var controlSequence))
            {
                return controlSequence;
            }

            if (TryTranslateSpecialKey(keyCode, composition, out var specialSequence))
            {
                return specialSequence;
            }

            if (TryTranslateArrowKey(keyCode, shift, control, out var arrowSequence))
            {
                return arrowSequence;
            }

            if (TryTranslateFunctionKey(keyCode, out var functionSequence))
            {
                return functionSequence;
            }

            return TryTranslatePrintableCharacter(character, composition, out var printableSequence) ? printableSequence : null;
        }

        public static string TranslateKeyEvent(Event evt, string composition)
        {
            if (evt == null || evt.type != EventType.KeyDown)
            {
                return null;
            }

            return TranslateKeyEvent(evt.character, evt.keyCode, evt.control, evt.shift, composition);
        }

        public static string GetPasteText() => EditorGUIUtility.systemCopyBuffer;

        public static bool IsPrimaryPasteShortcut(RuntimePlatform platform, KeyCode keyCode, bool command, bool control)
        {
            bool isPrimaryModifier = platform == RuntimePlatform.OSXEditor ? command : control;
            return isPrimaryModifier && keyCode == KeyCode.V;
        }

        public static bool IsPasteCommand(string commandName)
        {
            return string.Equals(commandName, "Paste", System.StringComparison.Ordinal);
        }

        public static string TranslateMouseButtonEvent(
            TerminalMouseEncoding encoding,
            Vector2Int cell,
            int button,
            bool shift,
            bool alt,
            bool control,
            bool isRelease,
            bool isMotion)
        {
            if (button is < 0 or > 2)
            {
                return null;
            }

            int modifierMask = GetMouseModifierMask(shift, alt, control);
            if (encoding == TerminalMouseEncoding.Sgr)
            {
                int code = button + modifierMask;
                if (isMotion)
                {
                    code += 32;
                }

                char suffix = isRelease ? 'm' : 'M';
                return $"\x1b[<{code};{cell.x + 1};{cell.y + 1}{suffix}";
            }

            int legacyCode = isRelease ? 3 + modifierMask : button + modifierMask;
            if (isMotion)
            {
                legacyCode = button + modifierMask + 32;
            }

            return BuildLegacyMouseSequence(legacyCode, cell);
        }

        public static string TranslateMouseMoveEvent(
            TerminalMouseEncoding encoding,
            Vector2Int cell,
            bool shift,
            bool alt,
            bool control)
        {
            int modifierMask = GetMouseModifierMask(shift, alt, control);
            int code = 35 + modifierMask;
            if (encoding == TerminalMouseEncoding.Sgr)
            {
                return $"\x1b[<{code};{cell.x + 1};{cell.y + 1}M";
            }

            return BuildLegacyMouseSequence(code, cell);
        }

        public static string TranslateMouseScrollEvent(
            TerminalMouseEncoding encoding,
            Vector2Int cell,
            bool shift,
            bool alt,
            bool control,
            bool scrollUp)
        {
            int modifierMask = GetMouseModifierMask(shift, alt, control);
            int code = (scrollUp ? 64 : 65) + modifierMask;
            if (encoding == TerminalMouseEncoding.Sgr)
            {
                return $"\x1b[<{code};{cell.x + 1};{cell.y + 1}M";
            }

            return BuildLegacyMouseSequence(code, cell);
        }

        static bool TryTranslatePrintableCharacter(Event evt, string composition, out string translated)
        {
            return TryTranslatePrintableCharacter(evt.character, composition, out translated);
        }

        static bool TryTranslatePrintableCharacter(char character, string composition, out string translated)
        {
            if (ShouldSuppressPrintableDuringImeComposition(character, composition))
            {
                translated = null;
                return false;
            }

            if (character >= 0x20 && character != 0x7f)
            {
                translated = character.ToString();
                return true;
            }

            translated = null;
            return false;
        }

        static bool ShouldSuppressPrintableDuringImeComposition(Event evt, string composition)
        {
            return ShouldSuppressPrintableDuringImeComposition(evt?.character ?? '\0', composition);
        }

        static bool ShouldSuppressPrintableDuringImeComposition(char character, string composition)
        {
            if (string.IsNullOrEmpty(composition))
            {
                return false;
            }

            return character >= 0x20 && character != 0x7f;
        }

        static bool TryTranslateControlLetter(Event evt, out string translated)
        {
            return TryTranslateControlLetter(evt.keyCode, evt.control, out translated);
        }

        static bool TryTranslateControlLetter(KeyCode keyCode, bool control, out string translated)
        {
            if (!control)
            {
                translated = null;
                return false;
            }

            if (keyCode >= KeyCode.A && keyCode <= KeyCode.Z)
            {
                int controlValue = (keyCode - KeyCode.A) + 1;
                translated = ((char)controlValue).ToString();
                return true;
            }

            translated = keyCode switch
            {
                KeyCode.C => "\x03",
                KeyCode.D => "\x04",
                KeyCode.Z => "\x1a",
                KeyCode.L => "\x0c",
                KeyCode.U => "\x15",
                KeyCode.W => "\x17",
                KeyCode.R => "\x12",
                _ => null
            };

            return translated != null;
        }

        static bool TryTranslateSpecialKey(Event evt, string composition, out string translated)
        {
            return TryTranslateSpecialKey(evt.keyCode, composition, out translated);
        }

        static bool TryTranslateSpecialKey(KeyCode keyCode, string composition, out string translated)
        {
            if (!string.IsNullOrEmpty(composition)
                && (keyCode == KeyCode.Return || keyCode == KeyCode.KeypadEnter))
            {
                translated = null;
                return false;
            }

            translated = keyCode switch
            {
                KeyCode.Return or KeyCode.KeypadEnter => "\r",
                KeyCode.Backspace => "\x7f",
                KeyCode.Tab => "\t",
                KeyCode.Escape => "\x1b",
                KeyCode.Delete => "\x1b[3~",
                KeyCode.Home => "\x1b[H",
                KeyCode.End => "\x1b[F",
                KeyCode.PageUp => "\x1b[5~",
                KeyCode.PageDown => "\x1b[6~",
                KeyCode.Insert => "\x1b[2~",
                _ => null
            };

            return translated != null;
        }

        static bool TryTranslateArrowKey(Event evt, out string translated)
        {
            return TryTranslateArrowKey(evt.keyCode, evt.shift, evt.control, out translated);
        }

        static bool TryTranslateArrowKey(KeyCode keyCode, bool shift, bool control, out string translated)
        {
            translated = keyCode switch
            {
                KeyCode.UpArrow => BuildArrowSequence('A', shift, control),
                KeyCode.DownArrow => BuildArrowSequence('B', shift, control),
                KeyCode.RightArrow => BuildArrowSequence('C', shift, control),
                KeyCode.LeftArrow => BuildArrowSequence('D', shift, control),
                _ => null
            };

            return translated != null;
        }

        static string BuildArrowSequence(char finalByte, bool shift, bool control)
        {
            if (control)
            {
                return $"\x1b[1;5{finalByte}";
            }

            if (shift)
            {
                return $"\x1b[1;2{finalByte}";
            }

            return $"\x1b[{finalByte}";
        }

        static bool TryTranslateFunctionKey(Event evt, out string translated)
        {
            return TryTranslateFunctionKey(evt.keyCode, out translated);
        }

        static bool TryTranslateFunctionKey(KeyCode keyCode, out string translated)
        {
            translated = keyCode switch
            {
                KeyCode.F1 => "\x1bOP",
                KeyCode.F2 => "\x1bOQ",
                KeyCode.F3 => "\x1bOR",
                KeyCode.F4 => "\x1bOS",
                KeyCode.F5 => "\x1b[15~",
                KeyCode.F6 => "\x1b[17~",
                KeyCode.F7 => "\x1b[18~",
                KeyCode.F8 => "\x1b[19~",
                KeyCode.F9 => "\x1b[20~",
                KeyCode.F10 => "\x1b[21~",
                KeyCode.F11 => "\x1b[23~",
                KeyCode.F12 => "\x1b[24~",
                _ => null
            };

            return translated != null;
        }

        static int GetMouseModifierMask(bool shift, bool alt, bool control)
        {
            int modifierMask = 0;
            if (shift)
            {
                modifierMask |= 4;
            }

            if (alt)
            {
                modifierMask |= 8;
            }

            if (control)
            {
                modifierMask |= 16;
            }

            return modifierMask;
        }

        static string BuildLegacyMouseSequence(int code, Vector2Int cell)
        {
            int x = cell.x + 1;
            int y = cell.y + 1;
            if (x > 223 || y > 223)
            {
                return null;
            }

            return string.Concat(
                "\x1b[M",
                ((char)(code + 32)).ToString(),
                ((char)(x + 32)).ToString(),
                ((char)(y + 32)).ToString());
        }

        static string NormalizeAttachmentPath(string projectRootDirectory, string path)
        {
            var normalizedPath = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return string.Empty;
            }

            var normalizedProjectRoot = NormalizePath(projectRootDirectory);
            if (string.IsNullOrWhiteSpace(normalizedProjectRoot))
            {
                return normalizedPath;
            }

            if (string.Equals(normalizedPath, normalizedProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return ".";
            }

            var projectRootWithSlash = normalizedProjectRoot + "/";
            if (normalizedPath.StartsWith(projectRootWithSlash, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPath.Substring(projectRootWithSlash.Length);
            }

            return normalizedPath;
        }

        static bool TryWriteMacClipboardImage(string tempFilePath)
        {
            if (string.IsNullOrWhiteSpace(tempFilePath) || !File.Exists("/usr/bin/osascript"))
            {
                return false;
            }

            var escapedPath = tempFilePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var arguments = string.Join(" ", new[]
            {
                $"-e \"set outPath to POSIX file \\\"{escapedPath}\\\"\"",
                "-e \"set imageData to (the clipboard as TIFF picture)\"",
                "-e \"set fileRef to open for access outPath with write permission\"",
                "-e \"set eof fileRef to 0\"",
                "-e \"write imageData to fileRef\"",
                "-e \"close access fileRef\""
            });

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/osascript",
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit(5000);
                return process.ExitCode == 0 && File.Exists(tempFilePath);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        static bool HasMacClipboardImage()
        {
            if (!File.Exists("/usr/bin/osascript"))
            {
                return false;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/osascript",
                        Arguments = "-e \"clipboard info\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);
                if (process.ExitCode != 0 || string.IsNullOrEmpty(output))
                {
                    return false;
                }

                return output.Contains("TIFF picture", StringComparison.Ordinal)
                    || output.Contains("«class PNGf»", StringComparison.Ordinal)
                    || output.Contains("JPEG picture", StringComparison.Ordinal)
                    || output.Contains("GIF picture", StringComparison.Ordinal)
                    || output.Contains("«class BMP »", StringComparison.Ordinal)
                    || output.Contains("«class AVIF»", StringComparison.Ordinal)
                    || output.Contains("«class jp2 »", StringComparison.Ordinal)
                    || output.Contains("«class 8BPS»", StringComparison.Ordinal)
                    || output.Contains("«class TPIC»", StringComparison.Ordinal);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        static void TryDeleteTempFile(string tempFilePath)
        {
            if (string.IsNullOrWhiteSpace(tempFilePath) || !File.Exists(tempFilePath))
            {
                return;
            }

            try
            {
                File.Delete(tempFilePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return path.Trim().Replace('\\', '/');
        }
    }
}
