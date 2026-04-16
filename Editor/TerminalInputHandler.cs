using UnityEditor;
using UnityEngine;

namespace Linalab.Terminal.Editor
{
    public static class TerminalInputHandler
    {
        public static string TranslateKeyEvent(Event evt)
        {
            if (evt == null || evt.type != EventType.KeyDown)
            {
                return null;
            }

            if (TryTranslateControlLetter(evt, out var controlSequence))
            {
                return controlSequence;
            }

            if (TryTranslateSpecialKey(evt, out var specialSequence))
            {
                return specialSequence;
            }

            if (TryTranslateArrowKey(evt, out var arrowSequence))
            {
                return arrowSequence;
            }

            if (TryTranslateFunctionKey(evt, out var functionSequence))
            {
                return functionSequence;
            }

            return TryTranslatePrintableCharacter(evt, out var printableSequence) ? printableSequence : null;
        }

        public static string GetPasteText() => EditorGUIUtility.systemCopyBuffer;

        static bool TryTranslatePrintableCharacter(Event evt, out string translated)
        {
            if (evt.character >= 0x20 && evt.character != 0x7f)
            {
                translated = evt.character.ToString();
                return true;
            }

            if (TryTranslatePrintableKeyCode(evt, out translated))
            {
                return true;
            }

            translated = null;
            return false;
        }

        static bool TryTranslatePrintableKeyCode(Event evt, out string translated)
        {
            translated = null;
            if (evt == null || evt.command || evt.control || evt.alt)
            {
                return false;
            }

            if (evt.keyCode >= KeyCode.A && evt.keyCode <= KeyCode.Z)
            {
                char baseCharacter = (char)('a' + (evt.keyCode - KeyCode.A));
                translated = (evt.shift ? char.ToUpperInvariant(baseCharacter) : baseCharacter).ToString();
                return true;
            }

            if (evt.keyCode >= KeyCode.Alpha0 && evt.keyCode <= KeyCode.Alpha9)
            {
                int index = evt.keyCode - KeyCode.Alpha0;
                translated = evt.shift ? ShiftedNumberSymbols[index] : ((char)('0' + index)).ToString();
                return true;
            }

            translated = evt.keyCode switch
            {
                KeyCode.Space => " ",
                KeyCode.Minus => evt.shift ? "_" : "-",
                KeyCode.Equals => evt.shift ? "+" : "=",
                KeyCode.LeftBracket => evt.shift ? "{" : "[",
                KeyCode.RightBracket => evt.shift ? "}" : "]",
                KeyCode.Backslash => evt.shift ? "|" : "\\",
                KeyCode.Semicolon => evt.shift ? ":" : ";",
                KeyCode.Quote => evt.shift ? "\"" : "'",
                KeyCode.BackQuote => evt.shift ? "~" : "`",
                KeyCode.Comma => evt.shift ? "<" : ",",
                KeyCode.Period => evt.shift ? ">" : ".",
                KeyCode.Slash => evt.shift ? "?" : "/",
                _ => null
            };

            return translated != null;
        }

        static readonly string[] ShiftedNumberSymbols =
        {
            ")",
            "!",
            "@",
            "#",
            "$",
            "%",
            "^",
            "&",
            "*",
            "("
        };

        static bool TryTranslateControlLetter(Event evt, out string translated)
        {
            if (!evt.control)
            {
                translated = null;
                return false;
            }

            if (evt.keyCode >= KeyCode.A && evt.keyCode <= KeyCode.Z)
            {
                int controlValue = (evt.keyCode - KeyCode.A) + 1;
                translated = ((char)controlValue).ToString();
                return true;
            }

            translated = evt.keyCode switch
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

        static bool TryTranslateSpecialKey(Event evt, out string translated)
        {
            translated = evt.keyCode switch
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
            translated = evt.keyCode switch
            {
                KeyCode.UpArrow => BuildArrowSequence('A', evt.shift, evt.control),
                KeyCode.DownArrow => BuildArrowSequence('B', evt.shift, evt.control),
                KeyCode.RightArrow => BuildArrowSequence('C', evt.shift, evt.control),
                KeyCode.LeftArrow => BuildArrowSequence('D', evt.shift, evt.control),
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
            translated = evt.keyCode switch
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
    }
}
