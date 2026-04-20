using System;
using System.Collections.Generic;
using System.Globalization;

namespace Linalab.Terminal.Editor
{
    public sealed class AnsiParser
    {
        enum ParserState
        {
            Ground,
            Escape,
            EscapeIntermediate,
            Csi,
            IgnoreString,
            IgnoreStringEscape
        }

        readonly ITerminalBuffer _buffer;
        readonly List<int> _parameters = new();

        ParserState _state;
        int _currentParameter;
        bool _hasCurrentParameter;
        char _privateMarker;
        bool _hasCsiIntermediate;
        bool _lastCharacterWasCarriageReturn;

        TerminalColor _foreground = TerminalColor.DefaultColor;
        TerminalColor _background = TerminalColor.DefaultColor;
        CellFlags _flags = CellFlags.None;
        TerminalColor _savedForeground = TerminalColor.DefaultColor;
        TerminalColor _savedBackground = TerminalColor.DefaultColor;
        CellFlags _savedFlags = CellFlags.None;
        bool _hasSavedPen;
        bool _mouseTrackingPress;
        bool _mouseTrackingDrag;
        bool _mouseTrackingAnyMotion;

        public TerminalMouseTrackingMode MouseTrackingMode
        {
            get
            {
                if (_mouseTrackingAnyMotion)
                {
                    return TerminalMouseTrackingMode.AnyMotion;
                }

                if (_mouseTrackingDrag)
                {
                    return TerminalMouseTrackingMode.ButtonDrag;
                }

                if (_mouseTrackingPress)
                {
                    return TerminalMouseTrackingMode.ButtonPress;
                }

                return TerminalMouseTrackingMode.None;
            }
        }

        public TerminalMouseEncoding MouseEncoding { get; private set; }
        public bool IsMouseReportingEnabled => MouseTrackingMode != TerminalMouseTrackingMode.None;

        public AnsiParser(ITerminalBuffer buffer)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        }

        public Action<string> ResponseCallback { get; set; }

        public void Feed(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            data = SanitizeInput(data);
            if (data.Length == 0)
            {
                return;
            }

            for (int i = 0; i < data.Length; i++)
            {
                Feed(data[i]);
            }
        }

