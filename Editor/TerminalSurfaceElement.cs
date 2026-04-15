using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Linalab.Terminal.Editor
{
    sealed class TerminalSurfaceElement : ImmediateModeElement
    {
        readonly ITerminalBuffer _buffer;

        GUIStyle _cellStyle;
        GUIStyle _boldCellStyle;
        GUIStyle _italicCellStyle;
        GUIStyle _boldItalicCellStyle;
        Font _cachedFont;
        int _cachedFontSize;
        float _cellWidth;
        float _cellHeight;
        float _drawCellWidth;
        float _drawCellHeight;
        bool _cursorVisible;
        double _lastBlinkToggle;
        int _scrollbackOffset;
        bool _isSelecting;
        Vector2Int _selectionStart;
        Vector2Int _selectionEnd;

        public int VisibleCols { get; private set; }
        public int VisibleRows { get; private set; }
        public float CellWidth => _drawCellWidth > 0f ? _drawCellWidth : _cellWidth;
        public float CellHeight => _drawCellHeight > 0f ? _drawCellHeight : _cellHeight;
        public bool HasSelection { get; private set; }

        public event System.Action OnGridSizeChanged;
        public event System.Action OnInputRequested;

        public TerminalSurfaceElement(ITerminalBuffer buffer)
        {
            _buffer = buffer;
            focusable = true;
            style.flexGrow = 1;
            style.overflow = Overflow.Hidden;
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (UpdateGridSize())
            {
                MarkDirtyRepaint();
            }
        }

        public void InvalidateStyle()
        {
            _cellStyle = null;
            _boldCellStyle = null;
            _italicCellStyle = null;
            _boldItalicCellStyle = null;
            _cachedFont = null;
            _cachedFontSize = -1;
            MarkDirtyRepaint();
        }

        public void ScrollToBottom()
        {
            _scrollbackOffset = 0;
            MarkDirtyRepaint();
        }

        public void AdjustScroll(int delta)
        {
            _scrollbackOffset = Mathf.Clamp(_scrollbackOffset - delta, 0, _buffer.ScrollbackCount);
            MarkDirtyRepaint();
        }

        public void ClearSelection()
        {
            HasSelection = false;
            _selectionStart = default;
            _selectionEnd = default;
            MarkDirtyRepaint();
        }

        public bool TryGetCellPosition(Vector2 localMousePos, out Vector2Int position)
        {
            EnsureStyle();

            float drawCellWidth = CellWidth;
            float drawCellHeight = CellHeight;
            int col = Mathf.Clamp(Mathf.FloorToInt(localMousePos.x / drawCellWidth), 0, Mathf.Max(0, VisibleCols - 1));
            int row = Mathf.Clamp(Mathf.FloorToInt(localMousePos.y / drawCellHeight), 0, Mathf.Max(0, VisibleRows - 1));
            position = new Vector2Int(col, row);
            return true;
        }

        public string GetSelectedText()
        {
            if (!HasSelection)
            {
                return string.Empty;
            }

            NormalizeSelection(out var start, out var end);
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

        public bool UpdateGridSize()
        {
            EnsureStyle();

            if (contentRect.width < 1f || contentRect.height < 1f)
            {
                return false;
            }

            int newVisibleCols = Mathf.Max(1, Mathf.FloorToInt(contentRect.width / _cellWidth));
            int newVisibleRows = Mathf.Max(1, Mathf.FloorToInt(contentRect.height / _cellHeight));
            _drawCellWidth = contentRect.width / newVisibleCols;
            _drawCellHeight = contentRect.height / newVisibleRows;

            if (newVisibleCols != VisibleCols || newVisibleRows != VisibleRows)
            {
                VisibleCols = newVisibleCols;
                VisibleRows = newVisibleRows;
                OnGridSizeChanged?.Invoke();
                return true;
            }

            return false;
        }

        protected override void ImmediateRepaint()
        {
            HandleSurfaceEvent(Event.current);
            EnsureStyle();

            if (contentRect.width < 1f || contentRect.height < 1f || _buffer == null)
            {
                return;
            }

            UpdateGridSize();

            var clipRect = contentRect;
            GUI.BeginClip(clipRect);

            TerminalTheme theme = TerminalThemeResolver.GetCurrentTheme();
            Color backgroundColor = Opaque(theme.DefaultBackground);
            EditorGUI.DrawRect(new Rect(0, 0, clipRect.width, clipRect.height), backgroundColor);

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
                DrawRow(row, cols, theme);
            }

            if (_scrollbackOffset == 0)
            {
                DrawCursor(theme);
            }

            GUI.EndClip();
        }

        void HandleSurfaceEvent(Event evt)
        {
            if (evt == null)
            {
                return;
            }

            if (evt.type == EventType.MouseDown && evt.button == 0)
            {
                Focus();
                Vector2 localPos = evt.mousePosition - contentRect.position;

                if (contentRect.Contains(localPos) && TryGetCellPosition(localPos, out var cell))
                {
                    _isSelecting = true;
                    _selectionStart = cell;
                    _selectionEnd = cell;
                    HasSelection = true;
                    MarkDirtyRepaint();
                    evt.Use();
                }
            }
            else if (_isSelecting && evt.type == EventType.MouseDrag && evt.button == 0)
            {
                Vector2 localPos = ClampToContentRect(evt.mousePosition - contentRect.position);
                if (TryGetCellPosition(localPos, out var cell))
                {
                    _selectionEnd = cell;
                    MarkDirtyRepaint();
                }

                evt.Use();
            }
            else if (_isSelecting && evt.type == EventType.MouseUp && evt.button == 0)
            {
                _isSelecting = false;
                Vector2 localPos = ClampToContentRect(evt.mousePosition - contentRect.position);
                if (TryGetCellPosition(localPos, out var cell))
                {
                    _selectionEnd = cell;
                }

                if (HasSelection)
                {
                    string copiedText = GetSelectedText();
                    if (!string.IsNullOrEmpty(copiedText))
                    {
                        EditorGUIUtility.systemCopyBuffer = copiedText;
                    }

                    ClearSelection();
                }

                evt.Use();
            }
            else if (evt.type == EventType.ScrollWheel)
            {
                int scrollDelta = evt.delta.y > 0 ? -3 : 3;
                AdjustScroll(scrollDelta);
                evt.Use();
            }
            else if (evt.type == EventType.KeyDown && focusController != null && focusController.focusedElement == this)
            {
                OnInputRequested?.Invoke();
                evt.Use();
            }
        }

        void EnsureStyle()
        {
            if (_cellStyle != null && _cachedFont != null && _cachedFontSize == TerminalSettings.FontSize)
            {
                return;
            }

            GUIStyle baseStyle = CreateTerminalBaseStyle();
            Font font = CreateMonospaceFont(TerminalSettings.GetEffectiveFontFamily(), TerminalSettings.FontSize) ?? baseStyle.font;
            _cachedFont = font;
            _cachedFontSize = TerminalSettings.FontSize;

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
            float measuredWidth = _cellStyle.CalcSize(widthProbe).x / widthProbeLength;
            float measuredHeight = _cellStyle.CalcSize(new GUIContent("Mg")).y;
            _cellWidth = Mathf.Max(1f, measuredWidth);
            _cellHeight = Mathf.Max(1f, measuredHeight);
        }

        void DrawRow(int row, int cols, TerminalTheme theme)
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

            var runBuilder = new StringBuilder(cols);
            int runStartCol = 0;
            Color runForeground = Opaque(theme.DefaultForeground);
            CellFlags runFlags = CellFlags.None;
            bool hasRun = false;

            for (int col = 0; col < cols; col++)
            {
                TerminalCell cell = isScrollback
                    ? _buffer.GetScrollbackCell(scrollbackRow, col)
                    : _buffer.GetCell(displayRow, col);

                var cellRect = new Rect(
                    col * CellWidth,
                    row * CellHeight,
                    GetCellDrawWidth(isScrollback, scrollbackRow, displayRow, col, cols),
                    CellHeight);

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

                if (HasSelection && SelectionContains(row, col))
                {
                    bgColor = new Color(0.33f, 0.52f, 0.88f, 1f);
                    fgColor = Color.white;
                }

                if (bgColor != theme.DefaultBackground)
                {
                    EditorGUI.DrawRect(cellRect, bgColor);
                }

                char displayCharacter = cell.Codepoint == '\0' ? ' ' : cell.Codepoint;
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
                    DrawTextRun(row, runStartCol, runBuilder.ToString(), runFlags, runForeground);
                    runStartCol = col;
                    runForeground = fgColor;
                    runFlags = cell.Flags;
                    runBuilder.Clear();
                }

                runBuilder.Append(displayCharacter);
            }

            if (hasRun)
            {
                DrawTextRun(row, runStartCol, runBuilder.ToString(), runFlags, runForeground);
            }
        }

        bool SelectionContains(int row, int col)
        {
            if (!HasSelection)
            {
                return false;
            }

            NormalizeSelection(out var start, out var end);
            if (row < start.y || row > end.y)
            {
                return false;
            }

            if (start.y == end.y)
            {
                return col >= start.x && col <= end.x;
            }

            if (row == start.y)
            {
                return col >= start.x;
            }

            if (row == end.y)
            {
                return col <= end.x;
            }

            return true;
        }

        void DrawTextRun(int row, int startCol, string text, CellFlags flags, Color foreground)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var runRect = new Rect(
                startCol * CellWidth,
                row * CellHeight,
                text.Length * CellWidth,
                CellHeight);

            GUI.Label(runRect, text, GetModifiedStyle(flags, foreground));
        }

        void DrawCursor(TerminalTheme theme)
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
                cursor.Col * CellWidth,
                cursor.Row * CellHeight,
                CellWidth,
                CellHeight);

            EditorGUI.DrawRect(cursorRect, Opaque(theme.CursorColor));

            var cell = _buffer.GetCell(cursor.Row, cursor.Col);
            if (cell.Codepoint != ' ')
            {
                GUI.Label(cursorRect, cell.Codepoint.ToString(), GetModifiedStyle(CellFlags.None, Opaque(theme.DefaultBackground)));
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
                return CellWidth;
            }

            TerminalCell nextCell = isScrollback
                ? _buffer.GetScrollbackCell(scrollbackRow, col + 1)
                : _buffer.GetCell(displayRow, col + 1);

            return nextCell.Codepoint == '\0' ? CellWidth * 2f : CellWidth;
        }

        void NormalizeSelection(out Vector2Int normalizedStart, out Vector2Int normalizedEnd)
        {
            if (_selectionStart.y < _selectionEnd.y || (_selectionStart.y == _selectionEnd.y && _selectionStart.x <= _selectionEnd.x))
            {
                normalizedStart = _selectionStart;
                normalizedEnd = _selectionEnd;
                return;
            }

            normalizedStart = _selectionEnd;
            normalizedEnd = _selectionStart;
        }

        Vector2 ClampToContentRect(Vector2 point)
        {
            return new Vector2(
                Mathf.Clamp(point.x, 0f, contentRect.width - 1f),
                Mathf.Clamp(point.y, 0f, contentRect.height - 1f));
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
            var installedFontSet = new HashSet<string>(installedFonts, System.StringComparer.OrdinalIgnoreCase);
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

        static GUIStyle CreateVariantStyle(GUIStyle baseStyle, FontStyle fontStyle)
        {
            var style = new GUIStyle(baseStyle);
            style.fontStyle = fontStyle;
            return style;
        }

        static Color Opaque(Color color)
        {
            color.a = 1f;
            return color;
        }
    }
}
