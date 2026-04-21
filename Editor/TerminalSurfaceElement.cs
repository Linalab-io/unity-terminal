using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Linalab.Terminal.Editor
{
    sealed class TerminalSurfaceElement : VisualElement
    {
        readonly ITerminalBuffer _buffer;
        AnsiParser _parser;
        TerminalRenderer _renderer;

        bool _isSelecting;
        Vector2Int _selectionStart;
        Vector2Int _selectionEnd;

        VisualElement _backgroundContainer;
        VisualElement _textContainer;
        VisualElement _overlayContainer;

        List<VisualElement> _backgroundPool = new List<VisualElement>();
        List<Label> _textPool = new List<Label>();

        VisualElement _cursorBackground;
        Label _cursorText;
        VisualElement _compositionBackground;
        Label _compositionText;

        public int VisibleCols => _renderer.VisibleCols;
        public int VisibleRows => _renderer.VisibleRows;
        public float CellWidth => _renderer.CellWidth;
        public float CellHeight => _renderer.CellHeight;
        public bool HasSelection => _renderer.HasSelection;

        public event System.Action OnGridSizeChanged;
        public event System.Action<KeyDownEvent> OnInputRequested;
        public event System.Action<string> OnMouseInputRequested;
        public event System.Action<IReadOnlyList<string>> OnDropRequested;
        public event System.Action OnInteractionStarted;

        public TerminalSurfaceElement(ITerminalBuffer buffer, AnsiParser parser)
        {
            _buffer = buffer;
            _parser = parser;
            _renderer = new TerminalRenderer(buffer);
            
            focusable = true;
            tabIndex = 0;
            pickingMode = PickingMode.Position;
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
            
            _backgroundContainer = new VisualElement { pickingMode = PickingMode.Ignore };
            _backgroundContainer.style.position = Position.Absolute;
            _backgroundContainer.style.left = 0;
            _backgroundContainer.style.top = 0;
            _backgroundContainer.style.right = 0;
            _backgroundContainer.style.bottom = 0;
            Add(_backgroundContainer);

            _textContainer = new VisualElement { pickingMode = PickingMode.Ignore };
            _textContainer.style.position = Position.Absolute;
            _textContainer.style.left = 0;
            _textContainer.style.top = 0;
            _textContainer.style.right = 0;
            _textContainer.style.bottom = 0;
            Add(_textContainer);

            _overlayContainer = new VisualElement { pickingMode = PickingMode.Ignore };
            _overlayContainer.style.position = Position.Absolute;
            _overlayContainer.style.left = 0;
            _overlayContainer.style.top = 0;
            _overlayContainer.style.right = 0;
            _overlayContainer.style.bottom = 0;
            Add(_overlayContainer);

            _cursorBackground = new VisualElement { style = { position = Position.Absolute, display = DisplayStyle.None } };
            _cursorText = new Label { style = { position = Position.Absolute, display = DisplayStyle.None, paddingLeft = 0, paddingRight = 0, paddingTop = 0, paddingBottom = 0, marginLeft = 0, marginRight = 0, marginTop = 0, marginBottom = 0 } };
            _compositionBackground = new VisualElement { style = { position = Position.Absolute, display = DisplayStyle.None } };
            _compositionText = new Label { style = { position = Position.Absolute, display = DisplayStyle.None, paddingLeft = 0, paddingRight = 0, paddingTop = 0, paddingBottom = 0, marginLeft = 0, marginRight = 0, marginTop = 0, marginBottom = 0 } };
            
            _overlayContainer.Add(_cursorBackground);
            _overlayContainer.Add(_cursorText);
            _overlayContainer.Add(_compositionBackground);
            _overlayContainer.Add(_compositionText);

            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RegisterCallback<MouseDownEvent>(OnMouseDownEvent);
            RegisterCallback<MouseUpEvent>(OnMouseUpEvent);
            RegisterCallback<MouseMoveEvent>(OnMouseMoveEvent);
            RegisterCallback<WheelEvent>(OnWheelEvent);
            RegisterCallback<KeyDownEvent>(OnKeyDownEvent);
            RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            RegisterCallback<DragPerformEvent>(OnDragPerform);
        }

        public void SetMouseProtocolSource(AnsiParser parser)
        {
            _parser = parser;
        }

        void OnMouseDownEvent(MouseDownEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            if (TryHandleMousePassthrough(evt.button, evt.localMousePosition, evt.shiftKey, evt.altKey, evt.ctrlKey, isRelease: false, isMotion: false))
            {
                evt.StopPropagation();
                return;
            }

            if (evt.button != 0)
            {
                return;
            }

            OnInteractionStarted?.Invoke();
            Focus();
            
            if (TryGetCellPosition(evt.localMousePosition, out var cell))
            {
                _isSelecting = true;
                _selectionStart = cell;
                _selectionEnd = cell;
                _renderer.SetSelection(_selectionStart, _selectionEnd);
                MarkDirtyRepaint();
                evt.StopPropagation();
            }
        }

        void OnMouseUpEvent(MouseUpEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            if (TryHandleMousePassthrough(evt.button, evt.localMousePosition, evt.shiftKey, evt.altKey, evt.ctrlKey, isRelease: true, isMotion: false))
            {
                evt.StopPropagation();
                return;
            }

            if (_isSelecting && evt.button == 0)
            {
                _isSelecting = false;
                Vector2 localPos = ClampToContentRect(evt.localMousePosition);
                if (TryGetCellPosition(localPos, out var cell))
                {
                    _selectionEnd = cell;
                    _renderer.SetSelection(_selectionStart, _selectionEnd);
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
                evt.StopPropagation();
            }
        }

        void OnMouseMoveEvent(MouseMoveEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            if (_parser != null && _parser.IsMouseReportingEnabled)
            {
                if (_parser.MouseTrackingMode == TerminalMouseTrackingMode.AnyMotion)
                {
                    if (TryTranslateMouseMoveEvent(evt.localMousePosition, evt.shiftKey, evt.altKey, evt.ctrlKey, out var moveSequence))
                    {
                        OnMouseInputRequested?.Invoke(moveSequence);
                        evt.StopPropagation();
                        return;
                    }
                }
                else if (_parser.MouseTrackingMode == TerminalMouseTrackingMode.ButtonDrag && (evt.pressedButtons & 1) != 0)
                {
                    if (TryHandleMousePassthrough(0, evt.localMousePosition, evt.shiftKey, evt.altKey, evt.ctrlKey, isRelease: false, isMotion: true))
                    {
                        evt.StopPropagation();
                        return;
                    }
                }
            }

            if (_isSelecting && (evt.pressedButtons & 1) != 0)
            {
                Vector2 localPos = ClampToContentRect(evt.localMousePosition);
                if (TryGetCellPosition(localPos, out var cell))
                {
                    _selectionEnd = cell;
                    _renderer.SetSelection(_selectionStart, _selectionEnd);
                    MarkDirtyRepaint();
                }

                evt.StopPropagation();
            }
        }

        void OnWheelEvent(WheelEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            if (_parser != null && _parser.IsMouseReportingEnabled)
            {
                if (TryTranslateScrollEvent(evt.localMousePosition, evt.shiftKey, evt.altKey, evt.ctrlKey, evt.delta.y < 0f, out var scrollSequence))
                {
                    OnMouseInputRequested?.Invoke(scrollSequence);
                    evt.StopPropagation();
                    return;
                }
            }

            int scrollDelta = evt.delta.y > 0 ? -3 : 3;
            AdjustScroll(scrollDelta);
            evt.StopPropagation();
        }

        void OnKeyDownEvent(KeyDownEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            OnInputRequested?.Invoke(evt);
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
            _renderer.InvalidateStyle();
            MarkDirtyRepaint();
        }

        public void ScrollToBottom()
        {
            _renderer.ScrollToBottom();
            MarkDirtyRepaint();
        }

        public bool TryGetCursorRect(out Rect cursorRect)
        {
            cursorRect = default;
            if (_buffer == null || contentRect.width < 1f || contentRect.height < 1f)
            {
                return false;
            }

            UpdateGridSize();
            var snapshot = _renderer.BuildSnapshot(Input.compositionString);
            
            if (snapshot.CompositionPreview.Visible)
            {
                cursorRect = new Rect(
                    snapshot.CompositionPreview.Col * snapshot.CellWidth,
                    snapshot.CompositionPreview.Row * snapshot.CellHeight,
                    snapshot.CompositionPreview.DisplayWidth * snapshot.CellWidth,
                    snapshot.CellHeight);
                return true;
            }

            if (!snapshot.Cursor.Visible)
            {
                return false;
            }

            cursorRect = new Rect(
                snapshot.Cursor.Col * snapshot.CellWidth,
                snapshot.Cursor.Row * snapshot.CellHeight,
                snapshot.Cursor.DisplayWidth * snapshot.CellWidth,
                snapshot.CellHeight);
            return true;
        }

        public void AdjustScroll(int delta)
        {
            _renderer.AdjustScroll(delta);
            if (HasSelection)
            {
                ClearSelection();
                return;
            }

            MarkDirtyRepaint();
        }

        public void ClearSelection()
        {
            _renderer.ClearSelection();
            _isSelecting = false;
            MarkDirtyRepaint();
        }

        public bool TryGetCellPosition(Vector2 localMousePos, out Vector2Int position)
        {
            return _renderer.TryGetCellPosition(contentRect, localMousePos, out position);
        }

        public string GetSelectedText()
        {
            return _renderer.GetSelectedText();
        }

        public bool UpdateGridSize()
        {
            if (contentRect.width < 1f || contentRect.height < 1f)
            {
                return false;
            }

            if (_renderer.CalculateGridSize(contentRect))
            {
                if (HasSelection)
                {
                    ClearSelection();
                }
                OnGridSizeChanged?.Invoke();
                return true;
            }

            return false;
        }

        public new void MarkDirtyRepaint()
        {
            UpdateVisualElements();
            base.MarkDirtyRepaint();
        }

        void UpdateVisualElements()
        {
            if (contentRect.width < 1f || contentRect.height < 1f || _buffer == null)
            {
                return;
            }

            UpdateGridSize();
            var snapshot = _renderer.BuildSnapshot(Input.compositionString);
            
            style.backgroundColor = snapshot.DefaultBackground;

            int activeBackgrounds = 0;
            int activeTexts = 0;

            foreach (var row in snapshot.Rows)
            {
                foreach (var bg in row.Backgrounds)
                {
                    var el = GetBackgroundElement(ref activeBackgrounds);
                    el.style.left = bg.StartCol * snapshot.CellWidth;
                    el.style.top = row.RowIndex * snapshot.CellHeight;
                    el.style.width = bg.DisplayWidth * snapshot.CellWidth;
                    el.style.height = snapshot.CellHeight;
                    el.style.backgroundColor = bg.Color;
                }

                foreach (var run in row.TextRuns)
                {
                    var el = GetTextElement(ref activeTexts);
                    el.style.left = run.StartCol * snapshot.CellWidth;
                    el.style.top = row.RowIndex * snapshot.CellHeight;
                    el.style.width = run.DisplayWidth * snapshot.CellWidth;
                    el.style.height = snapshot.CellHeight;
                    el.text = run.Text;
                    el.style.color = run.Foreground;
                    
                    FontStyle fontStyle = FontStyle.Normal;
                    if ((run.Flags & CellFlags.Bold) != 0 && (run.Flags & CellFlags.Italic) != 0)
                        fontStyle = FontStyle.BoldAndItalic;
                    else if ((run.Flags & CellFlags.Bold) != 0)
                        fontStyle = FontStyle.Bold;
                    else if ((run.Flags & CellFlags.Italic) != 0)
                        fontStyle = FontStyle.Italic;
                        
                    el.style.unityFontStyleAndWeight = new StyleEnum<FontStyle>(fontStyle);
                    el.style.fontSize = TerminalSettings.FontSize;
                    el.style.unityFont = new StyleFont(_renderer.GetFont());
                    el.style.unityFontDefinition = new StyleFontDefinition(StyleKeyword.None);
                }
            }

            _cursorBackground.style.display = DisplayStyle.None;
            _cursorText.style.display = DisplayStyle.None;
            _compositionBackground.style.display = DisplayStyle.None;
            _compositionText.style.display = DisplayStyle.None;

            if (snapshot.CompositionPreview.Visible)
            {
                _compositionBackground.style.display = DisplayStyle.Flex;
                _compositionBackground.style.left = snapshot.CompositionPreview.Col * snapshot.CellWidth;
                _compositionBackground.style.top = snapshot.CompositionPreview.Row * snapshot.CellHeight;
                _compositionBackground.style.width = snapshot.CompositionPreview.DisplayWidth * snapshot.CellWidth;
                _compositionBackground.style.height = snapshot.CellHeight;
                _compositionBackground.style.backgroundColor = snapshot.CursorColor;

                _compositionText.style.display = DisplayStyle.Flex;
                _compositionText.style.left = snapshot.CompositionPreview.Col * snapshot.CellWidth;
                _compositionText.style.top = snapshot.CompositionPreview.Row * snapshot.CellHeight;
                _compositionText.style.width = snapshot.CompositionPreview.DisplayWidth * snapshot.CellWidth;
                _compositionText.style.height = snapshot.CellHeight;
                _compositionText.text = snapshot.CompositionPreview.Text;
                _compositionText.style.color = snapshot.DefaultBackground;
                _compositionText.style.unityFontStyleAndWeight = new StyleEnum<FontStyle>(FontStyle.Normal);
                _compositionText.style.fontSize = TerminalSettings.FontSize;
                _compositionText.style.unityFont = new StyleFont(_renderer.GetFont());
                _compositionText.style.unityFontDefinition = new StyleFontDefinition(StyleKeyword.None);
            }
            else if (snapshot.Cursor.Visible)
            {
                _cursorBackground.style.display = DisplayStyle.Flex;
                _cursorBackground.style.left = snapshot.Cursor.Col * snapshot.CellWidth;
                _cursorBackground.style.top = snapshot.Cursor.Row * snapshot.CellHeight;
                _cursorBackground.style.width = snapshot.Cursor.DisplayWidth * snapshot.CellWidth;
                _cursorBackground.style.height = snapshot.CellHeight;
                _cursorBackground.style.backgroundColor = snapshot.CursorColor;

                if (!string.IsNullOrEmpty(snapshot.Cursor.Text))
                {
                    _cursorText.style.display = DisplayStyle.Flex;
                    _cursorText.style.left = snapshot.Cursor.Col * snapshot.CellWidth;
                    _cursorText.style.top = snapshot.Cursor.Row * snapshot.CellHeight;
                    _cursorText.style.width = snapshot.Cursor.DisplayWidth * snapshot.CellWidth;
                    _cursorText.style.height = snapshot.CellHeight;
                    _cursorText.text = snapshot.Cursor.Text;
                    _cursorText.style.color = snapshot.DefaultBackground;
                    _cursorText.style.unityFontStyleAndWeight = new StyleEnum<FontStyle>(FontStyle.Normal);
                    _cursorText.style.fontSize = TerminalSettings.FontSize;
                    _cursorText.style.unityFont = new StyleFont(_renderer.GetFont());
                    _cursorText.style.unityFontDefinition = new StyleFontDefinition(StyleKeyword.None);
                }
            }

            for (int i = activeBackgrounds; i < _backgroundPool.Count; i++)
            {
                _backgroundPool[i].style.display = DisplayStyle.None;
            }

            for (int i = activeTexts; i < _textPool.Count; i++)
            {
                _textPool[i].style.display = DisplayStyle.None;
            }
        }

        VisualElement GetBackgroundElement(ref int activeCount)
        {
            VisualElement el;
            if (activeCount < _backgroundPool.Count)
            {
                el = _backgroundPool[activeCount];
                el.style.display = DisplayStyle.Flex;
            }
            else
            {
                el = new VisualElement();
                el.style.position = Position.Absolute;
                _backgroundContainer.Add(el);
                _backgroundPool.Add(el);
            }
            activeCount++;
            return el;
        }

        Label GetTextElement(ref int activeCount)
        {
            Label el;
            if (activeCount < _textPool.Count)
            {
                el = _textPool[activeCount];
                el.style.display = DisplayStyle.Flex;
            }
            else
            {
                el = new Label();
                el.style.position = Position.Absolute;
                el.style.paddingLeft = 0;
                el.style.paddingRight = 0;
                el.style.paddingTop = 0;
                el.style.paddingBottom = 0;
                el.style.marginLeft = 0;
                el.style.marginRight = 0;
                el.style.marginTop = 0;
                el.style.marginBottom = 0;
                _textContainer.Add(el);
                _textPool.Add(el);
            }
            activeCount++;
            return el;
        }

        bool TryHandleMousePassthrough(int button, Vector2 localMousePos, bool shift, bool alt, bool control, bool isRelease, bool isMotion)
        {
            if (_parser == null || !_parser.IsMouseReportingEnabled)
            {
                return false;
            }

            if (TryTranslateMouseButtonEvent(button, localMousePos, shift, alt, control, isRelease, isMotion, out var sequence))
            {
                if (!isMotion)
                {
                    ClearSelection();
                    OnInteractionStarted?.Invoke();
                    Focus();
                }
                OnMouseInputRequested?.Invoke(sequence);
                return true;
            }

            return false;
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

        bool TryTranslateMouseButtonEvent(int button, Vector2 localMousePos, bool shift, bool alt, bool control, bool isRelease, bool isMotion, out string sequence)
        {
            sequence = null;
            if (button is < 0 or > 2)
            {
                return false;
            }

            if (!TryGetMouseCellPosition(localMousePos, allowClamp: isRelease || isMotion, out var cell))
            {
                return false;
            }

            sequence = TerminalInputHandler.TranslateMouseButtonEvent(
                _parser.MouseEncoding,
                cell,
                button,
                shift,
                alt,
                control,
                isRelease,
                isMotion);
            return !string.IsNullOrEmpty(sequence);
        }

        bool TryTranslateMouseMoveEvent(Vector2 localMousePos, bool shift, bool alt, bool control, out string sequence)
        {
            sequence = null;
            if (!TryGetMouseCellPosition(localMousePos, allowClamp: true, out var cell))
            {
                return false;
            }

            sequence = TerminalInputHandler.TranslateMouseMoveEvent(
                _parser.MouseEncoding,
                cell,
                shift,
                alt,
                control);
            return !string.IsNullOrEmpty(sequence);
        }

        bool TryTranslateScrollEvent(Vector2 localMousePos, bool shift, bool alt, bool control, bool scrollUp, out string sequence)
        {
            sequence = null;
            if (!TryGetMouseCellPosition(localMousePos, allowClamp: true, out var cell))
            {
                return false;
            }

            sequence = TerminalInputHandler.TranslateMouseScrollEvent(
                _parser.MouseEncoding,
                cell,
                shift,
                alt,
                control,
                scrollUp);
            return !string.IsNullOrEmpty(sequence);
        }

        bool TryGetMouseCellPosition(Vector2 localMousePos, bool allowClamp, out Vector2Int cell)
        {
            cell = default;
            if (contentRect.width < 1f || contentRect.height < 1f)
            {
                return false;
            }

            if (!allowClamp)
            {
                return contentRect.Contains(localMousePos) && TryGetCellPosition(localMousePos, out cell);
            }

            Vector2 localPos = ClampToContentRect(localMousePos);
            return TryGetCellPosition(localPos, out cell);
        }

        Vector2 ClampToContentRect(Vector2 point)
        {
            return new Vector2(
                Mathf.Clamp(point.x, contentRect.xMin, contentRect.xMax - 1f),
                Mathf.Clamp(point.y, contentRect.yMin, contentRect.yMax - 1f));
        }
    }
}
