using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Linalab;

namespace Linalab.Terminal.Editor
{
    public sealed class TerminalEditorWindow : EditorWindow
    {
        const string AttachToTmuxSessionStateKey = "Linalab.Terminal.AttachToTmux";
        const string MenuPath = "Tools/Unity Editor Terminal";
        const int DefaultRows = 24;
        const int DefaultCols = 80;
        const float PollIntervalMs = 16f;

        TerminalBuffer _buffer;
        AnsiParser _parser;
        ShellProcess _shellProcess;
        TerminalSurfaceElement _terminalSurface;
        bool _initialized;
        bool _needsResize;
        bool _terminalFocused;
        bool _editorUpdateSubscribed;
        bool _shellPumpScheduled;
        bool _surfaceAttachPending;
        bool _loggedBufferPreview;
        double _lastPollTime;
        double _notificationHideAt;
        int _lastAppliedFontSize;
        string _lastAppliedEffectiveFontFamily;
        TerminalCommandRouter _commandRouter;

        static bool _isAssemblyReloading;

        static TerminalEditorWindow()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

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

            rootVisualElement.style.flexDirection = FlexDirection.Column;
            EnsureScheduledShellPump();

            var toolbar = new IMGUIContainer(DrawToolbar);
            toolbar.style.flexShrink = 0;
            rootVisualElement.Add(toolbar);

            if (_terminalSurface == null && _buffer != null)
            {
                _terminalSurface = new TerminalSurfaceElement(_buffer);
                _terminalSurface.OnGridSizeChanged += OnGridSizeChanged;
                _terminalSurface.OnInputRequested += HandleKeyInput;
                _terminalSurface.RegisterCallback<FocusInEvent>(OnSurfaceFocusIn);
                _terminalSurface.RegisterCallback<FocusOutEvent>(OnSurfaceFocusOut);

                var surfaceContainer = new VisualElement
                {
                    style =
                    {
                        flexGrow = 1,
                        paddingLeft = 8f,
                        paddingRight = 8f,
                        paddingTop = 6f,
                        paddingBottom = 6f
                    }
                };
                surfaceContainer.Add(_terminalSurface);
            rootVisualElement.Add(surfaceContainer);
            }
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

                _terminalSurface = new TerminalSurfaceElement(_buffer);
                _terminalSurface.OnGridSizeChanged += OnGridSizeChanged;
                _terminalSurface.OnInputRequested += HandleKeyInput;
                _terminalSurface.RegisterCallback<FocusInEvent>(OnSurfaceFocusIn);
                _terminalSurface.RegisterCallback<FocusOutEvent>(OnSurfaceFocusOut);

                var surfaceContainer = new VisualElement
                {
                    style =
                    {
                        flexGrow = 1,
                        paddingLeft = 8f,
                        paddingRight = 8f,
                        paddingTop = 6f,
                        paddingBottom = 6f
                    }
                };

                surfaceContainer.Add(_terminalSurface);
                rootVisualElement.Add(surfaceContainer);
                _needsResize = true;
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

            _commandRouter = new TerminalCommandRouter();
            RegisterCommandRoutes();

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

        void RegisterCommandRoutes()
        {
            // Handler is state-sync only — must not write to the shell.
            // The shell already has the typed command and executes it normally when
            // Enter is forwarded by the router. This call just keeps editor state in sync
            // so the next shell restart (which happens automatically after tmux exits) uses
            // the correct attachToTmux = false flag.
            _commandRouter.Register("tmux detach-client", () => SetAttachToTmuxForSession(false));
        }

        static void OnBeforeAssemblyReload()
        {
            _isAssemblyReloading = true;
        }

        static void OnAfterAssemblyReload()
        {
            _isAssemblyReloading = false;
        }

        static bool GetAttachToTmuxForSession()
        {
            return SessionState.GetBool(AttachToTmuxSessionStateKey, TerminalSettings.AutoAttachTmux);
        }

        static void SetAttachToTmuxForSession(bool attachToTmux)
        {
            SessionState.SetBool(AttachToTmuxSessionStateKey, attachToTmux);
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

                // Buffer may already be the right size from OnGridSizeChanged; resize
                // here only if it isn't (e.g. first start before geometry is known).
                if (newCols != _buffer.Cols || newRows != _buffer.Rows)
                {
                    _buffer.Resize(newRows, newCols);
                    _terminalSurface.MarkDirtyRepaint();
                }

                // Always attempt the shell resize; clear the flag whether or not it
                // succeeds so a transient failure doesn't cause an infinite retry loop.
                if (_shellProcess.TryResize(newCols, newRows))
                {
                    if (GetAttachToTmuxForSession())
                    {
                        _shellProcess.Write("tmux setw -w window-size manual >/dev/null 2>&1 || true\n");
                        _shellProcess.Write($"tmux resize-window -x {newCols} -y {newRows} >/dev/null 2>&1 || true\n");
                        _shellProcess.Write($"tmux resize-pane -x {newCols} -y {newRows} -t :. >/dev/null 2>&1 || true\n");
                        _shellProcess.Write("tmux refresh-client >/dev/null 2>&1 || true\n");
                    }
                }

                _needsResize = false;
            }

