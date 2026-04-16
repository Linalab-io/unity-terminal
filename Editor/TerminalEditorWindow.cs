using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Linalab;

namespace Linalab.Terminal.Editor
{
    public sealed class TerminalEditorWindow : EditorWindow
    {
        const string MenuPath = "Tools/Unity Editor Terminal";
        const int DefaultRows = 24;
        const int DefaultCols = 80;
        const int MinimumUsableRows = 2;
        const int MinimumUsableCols = 8;
        const float PollIntervalMs = 16f;

        TerminalBuffer _buffer;
        AnsiParser _parser;
        ShellProcess _shellProcess;
        TerminalSurfaceElement _terminalSurface;
        IMGUIContainer _toolbarContainer;
        VisualElement _surfaceContainer;
        bool _initialized;
        bool _needsResize;
        bool _terminalFocused;
        bool _rootInputCallbacksRegistered;
        bool _editorUpdateSubscribed;
        bool _shellPumpScheduled;
        bool _surfaceAttachPending;
        bool _loggedBufferPreview;
        double _lastPollTime;
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

        void CreateGUI()
        {
            if (!_initialized)
            {
                InitializeTerminal();
            }

            EnsureRootLayout();
            EnsureRootInputCallbacks();
            EnsureToolbar();
            EnsureSurfaceContainer();
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            EnsureScheduledShellPump();

            if (_terminalSurface == null && _buffer != null)
            {
                CreateTerminalSurface();
            }

            AttachSurfaceToContainer();
        }

        void EnsureRootLayout()
        {
            if (rootVisualElement == null)
            {
                return;
            }

            if (_toolbarContainer != null && _toolbarContainer.parent != rootVisualElement)
            {
                _toolbarContainer = null;
            }

            if (_surfaceContainer != null && _surfaceContainer.parent != rootVisualElement)
            {
                _surfaceContainer = null;
            }
        }

        void EnsureRootInputCallbacks()
        {
            if (rootVisualElement == null || _rootInputCallbacksRegistered)
            {
                return;
            }

            rootVisualElement.RegisterCallback<MouseDownEvent>(OnRootMouseDown, TrickleDown.TrickleDown);
            _rootInputCallbacksRegistered = true;
        }

        void EnsureToolbar()
        {
            if (rootVisualElement == null || _toolbarContainer != null)
            {
                return;
            }

            _toolbarContainer = new IMGUIContainer(DrawToolbar);
            _toolbarContainer.style.flexShrink = 0;
            rootVisualElement.Add(_toolbarContainer);
        }

        void EnsureSurfaceContainer()
        {
            if (rootVisualElement == null || _surfaceContainer != null)
            {
                return;
            }

            _surfaceContainer = new VisualElement
            {
                style =
                {
                    flexGrow = 1,
                    flexShrink = 1,
                    flexBasis = 0f,
                    minWidth = 0f,
                    minHeight = 0f,
                    paddingLeft = 8f,
                    paddingRight = 8f,
                    paddingTop = 6f,
                    paddingBottom = 6f
                }
            };
            rootVisualElement.Add(_surfaceContainer);
        }

        void CreateTerminalSurface()
        {
            if (_terminalSurface != null || _buffer == null)
            {
                return;
            }

            _terminalSurface = new TerminalSurfaceElement(_buffer);
            _terminalSurface.OnGridSizeChanged += OnGridSizeChanged;
            _terminalSurface.OnInputRequested += HandleKeyInput;
            _terminalSurface.OnInteractionStarted += OnSurfaceInteractionStarted;
            _terminalSurface.RegisterCallback<FocusInEvent>(OnSurfaceFocusIn);
            _terminalSurface.RegisterCallback<FocusOutEvent>(OnSurfaceFocusOut);
        }

        void AttachSurfaceToContainer()
        {
            if (_terminalSurface == null)
            {
                return;
            }

            EnsureSurfaceContainer();
            if (_surfaceContainer == null || _terminalSurface.parent == _surfaceContainer)
            {
                return;
            }

            _terminalSurface.RemoveFromHierarchy();
            _surfaceContainer.Clear();
            _surfaceContainer.Add(_terminalSurface);
            _terminalFocused = true;
            _terminalSurface.schedule.Execute(() => _terminalSurface?.Focus()).StartingIn(0);
            _needsResize = true;
        }

        void EnsureScheduledShellPump()
        {
            if (_shellPumpScheduled || rootVisualElement == null)
            {
                return;
            }

            rootVisualElement.schedule.Execute(OnEditorUpdate).Every((long)PollIntervalMs);
            _shellPumpScheduled = true;
        }

