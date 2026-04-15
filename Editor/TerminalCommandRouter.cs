using System;
using System.Collections.Generic;
using System.Text;

namespace Linalab.Terminal.Editor
{
    /// <summary>
    /// Intercepts known terminal input lines before they reach the shell and dispatches
    /// them to registered editor-level handlers instead.
    ///
    /// Tracks printable characters typed by the user to reconstruct the current input
    /// line. When the user presses Enter the accumulated line is checked against the
    /// route table. On a match the handler is invoked and null is returned (indicating
    /// the input was consumed). On no match the original Enter sequence is returned so
    /// the shell receives it normally.
    ///
    /// The line buffer is reset when:
    ///   - Enter is pressed (matched or not)
    ///   - Ctrl-C / Ctrl-D is sent
    ///   - An escape sequence (arrow keys, function keys, etc.) is received — the
    ///     buffer is also marked dirty so command history navigation does not produce
    ///     false positives.
    /// </summary>
    sealed class TerminalCommandRouter
    {
        readonly Dictionary<string, Action> _routes =
            new(StringComparer.OrdinalIgnoreCase);

        readonly StringBuilder _lineBuffer = new();
        bool _isDirty;

        // ── Registration ─────────────────────────────────────────────────────────

        public void Register(string command, Action handler)
        {
            if (string.IsNullOrWhiteSpace(command) || handler == null)
            {
                return;
            }

            _routes[command.Trim()] = handler;
        }

        public void Unregister(string command)
        {
            if (!string.IsNullOrWhiteSpace(command))
            {
                _routes.Remove(command.Trim());
            }
        }

        public void ClearRoutes()
        {
            _routes.Clear();
        }

        // ── Routing ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Processes a translated input sequence.
        /// When the typed line matches a registered route the handler is invoked as a
        /// <b>state-sync side-effect only</b>. The original input is always returned so
        /// the shell still receives and executes the command normally. Handlers must not
        /// write to the shell themselves — the shell already has the typed text and will
        /// execute it when it receives the Enter that is passed through here.
        /// </summary>
        public string Route(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            // Enter — run the side-effect handler if the line matches a route, then
            // always forward \r so the shell executes what it already has in its buffer.
            if (input == "\r")
            {
                HandleEnter();   // state-sync side-effect only
                return "\r";     // always pass through to shell
            }

            // Ctrl-C / Ctrl-D — abort current line, pass through
            if (input == "\x03" || input == "\x04")
            {
                Reset();
                return input;
            }

            // Backspace (DEL \x7f or BS \x08)
            if (input == "\x7f" || input == "\x08")
            {
                if (!_isDirty && _lineBuffer.Length > 0)
                {
                    _lineBuffer.Remove(_lineBuffer.Length - 1, 1);
                }

                return input;
            }

            // Escape sequences — arrow keys, function keys, cursor positioning
            // After one arrives we can no longer track the line accurately.
            if (input.Length > 0 && input[0] == '\x1b')
            {
                if (!_isDirty)
                {
                    _lineBuffer.Clear();
                    _isDirty = true;
                }

                return input;
            }

            // Printable single character
            if (input.Length == 1 && input[0] >= 0x20)
            {
                if (!_isDirty)
                {
                    _lineBuffer.Append(input[0]);
                }

                return input;
            }

            return input;
        }

        /// <summary>
        /// Resets the line buffer and dirty flag. Call when the shell is restarted or
        /// the terminal is cleared so stale input does not produce spurious matches.
        /// </summary>
        public void Reset()
        {
            _lineBuffer.Clear();
            _isDirty = false;
        }

        // ── Internals ─────────────────────────────────────────────────────────────

        void HandleEnter()
        {
            string line = _isDirty ? null : _lineBuffer.ToString().Trim();
            _lineBuffer.Clear();
            _isDirty = false;

            if (line != null && _routes.TryGetValue(line, out var handler))
            {
                handler?.Invoke();
            }
        }
    }
}
