using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Linalab.Terminal.Editor
{
    sealed class TerminalSurfaceElement : IMGUIContainer
    {
        const string CellWidthProbeCharacters = "MW@#%&_gjy|/\\[]{}()";
        const string CellHeightProbeCharacters = "MW@#%&_gjy|/\\[]{}()한あ中█\uE0B0\uE0B1\uE0A0";

        readonly ITerminalBuffer _buffer;
        AnsiParser _parser;

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
        int _keyboardControlId;
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
        public event System.Action<Event> OnInputRequested;
        public event System.Action<string> OnMouseInputRequested;
        public event System.Action<IReadOnlyList<string>> OnDropRequested;
        public event System.Action OnInteractionStarted;

        public TerminalSurfaceElement(ITerminalBuffer buffer, AnsiParser parser)
        {
            _buffer = buffer;
            _parser = parser;
            onGUIHandler = DrawImmediateGui;
            focusable = true;
            tabIndex = 0;
            pickingMode = PickingMode.Position;
            // Editor UI Toolkit panels multiply all children by a dim/desaturated
            // "play mode tint" while EditorApplication.isPlaying is true. Opting
            // out keeps ANSI colors true to their palette hex in Play Mode too.
            disablePlayModeTint = true;
            style.flexGrow = 1;
            style.flexShrink = 1;
            style.flexBasis = 0f;
            style.minWidth = 0f;
            style.minHeight = 0f;
            style.alignSelf = Align.Stretch;
            style.width = Length.Percent(100);
            style.height = Length.Percent(100);
            style.overflow = Overflow.Hidden;
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RegisterCallback<MouseDownEvent>(OnMouseDownEvent);
            RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            RegisterCallback<DragPerformEvent>(OnDragPerform);
        }

        public void SetMouseProtocolSource(AnsiParser parser)
        {
            _parser = parser;
        }

        void OnMouseDownEvent(MouseDownEvent evt)
        {
            if (evt == null || evt.button != 0)
            {
                return;
            }

            OnInteractionStarted?.Invoke();
            Focus();
        }

        void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (evt == null || !TryGetDroppedPaths(out _))
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            evt.StopImmediatePropagation();
        }

        void OnDragPerform(DragPerformEvent evt)
        {
            if (evt == null || !TryGetDroppedPaths(out var droppedPaths))
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            DragAndDrop.AcceptDrag();
            OnInteractionStarted?.Invoke();
            Focus();
            OnDropRequested?.Invoke(droppedPaths);
            evt.StopImmediatePropagation();
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

        public bool TryGetCursorRect(out Rect cursorRect)
        {
            cursorRect = default;
            EnsureStyle();

            if (_buffer == null || _scrollbackOffset != 0 || contentRect.width < 1f || contentRect.height < 1f)
            {
                return false;
            }

            UpdateGridSize();
            var layout = BuildGridLayout();
            var cursor = _buffer.Cursor;
            if (!cursor.Visible || cursor.Row < 0 || cursor.Row >= VisibleRows || cursor.Col < 0 || cursor.Col >= VisibleCols)
            {
                return false;
            }

            int anchorCol = cursor.Col;
            int cursorWidth = 1;
            var cell = _buffer.GetCell(cursor.Row, cursor.Col);
            if (IsContinuationCell(cell))
            {
                int leadCol = cursor.Col - 1;
                if (leadCol < 0 || !IsWideLeadCell(false, -1, cursor.Row, leadCol, VisibleCols))
                {
                    return false;
                }

                anchorCol = leadCol;
                cursorWidth = 2;
            }

            cursorRect = GetCellRect(layout, anchorCol, cursor.Row, cursorWidth);
            return true;
        }

        public void AdjustScroll(int delta)
        {
            _scrollbackOffset = Mathf.Clamp(_scrollbackOffset - delta, 0, _buffer.ScrollbackCount);
            if (HasSelection)
            {
                ClearSelection();
                return;
            }

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

            if (drawCellWidth <= 0f || drawCellHeight <= 0f)
            {
                position = default;
                return false;
            }

            float gridWidth = VisibleCols * drawCellWidth;
            float gridHeight = VisibleRows * drawCellHeight;
            if (localMousePos.x < 0f || localMousePos.y < 0f || localMousePos.x >= gridWidth || localMousePos.y >= gridHeight)
            {
                position = default;
                return false;
            }

            int col = Mathf.FloorToInt(localMousePos.x / drawCellWidth);
            int row = Mathf.FloorToInt(localMousePos.y / drawCellHeight);
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

            float newDrawCellWidth = contentRect.width / newVisibleCols;
            float newDrawCellHeight = contentRect.height / newVisibleRows;
            bool gridChanged = newVisibleCols != VisibleCols || newVisibleRows != VisibleRows;
            bool drawMetricsChanged = !Mathf.Approximately(_drawCellWidth, newDrawCellWidth)
                || !Mathf.Approximately(_drawCellHeight, newDrawCellHeight);

            _drawCellWidth = newDrawCellWidth;
            _drawCellHeight = newDrawCellHeight;

            if (gridChanged)
            {
                VisibleCols = newVisibleCols;
                VisibleRows = newVisibleRows;
                if (HasSelection)
                {
                    HasSelection = false;
                    _selectionStart = default;
                    _selectionEnd = default;
                }
                OnGridSizeChanged?.Invoke();
                return true;
            }

            return drawMetricsChanged;
        }

        void DrawImmediateGui()
        {
            _keyboardControlId = GUIUtility.GetControlID(FocusType.Keyboard);
            HandleSurfaceEvent(Event.current);
            EnsureStyle();

            if (contentRect.width < 1f || contentRect.height < 1f || _buffer == null)
            {
                return;
            }

            UpdateGridSize();

            // UI Toolkit's IMGUIContainer does not reliably reset the IMGUI
            // global tint state before invoking onGUIHandler: the host editor
            // panel's dark-theme tint leaks into GUI.color/backgroundColor/
            // contentColor. EditorGUI.DrawRect internally multiplies by
            // GUI.color, so any non-white residual tint silently darkens every
            // palette color we pass in. Force-reset the trio for the duration
            // of our draw so ANSI palette hex values reach the backbuffer
            // unmodified.
            var savedGuiColor = GUI.color;
            var savedBackgroundColor = GUI.backgroundColor;
            var savedContentColor = GUI.contentColor;
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;

            var clipRect = contentRect;
            GUI.BeginClip(clipRect);

            try
            {
                TerminalTheme theme = TerminalThemeResolver.GetCurrentTheme();
                Color backgroundColor = Opaque(theme.DefaultBackground);
                var layout = BuildGridLayout();
                EditorGUI.DrawRect(new Rect(0f, 0f, layout.TotalWidth, layout.TotalHeight), backgroundColor);

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
                    DrawRow(row, cols, theme, backgroundColor, layout);
                }

                if (_scrollbackOffset == 0)
                {
                    if (!string.IsNullOrEmpty(Input.compositionString))
                    {
                        DrawCompositionPreview(theme, layout);
                    }
                    else
                    {
                        DrawCursor(theme, layout);
                    }
                }
            }
            finally
            {
                GUI.EndClip();
                GUI.color = savedGuiColor;
                GUI.backgroundColor = savedBackgroundColor;
                GUI.contentColor = savedContentColor;
            }
        }

        void HandleSurfaceEvent(Event evt)
        {
            if (evt == null)
            {
                return;
            }

            if (TryHandleMousePassthrough(evt))
            {
                return;
            }

            if (evt.type == EventType.MouseDown && evt.button == 0)
            {
                OnInteractionStarted?.Invoke();
                GUIUtility.keyboardControl = _keyboardControlId;
                Focus();
                Vector2 localPos = evt.mousePosition - contentRect.position;

                if (contentRect.Contains(evt.mousePosition) && TryGetCellPosition(localPos, out var cell))
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
                }

                MarkDirtyRepaint();
                evt.Use();
            }
            else if (evt.type == EventType.ScrollWheel)
            {
                int scrollDelta = evt.delta.y > 0 ? -3 : 3;
                AdjustScroll(scrollDelta);
                evt.Use();
            }
            else if (evt.type == EventType.KeyDown)
            {
                bool hasSurfaceFocus = focusController?.focusedElement == this;

                // Ensure this element has keyboard control only while it is the focused element.
                if (hasSurfaceFocus && GUIUtility.keyboardControl != _keyboardControlId)
                {
                    GUIUtility.keyboardControl = _keyboardControlId;
                }

                if (!hasSurfaceFocus)
                {
                    return;
                }

                // Process input only when the surface is the actively focused element.
                if (GUIUtility.keyboardControl == _keyboardControlId)
                {
                    OnInputRequested?.Invoke(evt);
                    evt.Use();
                }
            }
        }

        bool TryHandleMousePassthrough(Event evt)
        {
            if (_parser == null || !_parser.IsMouseReportingEnabled)
            {
                return false;
            }

            switch (evt.type)
            {
                case EventType.MouseDown:
                    if (TryTranslateMouseButtonEvent(evt, isRelease: false, isMotion: false, out var pressSequence))
                    {
                        ClearSelection();
                        OnInteractionStarted?.Invoke();
                        GUIUtility.keyboardControl = _keyboardControlId;
                        Focus();
                        OnMouseInputRequested?.Invoke(pressSequence);
                        evt.Use();
                        return true;
                    }

                    return false;

                case EventType.MouseDrag:
                    if (!_parser.IsMouseReportingEnabled || _parser.MouseTrackingMode == TerminalMouseTrackingMode.ButtonPress)
                    {
                        return false;
                    }

                    if (TryTranslateMouseMotionEvent(evt, out var dragSequence))
                    {
                        OnMouseInputRequested?.Invoke(dragSequence);
                        evt.Use();
                        return true;
                    }

                    return false;

                case EventType.MouseMove:
                    if (_parser.MouseTrackingMode != TerminalMouseTrackingMode.AnyMotion)
                    {
                        return false;
                    }

                    if (TryTranslateMouseMoveEvent(evt, out var moveSequence))
                    {
                        OnMouseInputRequested?.Invoke(moveSequence);
                        evt.Use();
                        return true;
                    }

                    return false;

                case EventType.MouseUp:
                    if (TryTranslateMouseButtonEvent(evt, isRelease: true, isMotion: false, out var releaseSequence))
                    {
                        OnMouseInputRequested?.Invoke(releaseSequence);
                        evt.Use();
                        return true;
                    }

                    return false;

                case EventType.ScrollWheel:
                    if (TryTranslateScrollEvent(evt, out var scrollSequence))
                    {
                        OnMouseInputRequested?.Invoke(scrollSequence);
                        evt.Use();
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        static bool TryGetDroppedPaths(out IReadOnlyList<string> droppedPaths)
        {
            droppedPaths = null;

            if (TryGetDroppedPaths(DragAndDrop.paths, out droppedPaths))
            {
                return true;
            }

            if (DragAndDrop.objectReferences == null)
            {
                return false;
            }

            var collectedPaths = new List<string>();
            foreach (var draggedObject in DragAndDrop.objectReferences)
            {
                var candidatePath = AssetDatabase.GetAssetPath(draggedObject);
                if (string.IsNullOrWhiteSpace(candidatePath))
                {
                    continue;
                }

                collectedPaths.Add(candidatePath);
            }

            if (collectedPaths.Count == 0)
            {
                return false;
            }

            droppedPaths = collectedPaths;
            return true;
        }

        static bool TryGetDroppedPaths(IReadOnlyList<string> paths, out IReadOnlyList<string> droppedPaths)
        {
            droppedPaths = null;
            if (paths == null)
            {
                return false;
            }

            var collectedPaths = new List<string>();
            for (var i = 0; i < paths.Count; i++)
            {
                var candidatePath = paths[i];
                if (string.IsNullOrWhiteSpace(candidatePath))
                {
                    continue;
                }

                collectedPaths.Add(candidatePath);
            }

            if (collectedPaths.Count == 0)
            {
                return false;
            }

            droppedPaths = collectedPaths;
            return true;
        }

        bool TryTranslateMouseButtonEvent(Event evt, bool isRelease, bool isMotion, out string sequence)
        {
            sequence = null;
            if (evt.button is < 0 or > 2)
            {
                return false;
            }

            if (!TryGetMouseCellPosition(evt, allowClamp: isRelease || isMotion, out var cell))
            {
                return false;
            }

            sequence = TerminalInputHandler.TranslateMouseButtonEvent(
                _parser.MouseEncoding,
                cell,
                evt.button,
                evt.shift,
                evt.alt,
                evt.control,
                isRelease,
                isMotion);
            return !string.IsNullOrEmpty(sequence);
        }

        bool TryTranslateMouseMotionEvent(Event evt, out string sequence)
        {
            sequence = null;
            return TryTranslateMouseButtonEvent(evt, isRelease: false, isMotion: true, out sequence);
        }

        bool TryTranslateMouseMoveEvent(Event evt, out string sequence)
        {
            sequence = null;
            if (!TryGetMouseCellPosition(evt, allowClamp: true, out var cell))
            {
                return false;
            }

            sequence = TerminalInputHandler.TranslateMouseMoveEvent(
                _parser.MouseEncoding,
                cell,
                evt.shift,
                evt.alt,
                evt.control);
            return !string.IsNullOrEmpty(sequence);
        }

        bool TryTranslateScrollEvent(Event evt, out string sequence)
        {
            sequence = null;
            if (!TryGetMouseCellPosition(evt, allowClamp: true, out var cell))
            {
                return false;
            }

            bool scrollUp = evt.delta.y < 0f;
            sequence = TerminalInputHandler.TranslateMouseScrollEvent(
                _parser.MouseEncoding,
                cell,
                evt.shift,
                evt.alt,
                evt.control,
                scrollUp);
            return !string.IsNullOrEmpty(sequence);
        }

        bool TryGetMouseCellPosition(Event evt, bool allowClamp, out Vector2Int cell)
        {
            cell = default;
            if (evt == null || contentRect.width < 1f || contentRect.height < 1f)
            {
                return false;
            }

            Vector2 localPos = evt.mousePosition - contentRect.position;
            if (!allowClamp)
            {
                return contentRect.Contains(evt.mousePosition) && TryGetCellPosition(localPos, out cell);
            }

            localPos = ClampToContentRect(localPos);
            return TryGetCellPosition(localPos, out cell);
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
                alignment = TextAnchor.MiddleLeft,
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

            MeasureCellMetrics(_cellStyle, out _cellWidth, out _cellHeight);
        }

        static void MeasureCellMetrics(GUIStyle style, out float cellWidth, out float cellHeight)
        {
            float maxNormalizedWidth = 0f;
            float maxHeight = Mathf.Max(1f, style.lineHeight);

            for (int i = 0; i < CellWidthProbeCharacters.Length; i++)
            {
                char probeCharacter = CellWidthProbeCharacters[i];
                var probeContent = new GUIContent(probeCharacter.ToString());
                Vector2 probeSize = style.CalcSize(probeContent);
                maxNormalizedWidth = Mathf.Max(maxNormalizedWidth, probeSize.x);
            }

            for (int i = 0; i < CellHeightProbeCharacters.Length; i++)
            {
                char probeCharacter = CellHeightProbeCharacters[i];
                var probeContent = new GUIContent(probeCharacter.ToString());
                Vector2 probeSize = style.CalcSize(probeContent);
                maxHeight = Mathf.Max(maxHeight, probeSize.y);
            }

            float pixelsPerPoint = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
            float horizontalPadding = 1f / pixelsPerPoint;
            float verticalPadding = 2f / pixelsPerPoint;
            cellWidth = Mathf.Max(1f, SnapToPixel(maxNormalizedWidth + horizontalPadding));
            cellHeight = Mathf.Max(1f, SnapToPixel(maxHeight + verticalPadding));
        }

        void DrawRow(int row, int cols, TerminalTheme theme, Color defaultBackground, GridLayout layout)
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

                bool isContinuation = cell.Codepoint == '\0';
                if (isContinuation)
                {
                    continue;
                }

                int displayWidth = GetCellDisplayWidth(isScrollback, scrollbackRow, displayRow, col, cols);
                bool isWideLead = displayWidth > 1;
                Rect cellRect = GetCellRect(layout, col, row, displayWidth);

                bool bold = (cell.Flags & CellFlags.Bold) != 0;
                Color bgColor = Opaque(cell.Background.ToUnityColor(theme.Palette, theme.DefaultBackground));
                Color fgColor = Opaque(ResolveForegroundWithBoldBright(cell.Foreground, bold, theme));
                if ((cell.Flags & CellFlags.Inverse) != 0)
                {
                    fgColor = Opaque(cell.Background.ToUnityColor(theme.Palette, theme.DefaultBackground));
                    bgColor = Opaque(ResolveForegroundWithBoldBright(cell.Foreground, bold, theme));
                }

                if ((cell.Flags & CellFlags.Dim) != 0)
                {
                    fgColor.r *= 0.6f;
                    fgColor.g *= 0.6f;
                    fgColor.b *= 0.6f;
                }

                bool isSelected = HasSelection && SelectionContains(row, col);
                if (isWideLead && HasSelection && col + 1 < cols && SelectionContains(row, col + 1))
                {
                    isSelected = true;
                }

                if (isSelected)
                {
                    bgColor = new Color(0.33f, 0.52f, 0.88f, 1f);
                    fgColor = Color.white;
                }

                if (bgColor != defaultBackground)
                {
                    EditorGUI.DrawRect(cellRect, bgColor);
                }

                if (cell.Codepoint == ' ')
                {
                    continue;
                }

                GUI.Label(cellRect, cell.Codepoint.ToString(), GetModifiedStyle(cell.Flags, fgColor));
            }
        }

        static Color ResolveForegroundWithBoldBright(TerminalColor foreground, bool bold, TerminalTheme theme)
        {
            if (bold && foreground.ColorKind == TerminalColor.Kind.Named && foreground.R < 8)
            {
                byte brightIndex = (byte)(foreground.R + 8);
                return TerminalColor.Named(brightIndex).ToUnityColor(theme.Palette, theme.DefaultForeground);
            }

            return foreground.ToUnityColor(theme.Palette, theme.DefaultForeground);
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

        void DrawCompositionPreview(TerminalTheme theme, GridLayout layout)
        {
            string compositionString = Input.compositionString;
            if (string.IsNullOrEmpty(compositionString))
            {
                return;
            }

            var cursor = _buffer.Cursor;
            if (cursor.Row < 0 || cursor.Row >= VisibleRows || cursor.Col < 0 || cursor.Col >= VisibleCols)
            {
                return;
            }

            int anchorCol = cursor.Col;
            int displayWidth = 0;
            for (int i = 0; i < compositionString.Length; i++)
            {
                displayWidth += GetDisplayWidth(compositionString[i]);
            }

            if (displayWidth <= 0)
            {
                return;
            }

            Rect previewRect = GetCellRect(layout, anchorCol, cursor.Row, displayWidth);

            EditorGUI.DrawRect(previewRect, Opaque(theme.CursorColor));

            GUI.Label(previewRect, compositionString, GetModifiedStyle(CellFlags.None, Opaque(theme.DefaultBackground)));
        }

        void DrawCursor(TerminalTheme theme, GridLayout layout)
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

            int anchorCol = cursor.Col;
            int cursorWidth = 1;
            var cell = _buffer.GetCell(cursor.Row, cursor.Col);
            if (IsContinuationCell(cell))
            {
                int leadCol = cursor.Col - 1;
                if (leadCol < 0 || !IsWideLeadCell(false, -1, cursor.Row, leadCol, VisibleCols))
                {
                    return;
                }

                anchorCol = leadCol;
                cursorWidth = 2;
                cell = _buffer.GetCell(cursor.Row, leadCol);
            }

            Rect cursorRect = GetCellRect(layout, anchorCol, cursor.Row, cursorWidth);

            EditorGUI.DrawRect(cursorRect, Opaque(theme.CursorColor));

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
            return CellWidth * GetCellDisplayWidth(isScrollback, scrollbackRow, displayRow, col, cols);
        }

        readonly struct GridLayout
        {
            public readonly float TotalWidth;
            public readonly float TotalHeight;
            readonly float[] _columnEdges;
            readonly float[] _rowEdges;

            public GridLayout(float totalWidth, float totalHeight, float[] columnEdges, float[] rowEdges)
            {
                TotalWidth = totalWidth;
                TotalHeight = totalHeight;
                _columnEdges = columnEdges;
                _rowEdges = rowEdges;
            }

            public Rect GetCellRect(int col, int row, int displayWidth)
            {
                int clampedCol = Mathf.Clamp(col, 0, Mathf.Max(0, _columnEdges.Length - 2));
                int endCol = Mathf.Clamp(clampedCol + Mathf.Max(1, displayWidth), clampedCol + 1, _columnEdges.Length - 1);
                int clampedRow = Mathf.Clamp(row, 0, Mathf.Max(0, _rowEdges.Length - 2));
                float xMin = _columnEdges[clampedCol];
                float xMax = _columnEdges[endCol];
                float yMin = _rowEdges[clampedRow];
                float yMax = _rowEdges[clampedRow + 1];
                return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            }
        }

        GridLayout BuildGridLayout()
        {
            int cols = Mathf.Max(1, VisibleCols);
            int rows = Mathf.Max(1, VisibleRows);
            float totalWidth = SnapToPixel(contentRect.width);
            float totalHeight = SnapToPixel(contentRect.height);
            var columnEdges = BuildSnappedEdges(cols, totalWidth);
            var rowEdges = BuildSnappedEdges(rows, totalHeight);
            return new GridLayout(totalWidth, totalHeight, columnEdges, rowEdges);
        }

        static float[] BuildSnappedEdges(int divisions, float totalSize)
        {
            var edges = new float[divisions + 1];
            for (int i = 0; i <= divisions; i++)
            {
                float normalized = divisions == 0 ? 0f : (float)i / divisions;
                edges[i] = i == divisions ? totalSize : SnapToPixel(totalSize * normalized);
            }

            return edges;
        }

        Rect GetCellRect(GridLayout layout, int col, int row, int displayWidth)
        {
            return layout.GetCellRect(col, row, displayWidth);
        }

        static float SnapToPixel(float value)
        {
            float pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            if (pixelsPerPoint <= 0f)
            {
                return value;
            }

            return Mathf.Round(value * pixelsPerPoint) / pixelsPerPoint;
        }

        int GetCellDisplayWidth(bool isScrollback, int scrollbackRow, int displayRow, int col, int cols)
        {
            return IsWideLeadCell(isScrollback, scrollbackRow, displayRow, col, cols) ? 2 : 1;
        }

        bool IsWideLeadCell(bool isScrollback, int scrollbackRow, int displayRow, int col, int cols)
        {
            if (col < 0 || col + 1 >= cols)
            {
                return false;
            }

            TerminalCell cell = isScrollback
                ? _buffer.GetScrollbackCell(scrollbackRow, col)
                : _buffer.GetCell(displayRow, col);

            return !IsContinuationCell(cell)
                && GetDisplayWidth(cell.Codepoint) > 1
                && IsValidContinuationCell(isScrollback, scrollbackRow, displayRow, col + 1, cols);
        }

        bool IsValidContinuationCell(bool isScrollback, int scrollbackRow, int displayRow, int col, int cols)
        {
            if (col <= 0 || col >= cols)
            {
                return false;
            }

            TerminalCell cell = isScrollback
                ? _buffer.GetScrollbackCell(scrollbackRow, col)
                : _buffer.GetCell(displayRow, col);

            TerminalCell leftCell = isScrollback
                ? _buffer.GetScrollbackCell(scrollbackRow, col - 1)
                : _buffer.GetCell(displayRow, col - 1);

            return IsContinuationCell(cell)
                && !IsContinuationCell(leftCell)
                && GetDisplayWidth(leftCell.Codepoint) > 1;
        }

        static bool IsContinuationCell(TerminalCell cell)
        {
            return cell.Codepoint == '\0';
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
            // Platform-specific font candidates, ordered by preference
            // Priority: Nerd Font Mono (monospace) > Monospace base > Nerd Font (fallback)
            string[] preferredFamilies = Application.platform == RuntimePlatform.WindowsEditor
                ? new[]
                {
                    // Nerd Font Mono variants (monospace with symbols)
                    "CaskaydiaCove Nerd Font Mono",
                    "JetBrainsMono Nerd Font Mono",
                    "FiraCode Nerd Font Mono",
                    "Hack Nerd Font Mono",
                    
                    // Monospace base fonts (fallback if Nerd Font not installed)
                    "Consolas",
                    "Courier New",
                    "Lucida Console"
                }
                : new[]
                {
                    // Nerd Font Mono variants (monospace with symbols) - macOS/Linux
                    "MesloLGS NF",
                    "MesloLGSNerdFontMono",
                    "MesloLGS Nerd Font Mono",
                    "JetBrainsMono Nerd Font Mono",
                    "Hack Nerd Font Mono",
                    "HackNerdFontMono",
                    "FiraCodeNerdFontMono",
                    "FiraCode Nerd Font Mono",
                    
                    // Monospace base fonts (fallback if Nerd Font not installed)
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