            if (_notificationHideAt > 0d && now >= _notificationHideAt)
            {
                RemoveNotification();
                _notificationHideAt = 0d;
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
            // "Restart" always starts a plain shell (no tmux) regardless of previous
            // session state. This is the safe baseline to recover a visible terminal.
            if (GUILayout.Button("Restart", EditorStyles.toolbarButton, GUILayout.Width(60f)))
            {
                RestartShell(false);
            }

            if (GUILayout.Button("Attach tmux", EditorStyles.toolbarButton, GUILayout.Width(84f)))
            {
                RestartShell(true);
            }

            if (GUILayout.Button($"Open {TerminalSettings.GetTerminalAppDisplayName(TerminalSettings.TerminalApp)}", EditorStyles.toolbarButton, GUILayout.Width(140f)))
            {
                OpenSelectedTerminalApp();
            }

            using (new EditorGUI.DisabledScope(!GetAttachToTmuxForSession()))
            {
                if (GUILayout.Button("Detach tmux", EditorStyles.toolbarButton, GUILayout.Width(88f)))
                {
                    DetachTmuxSession();
                }
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
                StartShell(GetAttachToTmuxForSession());
            }
        }

        void HandleKeyInput()
        {
            var evt = Event.current;
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

            translated = _commandRouter?.Route(translated);
            if (translated == null)
            {
                // Intercepted by command router — consume the event, do not forward to shell.
                evt.Use();
                return;
            }

            _shellProcess?.Write(translated);
            _terminalSurface?.ScrollToBottom();
            evt.Use();
        }

        void RestartShell()
        {
            RestartShell(GetAttachToTmuxForSession());
        }

        void RestartShell(bool attachToTmux)
        {
            SetAttachToTmuxForSession(attachToTmux);
            if (_shellProcess != null && _shellProcess.CanPreserveSessionOnReload)
            {
                _shellProcess.DetachPreservingSession();
            }
            else
            {
                _shellProcess?.Kill();
                _shellProcess?.Dispose();
            }

            if (_buffer == null)
            {
                _buffer = new TerminalBuffer(DefaultRows, DefaultCols, TerminalSettings.ScrollbackLimit);
            }
            else
            {
                _buffer.Reset();
            }

            _terminalSurface?.ClearSelection();
            _commandRouter?.Reset();
            _shellProcess = new ShellProcess(TerminalSettings.ResolveShellPath());
            _parser = new AnsiParser(_buffer);
            _parser.ResponseCallback = response => _shellProcess?.Write(response);
            StartShell(attachToTmux);
        }

        void StartShell(bool attachToTmux)
        {
            SetAttachToTmuxForSession(attachToTmux);
            _loggedBufferPreview = false;
            if (_shellProcess == null)
            {
                return;
            }

            string projectRoot = TerminalSettings.GetProjectRootDirectory();
            int cols = _terminalSurface != null && _terminalSurface.VisibleCols > 0
                ? _terminalSurface.VisibleCols
                : DefaultCols;
            int rows = _terminalSurface != null && _terminalSurface.VisibleRows > 0
                ? _terminalSurface.VisibleRows
                : DefaultRows;
            _shellProcess.Start(projectRoot, attachToTmux, cols, rows);
        }

        void DetachTmuxSession()
        {
            SetAttachToTmuxForSession(false);
            _shellProcess?.Write("tmux detach-client\n");
            _terminalSurface?.ScrollToBottom();
        }

        void OpenSelectedTerminalApp()
        {
            string projectRoot = TerminalSettings.GetProjectRootDirectory();
            string sessionName = TerminalSettings.GetTmuxSessionName(projectRoot);
            if (!TerminalAppLauncher.LaunchSelected(projectRoot, sessionName, out string error))
            {
                EditorUtility.DisplayDialog("Unity Terminal", error, "OK");
                return;
            }

            SetAttachToTmuxForSession(true);
            ShowNotification(new GUIContent($"Opened {TerminalSettings.GetTerminalAppDisplayName(TerminalSettings.TerminalApp)}"));
            _notificationHideAt = EditorApplication.timeSinceStartup + 1.2d;
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
            if (_isAssemblyReloading && _shellProcess != null && _shellProcess.CanPreserveSessionOnReload)
            {
                _shellProcess.DetachPreservingSession();
            }
            else
            {
                _shellProcess?.Dispose();
            }

            _shellProcess = null;
            _parser = null;
            _buffer = null;
            _terminalSurface = null;
            _commandRouter = null;
            _surfaceAttachPending = false;
        }
    }
}
