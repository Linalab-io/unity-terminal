using System;

namespace Linalab.Terminal.Editor
{
    public interface ITerminalBuffer
    {
        int Rows { get; }
        int Cols { get; }
        int ScrollbackCount { get; }
        CursorState Cursor { get; }
        bool IsDirty { get; }
        TerminalCell GetCell(int row, int col);
        TerminalCell GetScrollbackCell(int scrollbackRow, int col);
        void PutChar(char c);
        void MoveCursorTo(int row, int col);
        void MoveCursorRelative(int deltaRow, int deltaCol);
        void CarriageReturn();
        void NewLine();
        void ReverseIndex();
        void EraseInDisplay(int mode);
        void EraseInLine(int mode);
        void InsertLines(int count);
        void DeleteLines(int count);
        void ScrollUp(int lines);
        void ScrollDown(int lines);
        void SetScrollRegion(int top, int bottom);
        void ResetScrollRegion();
        void Resize(int rows, int cols);
        void Reset();
        void Tab();
        void Backspace();
        void SetAttribute(TerminalColor fg, TerminalColor bg, CellFlags flags);
        void ResetAttributes();
        void ClearDirty();
    }

    public sealed class TerminalBuffer : ITerminalBuffer
    {
        const char ContinuationSpacer = '\0';

        readonly int _maxScrollback;
        readonly TerminalCell[][] _scrollback;

        TerminalCell[,] _grid;
        CursorState _cursor;
        TerminalColor _currentForeground;
        TerminalColor _currentBackground;
        CellFlags _currentFlags;
        int _scrollbackHead;
        int _scrollbackCount;
        int _topMargin;
        int _bottomMargin;
        bool _isDirty;

        public TerminalBuffer(int rows, int cols, int maxScrollback = 5000)
        {
            if (rows <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rows));
            }

