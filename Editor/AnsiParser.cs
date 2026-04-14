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
            Csi
        }

        readonly ITerminalBuffer _buffer;
        readonly List<int> _parameters = new();

        ParserState _state;
        int _currentParameter;
        bool _hasCurrentParameter;
        bool _privateMarker;
        bool _lastCharacterWasCarriageReturn;

        TerminalColor _foreground = TerminalColor.DefaultColor;
        TerminalColor _background = TerminalColor.DefaultColor;
        CellFlags _flags = CellFlags.None;

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

                case ParserState.EscapeIntermediate:
                    _state = ParserState.Ground;
                    break;
            }
        }

        bool HandleControlCharacter(char character)
        {
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

            if (character is '(' or ')' or '*' or '+' or '-' or '.' or '/')
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
            }

            _state = ParserState.Ground;
        }

        void HandleCsi(char character)
        {
            if (character == '?')
            {
                _privateMarker = true;
                return;
            }

            if (char.IsDigit(character))
            {
                _currentParameter = (_currentParameter * 10) + (character - '0');
                _hasCurrentParameter = true;
                return;
            }

            if (character == ';')
            {
                CommitParameter();
                return;
            }

            CommitParameter();
            DispatchCsi(character);
            _state = ParserState.Ground;
            ResetParameters();
        }

        void DispatchCsi(char finalByte)
        {
            switch (finalByte)
            {
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
                case 'X':
                    EraseCharacters(GetPositiveParameter(0, 1));
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
                case 'h':
                case 'l':
                    break;
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
            if (_privateMarker || GetModeParameter(0, 0) != 6)
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
            _privateMarker = false;
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
