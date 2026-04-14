using UnityEditor;
using UnityEngine;

namespace Linalab.Terminal.Editor
{
    public sealed class TerminalEditorWindow : EditorWindow
    {
        const string MenuPath = "Tools/Unity Editor Terminal";
        const int DefaultRows = 24;
        const int DefaultCols = 80;
        const float PollIntervalMs = 16f;
        const float HorizontalPadding = 8f;
        const float VerticalPadding = 6f;

        TerminalBuffer _buffer;
        AnsiParser _parser;
        ShellProcess _shellProcess;
        TerminalRenderer _renderer;
        Rect _lastTerminalRect;
        bool _initialized;
        bool _needsResize;
        bool _terminalFocused;
        bool _isSelecting;
        Vector2Int _selectionStart;
        double _lastPollTime;
        double _notificationHideAt;
        int _lastAppliedFontSize;
        string _lastAppliedEffectiveFontFamily;

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            var window = GetWindow<TerminalEditorWindow>();
            window.titleContent = new GUIContent("Unity Terminal", EditorGUIUtility.IconContent("d_UnityEditor.ConsoleWindow").image);
            window.minSize = new Vector2(400f, 240f);
            window.Show();
        }

        void OnEnable()
        {
            InitializeTerminal();
        }

        void OnDisable()
        {
            Cleanup();
        }

        void OnDestroy()
        {
            Cleanup();
        }

        void InitializeTerminal()
        {
            if (_initialized)
            {
                return;
            }

            _buffer = new TerminalBuffer(DefaultRows, DefaultCols, TerminalSettings.ScrollbackLimit);
            _parser = new AnsiParser(_buffer);
            _renderer = new TerminalRenderer(_buffer);
            _shellProcess = new ShellProcess(TerminalSettings.ResolveShellPath());
            _parser.ResponseCallback = response => _shellProcess?.Write(response);
            _lastAppliedFontSize = -1;
            _lastAppliedEffectiveFontFamily = null;

            string cwd = TerminalSettings.GetWorkspaceDirectory();
            _shellProcess.Start(cwd);
            EditorApplication.update += OnEditorUpdate;
            _initialized = true;
        }

        void OnEditorUpdate()
        {
            if (_shellProcess == null || !_initialized)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if ((now - _lastPollTime) * 1000d < PollIntervalMs)
            {
                return;
            }

            _lastPollTime = now;
            bool hadOutput = false;

            _shellProcess.DrainOutput(data =>
            {
                _parser.Feed(data);
                hadOutput = true;
            });

            _shellProcess.DrainErrors(data =>
            {
                _parser.Feed(data);
                hadOutput = true;
            });

            if (hadOutput)
            {
                _renderer.ScrollToBottom();
            }

            if (_needsResize && _renderer != null)
            {
                _needsResize = false;
                int newCols = Mathf.Max(1, _renderer.VisibleCols);
                int newRows = Mathf.Max(1, _renderer.VisibleRows);
                if (newCols != _buffer.Cols || newRows != _buffer.Rows)
                {
                    _buffer.Resize(newRows, newCols);
                    _shellProcess.Resize(newCols, newRows);
                }
            }

            if (_notificationHideAt > 0d && now >= _notificationHideAt)
            {
                RemoveNotification();
                _notificationHideAt = 0d;
            }

            Repaint();
        }

        void OnGUI()
        {
            if (!_initialized)
            {
                InitializeTerminal();
            }

            DrawToolbar();

            var terminalArea = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            var paddedTerminalArea = new Rect(
                terminalArea.x + HorizontalPadding,
                terminalArea.y + VerticalPadding,
                Mathf.Max(1f, terminalArea.width - (HorizontalPadding * 2f)),
                Mathf.Max(1f, terminalArea.height - (VerticalPadding * 2f)));

            if (paddedTerminalArea.width < 1f || paddedTerminalArea.height < 1f || _buffer == null || _renderer == null)
            {
                return;
            }

            RefreshRendererStyleIfNeeded();

            if (paddedTerminalArea != _lastTerminalRect)
            {
                _lastTerminalRect = paddedTerminalArea;
                _renderer.CalculateGridSize(paddedTerminalArea);
                _needsResize = true;
            }

            HandleInput(paddedTerminalArea);
            _renderer.Draw(paddedTerminalArea);
        }

        void RefreshRendererStyleIfNeeded()
        {
            if (_renderer == null)
            {
                return;
            }

            string effectiveFontFamily = TerminalSettings.GetEffectiveFontFamily();
            if (_lastAppliedFontSize == TerminalSettings.FontSize
                && string.Equals(_lastAppliedEffectiveFontFamily, effectiveFontFamily, System.StringComparison.Ordinal))
            {
                return;
            }

            _lastAppliedFontSize = TerminalSettings.FontSize;
            _lastAppliedEffectiveFontFamily = effectiveFontFamily;
            _renderer.InvalidateStyle();
            if (_lastTerminalRect.width > 0f && _lastTerminalRect.height > 0f)
            {
                _renderer.CalculateGridSize(_lastTerminalRect);
            }

            _needsResize = true;
        }

        void DrawToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Restart", EditorStyles.toolbarButton, GUILayout.Width(60f)))
            {
                RestartShell();
            }