        // Creates and attaches the terminal surface if it is missing.
        // Called from DrawToolbar so it runs even when CreateGUI executed before
        // the buffer was ready (e.g. after a domain reload race condition).
        void EnsureSurface()
        {
            if (_terminalSurface != null || _buffer == null || _surfaceAttachPending)
            {
                return;
            }

            _surfaceAttachPending = true;
            rootVisualElement.schedule.Execute(() =>
            {
                _surfaceAttachPending = false;
                if (_terminalSurface != null || _buffer == null || rootVisualElement == null)
                {
                    return;
                }

                EnsureRootLayout();
                EnsureToolbar();
                CreateTerminalSurface();
                AttachSurfaceToContainer();
                Repaint();
            }).StartingIn(0);
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

        void OnSurfaceFocusIn(FocusInEvent evt)
        {
            _terminalFocused = true;
            _terminalSurface?.MarkDirtyRepaint();
        }

        void OnSurfaceFocusOut(FocusOutEvent evt)
        {
            _terminalFocused = false;
            _terminalSurface?.MarkDirtyRepaint();
        }

        void OnSurfaceInteractionStarted()
        {
            _terminalFocused = true;
            _terminalSurface?.Focus();
            _terminalSurface?.MarkDirtyRepaint();
            Repaint();
        }

        void OnRootMouseDown(MouseDownEvent evt)
        {
            if (_terminalSurface == null || evt == null)
            {
                return;
            }

            var target = evt.target as VisualElement;
            if (target == null)
            {
                return;
            }

            if (target == _terminalSurface || _terminalSurface.Contains(target))
            {
                return;
            }

            _terminalFocused = false;
            _terminalSurface.MarkDirtyRepaint();
        }

        void InitializeTerminal()
        {
            if (_initialized)
            {
                return;
            }

            _buffer = new TerminalBuffer(DefaultRows, DefaultCols, TerminalSettings.ScrollbackLimit);
            _parser = new AnsiParser(_buffer);
            _shellProcess = new ShellProcess(TerminalSettings.ResolveShellPath());
            _parser.ResponseCallback = response => _shellProcess?.Write(response);
            _lastAppliedFontSize = -1;
            _lastAppliedEffectiveFontFamily = null;

            EnsureEditorUpdateSubscription();
            _initialized = true;
        }

        void EnsureEditorUpdateSubscription()
        {
            if (_editorUpdateSubscribed)
            {
                return;
            }

            EditorApplication.update += OnEditorUpdate;
            _editorUpdateSubscribed = true;
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
                if (!_loggedBufferPreview && _buffer != null)
                {
                    _loggedBufferPreview = true;
                    D.Log($"[Terminal] Buffer preview after output: {BuildBufferPreview()}");
                }

                _terminalSurface?.ScrollToBottom();
                _terminalSurface?.MarkDirtyRepaint();
            }

            if (_needsResize && _terminalSurface != null)
            {
                int newCols = Mathf.Max(1, _terminalSurface.VisibleCols);
                int newRows = Mathf.Max(1, _terminalSurface.VisibleRows);

                if (!HasUsableShellSize(newCols, newRows))
                {
                    Repaint();
                    return;
                }

                // Buffer may already be the right size from OnGridSizeChanged; resize
                // here only if it isn't (e.g. first start before geometry is known).
                if (newCols != _buffer.Cols || newRows != _buffer.Rows)
                {
                    _buffer.Resize(newRows, newCols);
                    _terminalSurface.MarkDirtyRepaint();
                }

                _shellProcess.TryResize(newCols, newRows);
                _needsResize = false;
            }

            Repaint();
        }

        void OnGridSizeChanged()
        {
            // Immediately resize the buffer so the renderer fills the new area
            // in the same frame without waiting for the next OnEditorUpdate cycle.
            if (_buffer != null && _terminalSurface != null)
            {
                int newCols = Mathf.Max(1, _terminalSurface.VisibleCols);
                int newRows = Mathf.Max(1, _terminalSurface.VisibleRows);
                if (newCols != _buffer.Cols || newRows != _buffer.Rows)
                {
                    _buffer.Resize(newRows, newCols);
                }
            }

            _needsResize = true;
        }

        void RefreshSurfaceStyleIfNeeded()
        {
            if (_terminalSurface == null)
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
            _terminalSurface.InvalidateStyle();
            _needsResize = true;
        }