        static string SanitizeInput(string data)
        {
            return data
                .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
                .Replace("<feff>", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        public void Feed(char character)
        {
            if (HandleControlCharacter(character))
            {
                return;
            }

            _lastCharacterWasCarriageReturn = false;

            switch (_state)
            {
                case ParserState.Ground:
                    if (IsPrintable(character))
                    {
                        _buffer.PutChar(character);
                    }
                    break;

                case ParserState.Escape:
                    HandleEscape(character);
                    break;

                case ParserState.Csi:
                    HandleCsi(character);
                    break;

                case ParserState.IgnoreString:
                    HandleIgnoredString(character);
                    break;

                case ParserState.IgnoreStringEscape:
                    _state = character == '\\' ? ParserState.Ground : ParserState.IgnoreString;
                    break;

                case ParserState.EscapeIntermediate:
                    _state = ParserState.Ground;
                    break;
            }
        }

        bool HandleControlCharacter(char character)
        {
            if (_state == ParserState.IgnoreString)
            {
                if (character == '\x07')
                {
                    _state = ParserState.Ground;
                    return true;
                }

                if (character == '\x1B')
                {
                    _state = ParserState.IgnoreStringEscape;
                    return true;
                }
            }

            switch (character)
            {
                case '\x07':
                    return true;
                case '\x08':
                    _buffer.Backspace();
                    return true;
                case '\x09':
                    _buffer.Tab();
                    return true;
                case '\x0A':
                case '\x0B':
                case '\x0C':
                    _buffer.NewLine();
                    if (!_lastCharacterWasCarriageReturn)
                    {
                        _buffer.CarriageReturn();
                    }

                    _lastCharacterWasCarriageReturn = false;
                    return true;
                case '\x0D':
                    _buffer.CarriageReturn();
                    _lastCharacterWasCarriageReturn = true;
                    return true;
                case '\x1B':
                    _state = ParserState.Escape;
                    ResetParameters();
                    _lastCharacterWasCarriageReturn = false;
                    return true;
                default:
                    _lastCharacterWasCarriageReturn = false;
                    return false;
            }
        }

        void HandleEscape(char character)
        {
            if (character == '[')
            {
                _state = ParserState.Csi;
                ResetParameters();
                return;
            }

            if (character is ']' or 'P' or '_' or '^' or 'X')
            {
                _state = ParserState.IgnoreString;
                return;
            }

            if (character is '(' or ')' or '*' or '+' or '-' or '.' or '/' or '#' or ' ')
            {
                _state = ParserState.EscapeIntermediate;
                return;
            }

            switch (character)
            {
                case 'D':
                    _buffer.NewLine();
                    break;
                case 'E':
                    _buffer.NewLine();
                    _buffer.CarriageReturn();
                    break;
                case 'M':
                    _buffer.ReverseIndex();
                    break;
                case 'c':
                    FullReset();
                    break;
                case '7':
                    SavePenState();
                    _buffer.SaveCursor();
                    break;
                case '8':
                    _buffer.RestoreCursor();
                    RestorePenState();
                    break;
                case '=':
                case '>':
                    break;
            }

            _state = ParserState.Ground;
        }

        void HandleIgnoredString(char character)
        {
            if (character == '\x07')
            {
                _state = ParserState.Ground;
                return;
            }

            if (character == '\x1B')
            {
                _state = ParserState.IgnoreStringEscape;
            }
        }

        void HandleCsi(char character)
        {
            if (!_hasCurrentParameter
                && _parameters.Count == 0
                && !_hasCsiIntermediate
                && IsCsiPrivateMarker(character))
            {
                _privateMarker = character;
                return;
            }

            if (char.IsDigit(character))
            {
                _currentParameter = (_currentParameter * 10) + (character - '0');
                _hasCurrentParameter = true;
                return;
            }

            if (character == ';' || character == ':')
            {
                CommitParameter();
                return;
            }

            if (IsCsiIntermediate(character))
            {
                _hasCsiIntermediate = true;
                return;
            }

            if (!IsCsiFinalByte(character))
            {
                _state = ParserState.Ground;
                ResetParameters();
                return;
            }

            CommitParameter();
            DispatchCsi(character);
            _state = ParserState.Ground;
            ResetParameters();
        }

        void DispatchCsi(char finalByte)
        {
            if (_hasCsiIntermediate)
            {
                return;
            }

            if (_privateMarker != '\0')
            {
                DispatchPrivateCsi(finalByte);
                return;
            }

            switch (finalByte)
            {
                case '@':
                    _buffer.InsertBlankCharacters(GetPositiveParameter(0, 1));
                    break;
                case 'A':
                    _buffer.MoveCursorRelative(-GetPositiveParameter(0, 1), 0);
                    break;
                case 'B':
                    _buffer.MoveCursorRelative(GetPositiveParameter(0, 1), 0);
                    break;
                case 'C':
                    _buffer.MoveCursorRelative(0, GetPositiveParameter(0, 1));
                    break;
                case 'D':
                    _buffer.MoveCursorRelative(0, -GetPositiveParameter(0, 1));
                    break;
                case 'G':
                    _buffer.MoveCursorTo(_buffer.Cursor.Row, GetPositiveParameter(0, 1) - 1);
                    break;
                case 'H':
                case 'f':
                    _buffer.MoveCursorTo(GetPositiveParameter(0, 1) - 1, GetPositiveParameter(1, 1) - 1);
                    break;
                case 'J':
                    _buffer.EraseInDisplay(GetModeParameter(0, 0));
                    break;
                case 'K':
                    _buffer.EraseInLine(GetModeParameter(0, 0));
                    break;
                case 'L':
                    _buffer.InsertLines(GetPositiveParameter(0, 1));
                    break;
                case 'M':
                    _buffer.DeleteLines(GetPositiveParameter(0, 1));
                    break;
                case 'P':
                    DeleteCharacters(GetPositiveParameter(0, 1));
                    break;
                case 'S':
                    _buffer.ScrollUp(GetPositiveParameter(0, 1));
                    break;
                case 'T':
                    _buffer.ScrollDown(GetPositiveParameter(0, 1));
                    break;
                case 'X':
                    EraseCharacters(GetPositiveParameter(0, 1));
                    break;
                case 'c':
                    DispatchDeviceAttributes();
                    break;
                case 'd':
                    _buffer.MoveCursorTo(GetPositiveParameter(0, 1) - 1, _buffer.Cursor.Col);
                    break;
                case 'm':
                    ProcessSgr();
                    break;
                case 'n':
                    DispatchDeviceStatusReport();
                    break;
                case 'r':
                    DispatchSetScrollRegion();
                    break;
                case 's':
                    SavePenState();
                    _buffer.SaveCursor();
                    break;
                case 'u':
                    _buffer.RestoreCursor();
                    RestorePenState();
                    break;
            }
        }

        void DispatchSetScrollRegion()
        {
            int rows = _buffer.Rows;
            if (rows <= 0)
            {
                return;
            }

            int top = GetPositiveParameter(0, 1) - 1;
            int bottom = GetPositiveParameter(1, rows) - 1;

            if (top < 0)
            {
                top = 0;
            }

            if (bottom >= rows)
            {
                bottom = rows - 1;
            }

            if (top == 0 && bottom == rows - 1)
            {
                _buffer.ResetScrollRegion();
            }
            else if (top < bottom)
            {
                _buffer.SetScrollRegion(top, bottom);
            }

            _buffer.MoveCursorTo(0, 0);
        }

        void DispatchDeviceAttributes()
        {
            if (_privateMarker != '\0')
            {
                return;
            }

            ResponseCallback?.Invoke("\x1b[?1;2c");
        }

        void DispatchPrivateCsi(char finalByte)
        {
            if (_privateMarker != '?')
            {
                return;
            }

            bool? enabled = finalByte switch
            {
                'h' => true,
                'l' => false,
                _ => null
            };

            if (!enabled.HasValue)
            {
                return;
            }

            for (int i = 0; i < _parameters.Count; i++)
            {
                int parameter = GetModeParameter(i, -1);
                switch (parameter)
                {
                    case 1:
                    case 7:
                    case 12:
                    case 2004:
                        break;
                    case 25:
                        _buffer.SetCursorVisible(enabled.Value);
                        break;
                    case 47:
                    case 1047:
                    case 1049:
                        break;
                    case 1000:
                        _mouseTrackingPress = enabled.Value;
                        break;
                    case 1002:
                        _mouseTrackingDrag = enabled.Value;
                        break;
                    case 1003:
                        _mouseTrackingAnyMotion = enabled.Value;
                        break;
                    case 1005:
                        break;
                    case 1006:
                        if (enabled.Value)
                        {
                            MouseEncoding = TerminalMouseEncoding.Sgr;
                        }
                        else if (MouseEncoding == TerminalMouseEncoding.Sgr)
                        {
                            MouseEncoding = TerminalMouseEncoding.Default;
                        }
                        break;
                    case 1015:
                        break;
                }
            }
        }

        void ProcessSgr()
        {
            if (_parameters.Count == 0)
            {
                ResetPen();
                _buffer.SetAttribute(_foreground, _background, _flags);
                return;
            }

            for (int i = 0; i < _parameters.Count; i++)
            {
                int parameter = _parameters[i] < 0 ? 0 : _parameters[i];
                switch (parameter)
                {
                    case 0:
                        ResetPen();
                        break;
                    case 1:
                        _flags |= CellFlags.Bold;
                        break;
                    case 2:
                        _flags |= CellFlags.Dim;
                        break;
                    case 3:
                        _flags |= CellFlags.Italic;
                        break;
                    case 4:
                        _flags |= CellFlags.Underline;
                        break;
                    case 5:
                        _flags |= CellFlags.Blink;
                        break;
                    case 7:
                        _flags |= CellFlags.Inverse;
                        break;
                    case 8:
                        _flags |= CellFlags.Hidden;
                        break;
                    case 9:
                        _flags |= CellFlags.Strikethrough;
                        break;
                    case 22:
                        _flags &= ~(CellFlags.Bold | CellFlags.Dim);
                        break;
                    case 23:
                        _flags &= ~CellFlags.Italic;
                        break;
                    case 24:
                        _flags &= ~CellFlags.Underline;
                        break;
                    case 25:
                        _flags &= ~CellFlags.Blink;
                        break;
                    case 27:
                        _flags &= ~CellFlags.Inverse;
                        break;
                    case 28:
                        _flags &= ~CellFlags.Hidden;
                        break;
                    case 29:
                        _flags &= ~CellFlags.Strikethrough;
                        break;
                    case >= 30 and <= 37:
                        _foreground = TerminalColor.Named((byte)(parameter - 30));
                        break;
                    case 38:
                        if (TryConsumeExtendedColor(ref i, out var extendedForeground))
                        {
                            _foreground = extendedForeground;
                        }
                        break;
                    case 39:
                        _foreground = TerminalColor.DefaultColor;
                        break;
                    case >= 40 and <= 47:
                        _background = TerminalColor.Named((byte)(parameter - 40));
                        break;
                    case 48:
                        if (TryConsumeExtendedColor(ref i, out var extendedBackground))
                        {
                            _background = extendedBackground;
                        }
                        break;
                    case 49:
                        _background = TerminalColor.DefaultColor;
                        break;
                    case >= 90 and <= 97:
                        _foreground = TerminalColor.Named((byte)(parameter - 90 + 8));
                        break;
                    case >= 100 and <= 107:
                        _background = TerminalColor.Named((byte)(parameter - 100 + 8));
                        break;
                }
            }

            _buffer.SetAttribute(_foreground, _background, _flags);
        }

        bool TryConsumeExtendedColor(ref int index, out TerminalColor color)
        {
            color = TerminalColor.DefaultColor;
            if (index + 1 >= _parameters.Count)
            {
                return false;
            }

            int mode = _parameters[index + 1];
            if (mode == 5)
            {
                if (index + 2 < _parameters.Count && _parameters[index + 2] >= 0)
                {
                    color = TerminalColor.Indexed((byte)Math.Clamp(_parameters[index + 2], 0, 255));
                    index += 2;
                    return true;
                }

                index += 1;
                return false;
            }

            if (mode == 2)
            {
                if (index + 4 < _parameters.Count
                    && _parameters[index + 2] >= 0
                    && _parameters[index + 3] >= 0
                    && _parameters[index + 4] >= 0)
                {
                    color = TerminalColor.FromRgb(
                        (byte)Math.Clamp(_parameters[index + 2], 0, 255),
                        (byte)Math.Clamp(_parameters[index + 3], 0, 255),
                        (byte)Math.Clamp(_parameters[index + 4], 0, 255));
                    index += 4;
                    return true;
                }

                index += Math.Min(4, _parameters.Count - index - 1);
                return false;
            }

            index += 1;
            return false;
        }

        void DispatchDeviceStatusReport()
        {
            if (_privateMarker != '\0' || GetModeParameter(0, 0) != 6)
            {
                return;
            }

            var cursor = _buffer.Cursor;
            ResponseCallback?.Invoke($"\x1b[{cursor.Row + 1};{cursor.Col + 1}R");
        }

        void DeleteCharacters(int count)
        {
            if (count <= 0)
            {
                return;
            }

            var cursor = _buffer.Cursor;
            int row = cursor.Row;
            int startCol = cursor.Col;
            int deleteCount = Math.Min(count, _buffer.Cols - startCol);
            if (deleteCount <= 0)
            {
                return;
            }

            for (int col = startCol; col < _buffer.Cols - deleteCount; col++)
            {
                WriteCell(row, col, _buffer.GetCell(row, col + deleteCount));
            }

            for (int col = _buffer.Cols - deleteCount; col < _buffer.Cols; col++)
            {
                WriteBlank(row, col);
            }

            RestoreCursorAndPen(row, startCol);
        }

        void EraseCharacters(int count)
        {
            if (count <= 0)
            {
                return;
            }

            var cursor = _buffer.Cursor;
            int row = cursor.Row;
            int startCol = cursor.Col;
            int endCol = Math.Min(_buffer.Cols, startCol + count);
            for (int col = startCol; col < endCol; col++)
            {
                WriteBlank(row, col);
            }

            RestoreCursorAndPen(row, startCol);
        }

        void WriteCell(int row, int col, TerminalCell cell)
        {
            _buffer.MoveCursorTo(row, col);
            _buffer.SetAttribute(cell.Foreground, cell.Background, cell.Flags);
            _buffer.PutChar(cell.Codepoint);
        }

        void WriteBlank(int row, int col)
        {
            _buffer.MoveCursorTo(row, col);
            _buffer.SetAttribute(_foreground, _background, _flags);
            _buffer.PutChar(' ');
        }

        void RestoreCursorAndPen(int row, int col)
        {
            _buffer.MoveCursorTo(row, col);
            _buffer.SetAttribute(_foreground, _background, _flags);
        }

        void FullReset()
        {
            _buffer.Reset();
            ResetPen();
            ResetMouseState();
            _buffer.SetAttribute(_foreground, _background, _flags);
            _state = ParserState.Ground;
            ResetParameters();
        }

        void ResetPen()
        {
            _foreground = TerminalColor.DefaultColor;
            _background = TerminalColor.DefaultColor;
            _flags = CellFlags.None;
        }

        void SavePenState()
        {
            _savedForeground = _foreground;
            _savedBackground = _background;
            _savedFlags = _flags;
            _hasSavedPen = true;
        }

        void RestorePenState()
        {
            if (!_hasSavedPen)
            {
                ResetPen();
                return;
            }

            _foreground = _savedForeground;
            _background = _savedBackground;
            _flags = _savedFlags;
        }

        void CommitParameter()
        {
            _parameters.Add(_hasCurrentParameter ? _currentParameter : -1);
            _currentParameter = 0;
            _hasCurrentParameter = false;
        }

        void ResetParameters()
        {
            _parameters.Clear();
            _currentParameter = 0;
            _hasCurrentParameter = false;
            _privateMarker = '\0';
            _hasCsiIntermediate = false;
        }

        void ResetMouseState()
        {
            _mouseTrackingPress = false;
            _mouseTrackingDrag = false;
            _mouseTrackingAnyMotion = false;
            MouseEncoding = TerminalMouseEncoding.Default;
        }

        static bool IsCsiPrivateMarker(char character)
        {
            return character is >= '<' and <= '?';
        }

        static bool IsCsiIntermediate(char character)
        {
            return character is >= ' ' and <= '/';
        }

        static bool IsCsiFinalByte(char character)
        {
            return character is >= '@' and <= '~';
        }

        int GetPositiveParameter(int index, int defaultValue)
        {
            if (index >= _parameters.Count)
            {
                return defaultValue;
            }

            int value = _parameters[index];
            return value <= 0 ? defaultValue : value;
        }

        int GetModeParameter(int index, int defaultValue)
        {
            if (index >= _parameters.Count)
            {
                return defaultValue;
            }

            return _parameters[index] < 0 ? defaultValue : _parameters[index];
        }

        static bool IsPrintable(char character)
        {
            if (character < ' ' || character == '\x7F')
            {
                return false;
            }

            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            return category != UnicodeCategory.Format;
        }
    }
}
