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
        GUIStyle _cellStyle;
        GUIStyle _boldCellStyle;
        GUIStyle _italicCellStyle;
        GUIStyle _boldItalicCellStyle;
        float _cellWidth;
        float _cellHeight;
        bool _cursorVisible;
        double _lastBlinkToggle;
        int _scrollbackOffset;
        SelectionRange? _selection;

        public TerminalRenderer(ITerminalBuffer buffer)
        {
            _buffer = buffer;
        }

        public int VisibleCols { get; private set; }
        public int VisibleRows { get; private set; }
        internal float CellWidth => _cellWidth;
        internal float CellHeight => _cellHeight;
        public bool HasSelection => _selection.HasValue;

        public void InvalidateStyle()
        {
            _cellStyle = null;
            _boldCellStyle = null;
            _italicCellStyle = null;
            _boldItalicCellStyle = null;
        }

        public bool CalculateGridSize(Rect area)
        {
            EnsureStyle();
            var newVisibleCols = Mathf.Max(1, Mathf.FloorToInt(area.width / _cellWidth));
            var newVisibleRows = Mathf.Max(1, Mathf.FloorToInt(area.height / _cellHeight));
            var changed = newVisibleCols != VisibleCols || newVisibleRows != VisibleRows;
            VisibleCols = newVisibleCols;
            VisibleRows = newVisibleRows;
            return changed;
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

            var col = Mathf.Clamp(Mathf.FloorToInt((mousePosition.x - area.x) / _cellWidth), 0, Mathf.Max(0, VisibleCols - 1));
            var row = Mathf.Clamp(Mathf.FloorToInt((mousePosition.y - area.y) / _cellHeight), 0, Mathf.Max(0, VisibleRows - 1));
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

            for (var row = start.y; row <= end.y; row++)
            {
                var startCol = row == start.y ? start.x : 0;
                var endCol = row == end.y ? end.x : Mathf.Max(0, VisibleCols - 1);
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
            TerminalTheme theme = TerminalThemeResolver.GetCurrentTheme();
            EditorDrawRect(area, Opaque(theme.DefaultBackground));

            var rows = Mathf.Min(VisibleRows, _buffer.Rows);
            var cols = Mathf.Min(VisibleCols, _buffer.Cols);

            var now = EditorApplication.timeSinceStartup;
            if (now - _lastBlinkToggle > TerminalSettings.CursorBlinkRate)
            {
                _cursorVisible = !_cursorVisible;
                _lastBlinkToggle = now;
            }

            for (var row = 0; row < rows; row++)
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

            GUIStyle baseStyle = CreateTerminalBaseStyle();
            Font font = CreateMonospaceFont(TerminalSettings.GetEffectiveFontFamily(), TerminalSettings.FontSize) ?? baseStyle.font;

            _cellStyle = new GUIStyle(baseStyle)
            {
                font = font,
                fontSize = TerminalSettings.FontSize,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0),
                wordWrap = false,
                clipping = TextClipping.Clip,
                richText = false,
                stretchWidth = false,
                stretchHeight = false,
                contentOffset = Vector2.zero
            };
            _cellStyle.normal.textColor = Color.white;
            _cellStyle.hover.textColor = Color.white;
            _cellStyle.focused.textColor = Color.white;
            _cellStyle.active.textColor = Color.white;
            _boldCellStyle = CreateVariantStyle(_cellStyle, FontStyle.Bold);
            _italicCellStyle = CreateVariantStyle(_cellStyle, FontStyle.Italic);
            _boldItalicCellStyle = CreateVariantStyle(_cellStyle, FontStyle.BoldAndItalic);

            const int widthProbeLength = 16;
            var widthProbe = new GUIContent(new string('M', widthProbeLength));
            var measuredWidth = _cellStyle.CalcSize(widthProbe).x / widthProbeLength;
            var measuredHeight = Mathf.Max(_cellStyle.lineHeight, _cellStyle.CalcSize(new GUIContent("M")).y);
            _cellWidth = Mathf.Max(1f, measuredWidth);
            _cellHeight = Mathf.Max(1f, measuredHeight);
        }

        static GUIStyle CreateTerminalBaseStyle()
        {
            var style = new GUIStyle
            {
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0),
                clipping = TextClipping.Clip,
                wordWrap = false,
                richText = false,
                stretchWidth = false,
                stretchHeight = false,
                contentOffset = Vector2.zero,
                imagePosition = ImagePosition.TextOnly
            };

            style.normal.background = null;
            style.hover.background = null;
            style.focused.background = null;
            style.active.background = null;
            style.onNormal.background = null;
            style.onHover.background = null;
            style.onFocused.background = null;
            style.onActive.background = null;
            style.normal.textColor = Color.white;
            style.hover.textColor = Color.white;
            style.focused.textColor = Color.white;
            style.active.textColor = Color.white;
            style.onNormal.textColor = Color.white;
            style.onHover.textColor = Color.white;
            style.onFocused.textColor = Color.white;
            style.onActive.textColor = Color.white;
            return style;
        }

        static Font CreateMonospaceFont(string preferredFontFamily, int fontSize)
        {
            var fontNames = ResolveFontCandidates(preferredFontFamily);

            return Font.CreateDynamicFontFromOSFont(fontNames, fontSize);
        }

        static string[] ResolveFontCandidates(string preferredFontFamily)
        {
            var preferredFamilies = Application.platform == RuntimePlatform.WindowsEditor
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

            var installedFonts = Font.GetOSInstalledFontNames();
            var installedFontSet = new HashSet<string>(installedFonts, StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>(preferredFamilies.Length + 1);

            AddInstalledFont(candidates, installedFontSet, preferredFontFamily);
            for (var i = 0; i < preferredFamilies.Length; i++)
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
            var displayRow = row;
            var isScrollback = false;
            var scrollbackRow = -1;

            if (_scrollbackOffset > 0)
            {
                var scrollbackRowsVisible = Mathf.Min(_scrollbackOffset, VisibleRows);
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

            TerminalTheme theme = TerminalThemeResolver.GetCurrentTheme();
            var runBuilder = new StringBuilder(cols);
            var runStartCol = 0;
            Color runForeground = Opaque(theme.DefaultForeground);
            CellFlags runFlags = CellFlags.None;
            var hasRun = false;

            for (var col = 0; col < cols; col++)
            {
                TerminalCell cell = isScrollback
                    ? _buffer.GetScrollbackCell(scrollbackRow, col)
                    : _buffer.GetCell(displayRow, col);

                var cellRect = new Rect(
                    area.x + (col * _cellWidth),
                    area.y + (row * _cellHeight),
                    GetCellDrawWidth(isScrollback, scrollbackRow, displayRow, col, cols),
                    _cellHeight);

                Color bgColor = Opaque(cell.Background.ToUnityColor(theme.Palette, theme.DefaultBackground));
                Color fgColor = Opaque(cell.Foreground.ToUnityColor(theme.Palette, theme.DefaultForeground));
                if ((cell.Flags & CellFlags.Inverse) != 0)
                {
                    fgColor = Opaque(cell.Background.ToUnityColor(theme.Palette, theme.DefaultBackground));
                    bgColor = Opaque(cell.Foreground.ToUnityColor(theme.Palette, theme.DefaultForeground));
                }

                if ((cell.Flags & CellFlags.Dim) != 0)
                {
                    fgColor.r *= 0.6f;
                    fgColor.g *= 0.6f;
                    fgColor.b *= 0.6f;
                }

                if (_selection.HasValue && _selection.Value.Contains(row, col))
                {
                    bgColor = new Color(0.33f, 0.52f, 0.88f, 1f);
                    fgColor = Color.white;
                }

                if (bgColor != theme.DefaultBackground)
                {
                    EditorDrawRect(cellRect, bgColor);
                }

                var displayCharacter = cell.Codepoint == '\0' ? ' ' : cell.Codepoint;
                if (!hasRun)
                {
                    hasRun = true;
                    runStartCol = col;
                    runForeground = fgColor;
                    runFlags = cell.Flags;
                    runBuilder.Clear();
                }
                else if (runForeground != fgColor || runFlags != cell.Flags)
                {
                    DrawTextRun(area, row, runStartCol, runBuilder.ToString(), runFlags, runForeground);
                    runStartCol = col;
                    runForeground = fgColor;
                    runFlags = cell.Flags;
                    runBuilder.Clear();
                }

                runBuilder.Append(displayCharacter);
            }

            if (hasRun)
            {
                DrawTextRun(area, row, runStartCol, runBuilder.ToString(), runFlags, runForeground);
            }
        }

        string GetSelectedRowText(int displayRow, int startCol, int endCol)
        {
            if (!TryMapDisplayRow(displayRow, out var isScrollback, out var mappedRow))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(Mathf.Max(0, endCol - startCol + 1));
            var maxCol = Mathf.Min(endCol, Mathf.Max(0, _buffer.Cols - 1));
            for (var col = startCol; col <= maxCol; col++)
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
                var scrollbackRowsVisible = Mathf.Min(_scrollbackOffset, VisibleRows);
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

        void DrawTextRun(Rect area, int row, int startCol, string text, CellFlags flags, Color foreground)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var runRect = new Rect(
                area.x + (startCol * _cellWidth),
                area.y + (row * _cellHeight),
                text.Length * _cellWidth,
                _cellHeight);

            GUI.Label(runRect, text, GetModifiedStyle(flags, foreground));
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
            TerminalTheme theme = TerminalThemeResolver.GetCurrentTheme();
            Color opaqueCursorColor = Opaque(theme.CursorColor);
            GUI.color = opaqueCursorColor;
            GUI.DrawTexture(cursorRect, Texture2D.whiteTexture);
            GUI.color = previousColor;

            var cell = _buffer.GetCell(cursor.Row, cursor.Col);
            if (cell.Codepoint != ' ')
            {
                GUI.Label(cursorRect, cell.Codepoint.ToString(), GetModifiedStyle(CellFlags.None, Opaque(theme.DefaultBackground)));
            }
        }

        static Color Opaque(Color color)
        {
            color.a = 1f;
            return color;
        }

        static GUIStyle CreateVariantStyle(GUIStyle baseStyle, FontStyle fontStyle)
        {
            var style = new GUIStyle(baseStyle);
            style.fontStyle = fontStyle;
            return style;
        }

        GUIStyle GetModifiedStyle(CellFlags flags, Color textColor)
        {
            GUIStyle style = flags switch
            {
                _ when (flags & CellFlags.Bold) != 0 && (flags & CellFlags.Italic) != 0 => _boldItalicCellStyle,
                _ when (flags & CellFlags.Bold) != 0 => _boldCellStyle,
                _ when (flags & CellFlags.Italic) != 0 => _italicCellStyle,
                _ => _cellStyle
            };

            style.normal.textColor = textColor;
            style.hover.textColor = textColor;
            style.focused.textColor = textColor;
            style.active.textColor = textColor;
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
