using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Linalab.Terminal.Editor
{
    public sealed class TerminalRenderer
    {
        public readonly struct SelectionRange
        {
            public readonly Vector2Int Start;
            public readonly Vector2Int End;

            public SelectionRange(Vector2Int start, Vector2Int end)
            {
                Start = start;
                End = end;
            }

            public bool Contains(int row, int col)
            {
                Normalize(out var normalizedStart, out var normalizedEnd);
                if (row < normalizedStart.y || row > normalizedEnd.y)
                {
                    return false;
                }

                if (normalizedStart.y == normalizedEnd.y)
                {
                    return col >= normalizedStart.x && col <= normalizedEnd.x;
                }

                if (row == normalizedStart.y)
                {
                    return col >= normalizedStart.x;
                }

                if (row == normalizedEnd.y)
                {
                    return col <= normalizedEnd.x;
                }

                return true;
            }

            public void Normalize(out Vector2Int normalizedStart, out Vector2Int normalizedEnd)
            {
                if (Start.y < End.y || (Start.y == End.y && Start.x <= End.x))
                {
                    normalizedStart = Start;
                    normalizedEnd = End;
                    return;
                }

                normalizedStart = End;
                normalizedEnd = Start;
            }
        }

        readonly ITerminalBuffer _buffer;
        readonly Color[] _palette;

        GUIStyle _cellStyle;
        float _cellWidth;
        float _cellHeight;
        bool _cursorVisible;
        double _lastBlinkToggle;
        int _scrollbackOffset;
        SelectionRange? _selection;

        public TerminalRenderer(ITerminalBuffer buffer)
        {
            _buffer = buffer;
            _palette = AnsiPalette.Colors;
        }

        public int VisibleCols { get; private set; }
        public int VisibleRows { get; private set; }
        internal float CellWidth => _cellWidth;
        internal float CellHeight => _cellHeight;
        public bool HasSelection => _selection.HasValue;

        public void InvalidateStyle()
        {
            _cellStyle = null;
        }

        public void CalculateGridSize(Rect area)
        {
            EnsureStyle();
            VisibleCols = Mathf.Max(1, Mathf.FloorToInt(area.width / _cellWidth));
            VisibleRows = Mathf.Max(1, Mathf.FloorToInt(area.height / _cellHeight));
        }

        public void AdjustScroll(int delta)
        {
            _scrollbackOffset = Mathf.Clamp(_scrollbackOffset - delta, 0, _buffer.ScrollbackCount);
        }

        public void ScrollToBottom()
        {
            _scrollbackOffset = 0;
        }

        public void SetSelection(Vector2Int start, Vector2Int end)
        {
            _selection = new SelectionRange(start, end);
        }

        public void ClearSelection()
        {
            _selection = null;
        }

        public bool TryGetCellPosition(Rect area, Vector2 mousePosition, out Vector2Int position)
        {
            EnsureStyle();

            if (!area.Contains(mousePosition))
            {
                position = default;
                return false;
            }

            int col = Mathf.Clamp(Mathf.FloorToInt((mousePosition.x - area.x) / _cellWidth), 0, Mathf.Max(0, VisibleCols - 1));
            int row = Mathf.Clamp(Mathf.FloorToInt((mousePosition.y - area.y) / _cellHeight), 0, Mathf.Max(0, VisibleRows - 1));
            position = new Vector2Int(col, row);
            return true;
        }

        public string GetSelectedText()
        {
            if (!_selection.HasValue)
            {
                return string.Empty;
            }

            _selection.Value.Normalize(out var start, out var end);
            var builder = new StringBuilder();

            for (int row = start.y; row <= end.y; row++)
            {
                int startCol = row == start.y ? start.x : 0;
                int endCol = row == end.y ? end.x : Mathf.Max(0, VisibleCols - 1);
                builder.Append(GetSelectedRowText(row, startCol, endCol));

                if (row < end.y)
                {
                    builder.Append('\n');
                }
            }

            return builder.ToString();
        }

        public void Draw(Rect area)
        {
            EnsureStyle();
            EditorDrawRect(area, AnsiPalette.DefaultBackground);

            int rows = Mathf.Min(VisibleRows, _buffer.Rows);
            int cols = Mathf.Min(VisibleCols, _buffer.Cols);

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastBlinkToggle > TerminalSettings.CursorBlinkRate)
            {
                _cursorVisible = !_cursorVisible;
                _lastBlinkToggle = now;
            }

            for (int row = 0; row < rows; row++)
            {
                DrawRow(area, row, cols);
            }

            if (_scrollbackOffset == 0)
            {
                DrawCursor(area);
            }
        }

        void EnsureStyle()
        {
            if (_cellStyle != null)
            {
                return;
            }

            GUIStyle baseStyle = GetBaseStyle();
            Font font = CreateMonospaceFont(TerminalSettings.GetEffectiveFontFamily(), TerminalSettings.FontSize) ?? baseStyle.font;

            _cellStyle = new GUIStyle(baseStyle)
            {
                font = font,
                fontSize = TerminalSettings.FontSize,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                wordWrap = false,
                clipping = TextClipping.Clip,
                richText = false
            };

            var charSize = _cellStyle.CalcSize(new GUIContent("M"));
            _cellWidth = Mathf.Ceil(charSize.x);
            _cellHeight = Mathf.Ceil(charSize.y);
        }

        static GUIStyle GetBaseStyle()
        {
            try
            {
                return EditorStyles.label ?? GUI.skin?.label ?? new GUIStyle();
            }
            catch (NullReferenceException)
            {
                return new GUIStyle();
            }
        }

        static Font CreateMonospaceFont(string preferredFontFamily, int fontSize)
        {
            string[] fontNames = ResolveFontCandidates(preferredFontFamily);

            return Font.CreateDynamicFontFromOSFont(fontNames, fontSize);
        }

        static string[] ResolveFontCandidates(string preferredFontFamily)
        {
            string[] preferredFamilies = Application.platform == RuntimePlatform.WindowsEditor
                ? new[]
                {
                    "CaskaydiaCove Nerd Font Mono",
                    "JetBrainsMono Nerd Font Mono",
                    "Consolas",
                    "Courier New",
                    "Lucida Console"
                }
                : new[]
                {
                    "MesloLGS NF",
                    "MesloLGS Nerd Font",
                    "JetBrainsMono Nerd Font Mono",
                    "Hack Nerd Font Mono",
                    "SauceCodePro Nerd Font",
                    "Menlo",
                    "Monaco",
                    "Courier"
                };

            string[] installedFonts = Font.GetOSInstalledFontNames();
            var installedFontSet = new HashSet<string>(installedFonts, StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>(preferredFamilies.Length + 1);

            AddInstalledFont(candidates, installedFontSet, preferredFontFamily);
            for (int i = 0; i < preferredFamilies.Length; i++)
            {
                AddInstalledFont(candidates, installedFontSet, preferredFamilies[i]);
            }

            if (candidates.Count == 0)
            {
                return preferredFamilies;
            }

            return candidates.ToArray();
        }

        static void AddInstalledFont(List<string> candidates, HashSet<string> installedFontSet, string fontName)
        {
            if (string.IsNullOrWhiteSpace(fontName) || !installedFontSet.Contains(fontName))
            {
                return;
            }

            if (!candidates.Contains(fontName))
            {
                candidates.Add(fontName);
            }
        }

        void DrawRow(Rect area, int row, int cols)
        {
            int displayRow = row;
            bool isScrollback = false;
            int scrollbackRow = -1;

            if (_scrollbackOffset > 0)
            {
                int scrollbackRowsVisible = Mathf.Min(_scrollbackOffset, VisibleRows);
                if (row < scrollbackRowsVisible)
                {
                    isScrollback = true;
                    scrollbackRow = _buffer.ScrollbackCount - _scrollbackOffset + row;
                    if (scrollbackRow < 0)
                    {
                        return;
                    }
                }
                else
                {
                    displayRow = row - scrollbackRowsVisible;
                    if (displayRow >= _buffer.Rows)
                    {
                        return;
                    }
                }
            }

            for (int col = 0; col < cols; col++)
            {
                TerminalCell cell = isScrollback
                    ? _buffer.GetScrollbackCell(scrollbackRow, col)
                    : _buffer.GetCell(displayRow, col);

                var cellRect = new Rect(
                    area.x + (col * _cellWidth),
                    area.y + (row * _cellHeight),
                    GetCellDrawWidth(isScrollback, scrollbackRow, displayRow, col, cols),
                    _cellHeight);

                Color bgColor = cell.Background.ToUnityColor(_palette, AnsiPalette.DefaultBackground);
                Color fgColor = cell.Foreground.ToUnityColor(_palette, AnsiPalette.DefaultForeground);
                if ((cell.Flags & CellFlags.Inverse) != 0)
                {
                    (fgColor, bgColor) = (bgColor, fgColor);
                }

                if (_selection.HasValue && _selection.Value.Contains(row, col))
                {
                    bgColor = new Color(0.33f, 0.52f, 0.88f, 0.65f);
                    fgColor = Color.white;
                }

                if (bgColor != AnsiPalette.DefaultBackground)
                {
                    EditorDrawRect(cellRect, bgColor);
                }

                if (cell.Codepoint == ' ' || cell.Codepoint == '\0')
                {
                    continue;
                }

                if ((cell.Flags & CellFlags.Dim) != 0)
                {
                    fgColor.r *= 0.6f;
                    fgColor.g *= 0.6f;
                    fgColor.b *= 0.6f;
                }

                var previousColor = GUI.color;
                GUI.color = fgColor;
                GUI.Label(cellRect, cell.Codepoint.ToString(), GetModifiedStyle(cell.Flags));
                GUI.color = previousColor;
            }
        }

        string GetSelectedRowText(int displayRow, int startCol, int endCol)
        {
            if (!TryMapDisplayRow(displayRow, out var isScrollback, out var mappedRow))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(Mathf.Max(0, endCol - startCol + 1));
            int maxCol = Mathf.Min(endCol, Mathf.Max(0, _buffer.Cols - 1));
            for (int col = startCol; col <= maxCol; col++)
            {
                TerminalCell cell = isScrollback
                    ? _buffer.GetScrollbackCell(mappedRow, col)
                    : _buffer.GetCell(mappedRow, col);
                if (cell.Codepoint == '\0')
                {
                    continue;
                }

                builder.Append(cell.Codepoint);
            }

            return builder.ToString().TrimEnd(' ');
        }

        bool TryMapDisplayRow(int displayRow, out bool isScrollback, out int mappedRow)
        {
            if (_scrollbackOffset > 0)
            {
                int scrollbackRowsVisible = Mathf.Min(_scrollbackOffset, VisibleRows);
                if (displayRow < scrollbackRowsVisible)
                {
                    isScrollback = true;
                    mappedRow = _buffer.ScrollbackCount - _scrollbackOffset + displayRow;
                    return mappedRow >= 0 && mappedRow < _buffer.ScrollbackCount;
                }

                mappedRow = displayRow - scrollbackRowsVisible;
                isScrollback = false;
                return mappedRow >= 0 && mappedRow < _buffer.Rows;
            }

            isScrollback = false;
            mappedRow = displayRow;
            return mappedRow >= 0 && mappedRow < _buffer.Rows;
        }

        float GetCellDrawWidth(bool isScrollback, int scrollbackRow, int displayRow, int col, int cols)
        {
            if (col + 1 >= cols)
            {
                return _cellWidth;
            }

            TerminalCell nextCell = isScrollback
                ? _buffer.GetScrollbackCell(scrollbackRow, col + 1)
                : _buffer.GetCell(displayRow, col + 1);

            return nextCell.Codepoint == '\0' ? _cellWidth * 2f : _cellWidth;
        }

        void DrawCursor(Rect area)
        {
            var cursor = _buffer.Cursor;
            if (!cursor.Visible || !_cursorVisible)
            {
                return;
            }

            if (cursor.Row < 0 || cursor.Row >= VisibleRows || cursor.Col < 0 || cursor.Col >= VisibleCols)
            {
                return;
            }

            var cursorRect = new Rect(
                area.x + (cursor.Col * _cellWidth),
                area.y + (cursor.Row * _cellHeight),
                _cellWidth,
                _cellHeight);

            var previousColor = GUI.color;
            GUI.color = new Color(AnsiPalette.CursorColor.r, AnsiPalette.CursorColor.g, AnsiPalette.CursorColor.b, 0.7f);
            GUI.DrawTexture(cursorRect, Texture2D.whiteTexture);
            GUI.color = previousColor;

            var cell = _buffer.GetCell(cursor.Row, cursor.Col);
            if (cell.Codepoint != ' ')
            {
                GUI.color = AnsiPalette.DefaultBackground;
                GUI.Label(cursorRect, cell.Codepoint.ToString(), _cellStyle);
                GUI.color = previousColor;
            }
        }

        GUIStyle GetModifiedStyle(CellFlags flags)
        {
            if (flags == CellFlags.None)
            {
                return _cellStyle;
            }

            var style = new GUIStyle(_cellStyle);
            if ((flags & CellFlags.Bold) != 0)
            {
                style.fontStyle = FontStyle.Bold;
            }

            if ((flags & CellFlags.Italic) != 0)
            {
                style.fontStyle = (flags & CellFlags.Bold) != 0 ? FontStyle.BoldAndItalic : FontStyle.Italic;
            }

            return style;
        }

        static void EditorDrawRect(Rect rect, Color color)
        {
            var previousColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previousColor;
        }
    }
}