            if (GUILayout.Button("Settings", EditorStyles.toolbarButton, GUILayout.Width(64f)))
            {
                TerminalSettingsProvider.Open();
            }

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(60f)))
            {
                ClearTerminal();
            }

            if (GUILayout.Button("A-", EditorStyles.toolbarButton, GUILayout.Width(32f)))
            {
                DecreaseFontSize();
            }

            if (GUILayout.Button("A+", EditorStyles.toolbarButton, GUILayout.Width(32f)))
            {
                IncreaseFontSize();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(_terminalFocused ? "Focused" : "Click terminal to focus", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();
        }

        void HandleInput(Rect terminalArea)
        {
            var evt = Event.current;
            if (evt == null)
            {
                return;
            }

            if (evt.type == EventType.MouseDown)
            {
                _terminalFocused = terminalArea.Contains(evt.mousePosition);
                if (_terminalFocused && evt.button == 0 && _renderer.TryGetCellPosition(terminalArea, evt.mousePosition, out var startCell))
                {
                    _isSelecting = true;
                    _selectionStart = startCell;
                    _renderer.SetSelection(startCell, startCell);
                    evt.Use();
                    return;
                }
            }

            if (_isSelecting && evt.type == EventType.MouseDrag && evt.button == 0)
            {
                if (_renderer.TryGetCellPosition(terminalArea, ClampToRect(terminalArea, evt.mousePosition), out var dragCell))
                {
                    _renderer.SetSelection(_selectionStart, dragCell);
                }

                evt.Use();
                return;
            }

            if (_isSelecting && evt.type == EventType.MouseUp && evt.button == 0)
            {
                _isSelecting = false;
                if (_renderer.TryGetCellPosition(terminalArea, ClampToRect(terminalArea, evt.mousePosition), out var endCell))
                {
                    _renderer.SetSelection(_selectionStart, endCell);
                }

                if (_renderer != null && _renderer.HasSelection)
                {
                    string copiedText = _renderer.GetSelectedText();
                    if (!string.IsNullOrEmpty(copiedText))
                    {
                        EditorGUIUtility.systemCopyBuffer = copiedText;
                        ShowNotification(new GUIContent("Copied"));
                        _notificationHideAt = EditorApplication.timeSinceStartup + 1.2d;
                    }

                    _renderer.ClearSelection();
                }

                evt.Use();
                return;
            }

            if (evt.type == EventType.ScrollWheel && terminalArea.Contains(evt.mousePosition))
            {
                int scrollDelta = evt.delta.y > 0 ? -3 : 3;
                _renderer.AdjustScroll(scrollDelta);
                evt.Use();
                return;
            }

            if (!_terminalFocused || evt.type != EventType.KeyDown)
            {
                return;
            }

            bool isCommandModifier = Application.platform == RuntimePlatform.OSXEditor ? evt.command : evt.control;
            if (isCommandModifier && evt.keyCode == KeyCode.C && _renderer != null && _renderer.HasSelection)
            {
                EditorGUIUtility.systemCopyBuffer = _renderer.GetSelectedText();
                evt.Use();
                return;
            }

            if (isCommandModifier && evt.keyCode == KeyCode.V)
            {
                string paste = TerminalInputHandler.GetPasteText();
                if (!string.IsNullOrEmpty(paste))
                {
                    _shellProcess?.Write(paste);
                    _renderer?.ScrollToBottom();
                }

                evt.Use();
                return;
            }

            string translated = TerminalInputHandler.TranslateKeyEvent(evt);
            if (translated == null)
            {
                return;
            }

            _shellProcess?.Write(translated);
            _renderer?.ScrollToBottom();
            evt.Use();
        }

        static Vector2 ClampToRect(Rect rect, Vector2 point)
        {
            return new Vector2(
                Mathf.Clamp(point.x, rect.xMin, rect.xMax - 1f),
                Mathf.Clamp(point.y, rect.yMin, rect.yMax - 1f));
        }

        void RestartShell()
        {
            _shellProcess?.Kill();
            _shellProcess?.Dispose();
            _buffer?.Reset();
            _renderer?.ClearSelection();
            _shellProcess = new ShellProcess(TerminalSettings.ResolveShellPath());
            _parser = new AnsiParser(_buffer);
            _parser.ResponseCallback = response => _shellProcess?.Write(response);
            string cwd = TerminalSettings.GetWorkspaceDirectory();
            _shellProcess.Start(cwd);
        }

        void ClearTerminal()
        {
            _buffer?.Reset();
            _renderer?.ClearSelection();
        }

        void IncreaseFontSize()
        {
            TerminalSettings.FontSize += 1;
            _renderer?.InvalidateStyle();
            if (_lastTerminalRect.width > 0f && _lastTerminalRect.height > 0f)
            {
                _renderer?.CalculateGridSize(_lastTerminalRect);
            }
            _needsResize = true;
        }

        void DecreaseFontSize()
        {
            TerminalSettings.FontSize -= 1;
            _renderer?.InvalidateStyle();
            if (_lastTerminalRect.width > 0f && _lastTerminalRect.height > 0f)
            {
                _renderer?.CalculateGridSize(_lastTerminalRect);
            }
            _needsResize = true;
        }

        void Cleanup()
        {
            EditorApplication.update -= OnEditorUpdate;
            _shellProcess?.Dispose();
            _shellProcess = null;
            _parser = null;
            _buffer = null;
            _renderer = null;
            _initialized = false;
        }
    }
}