            if (cols <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cols));
            }

            if (maxScrollback < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxScrollback));
            }

            _maxScrollback = maxScrollback;
            _scrollback = maxScrollback > 0 ? new TerminalCell[maxScrollback][] : Array.Empty<TerminalCell[]>();
            _grid = new TerminalCell[rows, cols];
            _cursor = CursorState.Create();
            ResetAttributesInternal();
            ResetScrollRegionInternal();
            ClearVisibleGrid();
            _isDirty = false;
        }

        public int Rows => _grid.GetLength(0);
        public int Cols => _grid.GetLength(1);
        public int ScrollbackCount => _scrollbackCount;
        public CursorState Cursor => _cursor;
        public bool IsDirty => _isDirty;

        public TerminalCell GetCell(int row, int col)
        {
            ValidateVisibleCell(row, col);
            return _grid[row, col];
        }

        public TerminalCell GetScrollbackCell(int scrollbackRow, int col)
        {
            if ((uint)scrollbackRow >= (uint)_scrollbackCount)
            {
                throw new ArgumentOutOfRangeException(nameof(scrollbackRow));
            }

            if ((uint)col >= (uint)Cols)
            {
                throw new ArgumentOutOfRangeException(nameof(col));
            }

            var row = _scrollback[GetScrollbackIndex(scrollbackRow)];
            return col < row.Length ? row[col] : TerminalCell.Empty;
        }

        public void PutChar(char c)
        {
            if (_cursor.PendingWrap)
            {
                ApplyPendingWrap();
            }

            int width = GetDisplayWidth(c);
            if (width > 1 && _cursor.Col >= Cols - 1)
            {
                ApplyPendingWrap();
            }

            SanitizeWriteSpan(_cursor.Row, _cursor.Col, width);

            _grid[_cursor.Row, _cursor.Col] = new TerminalCell
            {
                Codepoint = c,
                Foreground = _currentForeground,
                Background = _currentBackground,
                Flags = _currentFlags
            };

            if (width > 1 && _cursor.Col < Cols - 1)
            {
                _grid[_cursor.Row, _cursor.Col + 1] = new TerminalCell
                {
                    Codepoint = ContinuationSpacer,
                    Foreground = _currentForeground,
                    Background = _currentBackground,
                    Flags = _currentFlags
                };
            }

            if (_cursor.Col >= Cols - width)
            {
                _cursor.PendingWrap = true;
            }
            else
            {
                _cursor.Col += width;
            }

            _isDirty = true;
        }

        public void MoveCursorTo(int row, int col)
        {
            int nextRow = Math.Clamp(row, 0, Rows - 1);
            int nextCol = Math.Clamp(col, 0, Cols - 1);
            bool changed = _cursor.Row != nextRow || _cursor.Col != nextCol || _cursor.PendingWrap;
            _cursor.Row = nextRow;
            _cursor.Col = nextCol;
            _cursor.PendingWrap = false;

            if (changed)
            {
                _isDirty = true;
            }
        }

        public void MoveCursorRelative(int deltaRow, int deltaCol)
        {
            MoveCursorTo(_cursor.Row + deltaRow, _cursor.Col + deltaCol);
        }

        public void CarriageReturn()
        {
            bool changed = _cursor.Col != 0 || _cursor.PendingWrap;
            _cursor.Col = 0;
            _cursor.PendingWrap = false;
            if (changed)
            {
                _isDirty = true;
            }
        }

        public void NewLine()
        {
            bool changed = _cursor.PendingWrap;
            _cursor.PendingWrap = false;
            if (_cursor.Row == _bottomMargin)
            {
                ScrollUpInternal(1);
                changed = true;
            }
            else if (_cursor.Row < Rows - 1)
            {
                _cursor.Row++;
                changed = true;
            }

            if (changed)
            {
                _isDirty = true;
            }
        }

        public void ReverseIndex()
        {
            bool changed = _cursor.PendingWrap;
            _cursor.PendingWrap = false;
            if (_cursor.Row == _topMargin)
            {
                ScrollDownInternal(1);
                changed = true;
            }
            else if (_cursor.Row > 0)
            {
                _cursor.Row--;
                changed = true;
            }

            if (changed)
            {
                _isDirty = true;
            }
        }

        public void EraseInDisplay(int mode)
        {
            switch (mode)
            {
                case 0:
                    ClearRowSegment(_cursor.Row, _cursor.Col, Cols - 1);
                    for (int row = _cursor.Row + 1; row < Rows; row++)
                    {
                        ClearRow(row);
                    }
                    _isDirty = true;
                    break;
                case 1:
                    for (int row = 0; row < _cursor.Row; row++)
                    {
                        ClearRow(row);
                    }
                    ClearRowSegment(_cursor.Row, 0, _cursor.Col);
                    _isDirty = true;
                    break;
                case 2:
                    ClearVisibleGrid();
                    _isDirty = true;
                    break;
                case 3:
                    ClearVisibleGrid();
                    ClearScrollback();
                    _isDirty = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        public void EraseInLine(int mode)
        {
            switch (mode)
            {
                case 0:
                    ClearRowSegment(_cursor.Row, _cursor.Col, Cols - 1);
                    _isDirty = true;
                    break;
                case 1:
                    ClearRowSegment(_cursor.Row, 0, _cursor.Col);
                    _isDirty = true;
                    break;
                case 2:
                    ClearRow(_cursor.Row);
                    _isDirty = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        public void InsertLines(int count)
        {
            if (count <= 0 || _cursor.Row < _topMargin || _cursor.Row > _bottomMargin)
            {
                return;
            }

            int lineCount = Math.Min(count, _bottomMargin - _cursor.Row + 1);
            for (int row = _bottomMargin; row >= _cursor.Row + lineCount; row--)
            {
                CopyRow(row - lineCount, row);
            }

            for (int row = _cursor.Row; row < _cursor.Row + lineCount; row++)
            {
                ClearRow(row);
            }

            _cursor.PendingWrap = false;
            _isDirty = true;
        }

        public void DeleteLines(int count)
        {
            if (count <= 0 || _cursor.Row < _topMargin || _cursor.Row > _bottomMargin)
            {
                return;
            }

            int lineCount = Math.Min(count, _bottomMargin - _cursor.Row + 1);
            for (int row = _cursor.Row; row <= _bottomMargin - lineCount; row++)
            {
                CopyRow(row + lineCount, row);
            }

            for (int row = _bottomMargin - lineCount + 1; row <= _bottomMargin; row++)
            {
                ClearRow(row);
            }

            _cursor.PendingWrap = false;
            _isDirty = true;
        }

        public void ScrollUp(int lines)
        {
            if (lines <= 0)
            {
                return;
            }

            ScrollUpInternal(lines);
            _cursor.PendingWrap = false;
            _isDirty = true;
        }

        public void ScrollDown(int lines)
        {
            if (lines <= 0)
            {
                return;
            }

            ScrollDownInternal(lines);
            _cursor.PendingWrap = false;
            _isDirty = true;
        }

        public void SetScrollRegion(int top, int bottom)
        {
            if ((uint)top >= (uint)Rows)
            {
                throw new ArgumentOutOfRangeException(nameof(top));
            }

            if ((uint)bottom >= (uint)Rows)
            {
                throw new ArgumentOutOfRangeException(nameof(bottom));
            }

            if (top > bottom)
            {
                throw new ArgumentException("top must be less than or equal to bottom.");
            }

            bool changed = _topMargin != top || _bottomMargin != bottom;
            _topMargin = top;
            _bottomMargin = bottom;
            if (_cursor.PendingWrap)
            {
                _cursor.PendingWrap = false;
                changed = true;
            }

            if (changed)
            {
                _isDirty = true;
            }
        }

        public void ResetScrollRegion()
        {
            bool changed = _topMargin != 0 || _bottomMargin != Rows - 1 || _cursor.PendingWrap;
            ResetScrollRegionInternal();
            _cursor.PendingWrap = false;
            if (changed)
            {
                _isDirty = true;
            }
        }

        public void Resize(int rows, int cols)
        {
            if (rows <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rows));
            }

            if (cols <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cols));
            }

            if (rows == Rows && cols == Cols)
            {
                return;
            }

            var newGrid = new TerminalCell[rows, cols];
            FillGrid(newGrid, TerminalCell.Empty);
            int copyRows = Math.Min(Rows, rows);
            int copyCols = Math.Min(Cols, cols);
            for (int row = 0; row < copyRows; row++)
            {
                for (int col = 0; col < copyCols; col++)
                {
                    newGrid[row, col] = _grid[row, col];
                }

                SanitizeRow(newGrid, row, cols);
            }

            _grid = newGrid;
            _cursor.Clamp(rows, cols);
            _cursor.PendingWrap = false;
            ResetScrollRegionInternal();
            _isDirty = true;
        }

        public void Reset()
        {
            ClearVisibleGrid();
            ClearScrollback();
            _cursor = CursorState.Create();
            ResetAttributesInternal();
            ResetScrollRegionInternal();
            _isDirty = true;
        }

        public void Tab()
        {
            int nextCol = ((_cursor.Col / 8) + 1) * 8;
            nextCol = Math.Min(nextCol, Cols - 1);
            bool changed = _cursor.Col != nextCol || _cursor.PendingWrap;
            _cursor.Col = nextCol;
            _cursor.PendingWrap = false;
            if (changed)
            {
                _isDirty = true;
            }
        }

        public void Backspace()
        {
            int nextCol = Math.Max(0, _cursor.Col - 1);
            bool changed = _cursor.Col != nextCol || _cursor.PendingWrap;
            _cursor.Col = nextCol;
            _cursor.PendingWrap = false;
            if (changed)
            {
                _isDirty = true;
            }
        }

        public void SetAttribute(TerminalColor fg, TerminalColor bg, CellFlags flags)
        {
            bool changed = _currentForeground != fg || _currentBackground != bg || _currentFlags != flags;
            _currentForeground = fg;
            _currentBackground = bg;
            _currentFlags = flags;
            if (changed)
            {
                _isDirty = true;
            }
        }

        public void ResetAttributes()
        {
            bool changed = _currentForeground != TerminalColor.DefaultColor
                || _currentBackground != TerminalColor.DefaultColor
                || _currentFlags != CellFlags.None;
            ResetAttributesInternal();
            if (changed)
            {
                _isDirty = true;
            }
        }

        public void ClearDirty()
        {
            _isDirty = false;
        }

        void ApplyPendingWrap()
        {
            _cursor.PendingWrap = false;
            if (_cursor.Row == _bottomMargin)
            {
                ScrollUpInternal(1);
            }
            else if (_cursor.Row < Rows - 1)
            {
                _cursor.Row++;
            }

            _cursor.Col = 0;
        }

        void ScrollUpInternal(int lines)
        {
            int regionHeight = _bottomMargin - _topMargin + 1;
            int lineCount = Math.Min(lines, regionHeight);
            for (int i = 0; i < lineCount; i++)
            {
                if (_topMargin == 0)
                {
                    PushScrollback(CopyRowToArray(_topMargin));
                }

                for (int row = _topMargin; row < _bottomMargin; row++)
                {
                    CopyRow(row + 1, row);
                }

                ClearRow(_bottomMargin);
            }
        }

        void ScrollDownInternal(int lines)
        {
            int regionHeight = _bottomMargin - _topMargin + 1;
            int lineCount = Math.Min(lines, regionHeight);
            for (int i = 0; i < lineCount; i++)
            {
                for (int row = _bottomMargin; row > _topMargin; row--)
                {
                    CopyRow(row - 1, row);
                }

                ClearRow(_topMargin);
            }
        }

        void PushScrollback(TerminalCell[] row)
        {
            if (_maxScrollback == 0)
            {
                return;
            }

            if (_scrollbackCount < _maxScrollback)
            {
                int index = (_scrollbackHead + _scrollbackCount) % _maxScrollback;
                _scrollback[index] = row;
                _scrollbackCount++;
                return;
            }

            _scrollback[_scrollbackHead] = row;
            _scrollbackHead = (_scrollbackHead + 1) % _maxScrollback;
        }

        int GetScrollbackIndex(int row) => (_scrollbackHead + row) % _maxScrollback;

        TerminalCell[] CopyRowToArray(int row)
        {
            var copy = new TerminalCell[Cols];
            for (int col = 0; col < Cols; col++)
            {
                copy[col] = _grid[row, col];
            }

            return copy;
        }

        void CopyRow(int sourceRow, int destinationRow)
        {
            for (int col = 0; col < Cols; col++)
            {
                _grid[destinationRow, col] = _grid[sourceRow, col];
            }
        }

        void ClearVisibleGrid()
        {
            FillGrid(_grid, TerminalCell.Empty);
        }

        void ClearScrollback()
        {
            if (_scrollbackCount == 0)
            {
                return;
            }

            Array.Clear(_scrollback, 0, _scrollback.Length);
            _scrollbackHead = 0;
            _scrollbackCount = 0;
        }

        void ClearRow(int row)
        {
            for (int col = 0; col < Cols; col++)
            {
                _grid[row, col] = TerminalCell.Empty;
            }
        }

        void ClearRowSegment(int row, int startCol, int endCol)
        {
            int from = Math.Max(0, startCol);
            int to = Math.Min(Cols - 1, endCol);
            ExpandSanitizedSpan(row, ref from, ref to);
            for (int col = from; col <= to; col++)
            {
                _grid[row, col] = TerminalCell.Empty;
            }

            if (from <= to)
            {
                SanitizeBoundaryAt(row, from - 1);
                SanitizeBoundaryAt(row, to + 1);
            }
        }

        void SanitizeWriteSpan(int row, int startCol, int width)
        {
            int from = Math.Max(0, startCol);
            int to = Math.Min(Cols - 1, startCol + Math.Max(1, width) - 1);
            if (from > to)
            {
                return;
            }

            ExpandSanitizedSpan(row, ref from, ref to);

            for (int col = from; col <= to; col++)
            {
                _grid[row, col] = TerminalCell.Empty;
            }

            SanitizeBoundaryAt(row, from - 1);
            SanitizeBoundaryAt(row, from);
            SanitizeBoundaryAt(row, to);
            SanitizeBoundaryAt(row, to + 1);
        }

        void ExpandSanitizedSpan(int row, ref int from, ref int to)
        {
            if ((uint)row >= (uint)Rows || from > to)
            {
                return;
            }

            if (IsValidContinuationCell(_grid, row, from, Cols))
            {
                from = Math.Max(0, from - 1);
            }

            if (IsWideLeadCell(_grid, row, to, Cols))
            {
                to = Math.Min(Cols - 1, to + 1);
            }
        }

        void SanitizeBoundaryAt(int row, int col)
        {
            if ((uint)row >= (uint)Rows || (uint)col >= (uint)Cols)
            {
                return;
            }

            if (!IsContinuationCell(_grid[row, col]))
            {
                return;
            }

            if (!IsValidContinuationCell(_grid, row, col, Cols))
            {
                _grid[row, col] = TerminalCell.Empty;
            }
        }

        static void SanitizeRow(TerminalCell[,] grid, int row, int cols)
        {
            for (int col = 0; col < cols; col++)
            {
                if (grid[row, col].Codepoint != ContinuationSpacer)
                {
                    continue;
                }

                if (!IsValidContinuationCell(grid, row, col, cols))
                {
                    grid[row, col] = TerminalCell.Empty;
                }
            }
        }

        static bool IsContinuationCell(TerminalCell cell)
        {
            return cell.Codepoint == ContinuationSpacer;
        }

        static bool IsWideLeadCell(TerminalCell[,] grid, int row, int col, int cols)
        {
            return (uint)col < (uint)(cols - 1)
                && GetDisplayWidth(grid[row, col].Codepoint) > 1
                && IsContinuationCell(grid[row, col + 1]);
        }

        static bool IsValidContinuationCell(TerminalCell[,] grid, int row, int col, int cols)
        {
            return (uint)col < (uint)cols
                && IsContinuationCell(grid[row, col])
                && col > 0
                && IsWideLeadCell(grid, row, col - 1, cols);
        }

        void ResetAttributesInternal()
        {
            _currentForeground = TerminalColor.DefaultColor;
            _currentBackground = TerminalColor.DefaultColor;
            _currentFlags = CellFlags.None;
        }

        void ResetScrollRegionInternal()
        {
            _topMargin = 0;
            _bottomMargin = Rows - 1;
        }

        void ValidateVisibleCell(int row, int col)
        {
            if ((uint)row >= (uint)Rows)
            {
                throw new ArgumentOutOfRangeException(nameof(row));
            }

            if ((uint)col >= (uint)Cols)
            {
                throw new ArgumentOutOfRangeException(nameof(col));
            }
        }

        static void FillGrid(TerminalCell[,] grid, TerminalCell value)
        {
            int rows = grid.GetLength(0);
            int cols = grid.GetLength(1);
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    grid[row, col] = value;
                }
            }
        }

        static int GetDisplayWidth(char character)
        {
            int codepoint = character;

            return codepoint switch
            {
                >= 0x1100 and <= 0x115F => 2,
                >= 0x2329 and <= 0x232A => 2,
                >= 0x2E80 and <= 0xA4CF => 2,
                >= 0xAC00 and <= 0xD7A3 => 2,
                >= 0xF900 and <= 0xFAFF => 2,
                >= 0xFE10 and <= 0xFE19 => 2,
                >= 0xFE30 and <= 0xFE6F => 2,
                >= 0xFF00 and <= 0xFF60 => 2,
                >= 0xFFE0 and <= 0xFFE6 => 2,
                _ => 1
            };
        }
    }
}