        void DrawToolbar()
        {
            // Guard against _initialized being true while objects are null.
            // This can happen when Cleanup() and InitializeTerminal() execute in
            // close succession across domain reload events.
            if (!_initialized || _buffer == null || _shellProcess == null)
            {
                _initialized = false;
                InitializeTerminal();
            }

            EnsureEditorUpdateSubscription();
            EnsureScheduledShellPump();

            EnsureSurface();

            RefreshSurfaceStyleIfNeeded();

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

            bool shellNull = _shellProcess == null;
            bool shellRunning = !shellNull && _shellProcess.IsRunning;
            if (shellNull || !shellRunning)
            {
                StartShell();
            }
        }

        void HandleKeyInput(Event evt)
        {
            if (evt == null || evt.type != EventType.KeyDown)
            {
                return;
            }

            bool isCommandModifier = Application.platform == RuntimePlatform.OSXEditor ? evt.command : evt.control;
            if (isCommandModifier && evt.keyCode == KeyCode.C && _terminalSurface != null && _terminalSurface.HasSelection)
            {
                EditorGUIUtility.systemCopyBuffer = _terminalSurface.GetSelectedText();
                evt.Use();
                return;
            }

            if ((evt.command || evt.control) && evt.keyCode == KeyCode.C)
            {
                _shellProcess?.Write("\x03");
                _terminalSurface?.ScrollToBottom();
                evt.Use();
                return;
            }

            if (isCommandModifier && evt.keyCode == KeyCode.V)
            {
                string paste = TerminalInputHandler.GetPasteText();
                if (!string.IsNullOrEmpty(paste))
                {
                    _shellProcess?.Write(paste);
                    _terminalSurface?.ScrollToBottom();
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
            _terminalSurface?.ScrollToBottom();
            evt.Use();
        }

        void RestartShell()
        {
            _shellProcess?.Kill();
            _shellProcess?.Dispose();

            if (_buffer == null)
            {
                _buffer = new TerminalBuffer(DefaultRows, DefaultCols, TerminalSettings.ScrollbackLimit);
            }
            else
            {
                _buffer.Reset();
            }

            _terminalSurface?.ClearSelection();
            _shellProcess = new ShellProcess(TerminalSettings.ResolveShellPath());
            _parser = new AnsiParser(_buffer);
            _parser.ResponseCallback = response => _shellProcess?.Write(response);
            StartShell();
        }

        void StartShell()
        {
            _loggedBufferPreview = false;
            if (_shellProcess == null)
            {
                return;
            }

            string projectRoot = TerminalSettings.GetProjectRootDirectory();
            int cols = DefaultCols;
            int rows = DefaultRows;
            if (_terminalSurface != null)
            {
                int candidateCols = Mathf.Max(1, _terminalSurface.VisibleCols);
                int candidateRows = Mathf.Max(1, _terminalSurface.VisibleRows);
                if (HasUsableShellSize(candidateCols, candidateRows))
                {
                    cols = candidateCols;
                    rows = candidateRows;
                }
                else
                {
                    _needsResize = true;
                }
            }

            _shellProcess.Start(projectRoot, cols, rows);
        }

        static bool HasUsableShellSize(int cols, int rows)
        {
            return cols >= MinimumUsableCols && rows >= MinimumUsableRows;
        }

        void ClearTerminal()
        {
            _buffer?.Reset();
            _terminalSurface?.ClearSelection();
        }

        string BuildBufferPreview()
        {
            if (_buffer == null)
            {
                return "buffer=null";
            }

            int rows = Mathf.Min(3, _buffer.Rows);
            int cols = Mathf.Min(40, _buffer.Cols);
            var parts = new System.Text.StringBuilder();

            for (int row = 0; row < rows; row++)
            {
                if (row > 0)
                {
                    parts.Append(" | ");
                }

                for (int col = 0; col < cols; col++)
                {
                    var cell = _buffer.GetCell(row, col);
                    char ch = cell.Codepoint == '\0' ? '·' : cell.Codepoint;
                    parts.Append(ch == ' ' ? '␠' : ch);
                }
            }

            return parts.ToString();
        }

        void IncreaseFontSize()
        {
            TerminalSettings.FontSize += 1;
            _terminalSurface?.InvalidateStyle();
            _needsResize = true;
        }

        void DecreaseFontSize()
        {
            TerminalSettings.FontSize -= 1;
            _terminalSurface?.InvalidateStyle();
            _needsResize = true;
        }

        void Cleanup()
        {
            if (_editorUpdateSubscribed)
            {
                EditorApplication.update -= OnEditorUpdate;
                _editorUpdateSubscribed = false;
            }

            _shellPumpScheduled = false;
            _shellProcess?.Dispose();

            _shellProcess = null;
            _parser = null;
            _buffer = null;
            _toolbarContainer = null;
            _surfaceContainer = null;
            _terminalSurface = null;
            _rootInputCallbacksRegistered = false;
            _surfaceAttachPending = false;
        }
    }
}
