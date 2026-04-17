using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Linalab;

namespace Linalab.Terminal.Editor
{
    public sealed class TerminalEditorWindow : EditorWindow
    {
        const string MenuPath = "Window/Linalab/Unity Terminal";
        const string VerboseLoggingMenuPath = MenuPath + "/Verbose Logging";
        const int DefaultRows = 24;
        const int DefaultCols = 80;
        const int MinimumUsableRows = 2;
        const int MinimumUsableCols = 8;
        const float PollIntervalMs = 16f;

        TerminalBuffer _buffer;
        AnsiParser _parser;
        ShellProcess _shellProcess;
        TerminalSurfaceElement _terminalSurface;
        TextField _textInputSink;
        IMGUIContainer _toolbarContainer;
        VisualElement _surfaceContainer;
        bool _initialized;
        bool _needsResize;
        bool _terminalFocused;
        bool _rootInputCallbacksRegistered;
        bool _editorUpdateSubscribed;
        bool _shellPumpScheduled;
        bool _surfaceAttachPending;
        bool _restoreTextInputFocusAfterSubmit;
        bool _loggedBufferPreview;
        double _lastPollTime;
        double _lastWriteTime;
        int _lastAppliedFontSize;
        string _lastWrittenSequence;
        string _lastAppliedEffectiveFontFamily;
        string _lastTextInputSinkValue;

        const string TextInputSinkName = "unity-terminal-ime-sink";

        static TerminalRuntimeSession s_sharedSession;
        static bool s_quittingHookRegistered;

        sealed class TerminalRuntimeSession
        {
            public TerminalBuffer Buffer;
            public AnsiParser Parser;
            public ShellProcess ShellProcess;
        }

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            var window = GetWindow<TerminalEditorWindow>();
            window.titleContent = new GUIContent("Unity Terminal", EditorGUIUtility.IconContent("d_UnityEditor.ConsoleWindow").image);
            window.minSize = new Vector2(400f, 240f);
            window.wantsMouseMove = true;
            window.Show();
        }

        [MenuItem(VerboseLoggingMenuPath)]
        static void ToggleVerboseLogging()
        {
            var enabled = TerminalSettings.ToggleVerboseLogging();
            Menu.SetChecked(VerboseLoggingMenuPath, enabled);
        }

        [MenuItem(VerboseLoggingMenuPath, true)]
        static bool ValidateVerboseLoggingMenu()
        {
            Menu.SetChecked(VerboseLoggingMenuPath, TerminalSettings.VerboseLogging);
            return true;
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
            EnsureTextInputSink();
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
            rootVisualElement.RegisterCallback<ValidateCommandEvent>(OnRootValidateCommand, TrickleDown.TrickleDown);
            rootVisualElement.RegisterCallback<ExecuteCommandEvent>(OnRootExecuteCommand, TrickleDown.TrickleDown);
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
            // Let the terminal surface receive pointer input directly instead of
            // resolving the parent container as the hit target.
            _surfaceContainer.pickingMode = PickingMode.Ignore;
            rootVisualElement.Add(_surfaceContainer);
        }

        void EnsureTextInputSink()
        {
            if (rootVisualElement == null)
            {
                return;
            }

            if (_textInputSink != null && _textInputSink.parent != rootVisualElement)
            {
                _textInputSink = null;
            }

            if (_textInputSink != null)
            {
                return;
            }

            _textInputSink = new TextField
            {
                name = TextInputSinkName,
                value = string.Empty,
                isDelayed = false
            };
            _lastTextInputSinkValue = string.Empty;
            _textInputSink.style.position = Position.Absolute;
            _textInputSink.style.left = -10000f;
            _textInputSink.style.top = -10000f;
            _textInputSink.style.width = 1f;
            _textInputSink.style.height = 1f;
            _textInputSink.style.opacity = 0f;
            _textInputSink.style.paddingLeft = 0f;
            _textInputSink.style.paddingRight = 0f;
            _textInputSink.style.paddingTop = 0f;
            _textInputSink.style.paddingBottom = 0f;
            _textInputSink.style.borderBottomWidth = 0f;
            _textInputSink.style.borderTopWidth = 0f;
            _textInputSink.style.borderLeftWidth = 0f;
            _textInputSink.style.borderRightWidth = 0f;
            _textInputSink.style.marginBottom = 0f;
            _textInputSink.style.marginTop = 0f;
            _textInputSink.style.marginLeft = 0f;
            _textInputSink.style.marginRight = 0f;
            _textInputSink.RegisterValueChangedCallback(OnTextInputSinkValueChanged);
            _textInputSink.RegisterCallback<KeyDownEvent>(OnTextInputSinkKeyDown, TrickleDown.TrickleDown);
            _textInputSink.RegisterCallback<FocusInEvent>(OnTextInputSinkFocusIn);
            _textInputSink.RegisterCallback<FocusOutEvent>(OnTextInputSinkFocusOut);
            rootVisualElement.Add(_textInputSink);
        }

        void CreateTerminalSurface()
        {
            if (_terminalSurface != null || _buffer == null)
            {
                return;
            }

            _terminalSurface = new TerminalSurfaceElement(_buffer, _parser);
            _terminalSurface.OnGridSizeChanged += OnGridSizeChanged;
            _terminalSurface.OnInputRequested += HandleKeyInput;
            _terminalSurface.OnMouseInputRequested += HandleMouseInput;
            _terminalSurface.OnImageDropRequested += HandleImageDropInput;
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
            FocusTextInputSink();
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
            wantsMouseMove = true;
            Menu.SetChecked(VerboseLoggingMenuPath, TerminalSettings.VerboseLogging);
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
            FocusTextInputSink();
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
            FocusTextInputSink();
            _terminalSurface?.MarkDirtyRepaint();
            Repaint();
        }

        void OnTextInputSinkFocusIn(FocusInEvent evt)
        {
            Input.imeCompositionMode = IMECompositionMode.On;
            _restoreTextInputFocusAfterSubmit = false;
            _terminalFocused = true;
            _terminalSurface?.MarkDirtyRepaint();
        }

        void OnTextInputSinkFocusOut(FocusOutEvent evt)
        {
            Input.imeCompositionMode = IMECompositionMode.Auto;
            _lastTextInputSinkValue = _textInputSink?.value ?? string.Empty;
            if (_restoreTextInputFocusAfterSubmit)
            {
                _restoreTextInputFocusAfterSubmit = false;
                FocusTextInputSink();
                return;
            }

            if (_terminalSurface != null && _terminalSurface.focusController?.focusedElement == _terminalSurface)
            {
                return;
            }

            _terminalFocused = false;
            _terminalSurface?.MarkDirtyRepaint();
        }

        void FocusTextInputSink()
        {
            if (_textInputSink == null)
            {
                return;
            }

            _textInputSink.schedule.Execute(() => _textInputSink?.Focus()).StartingIn(0);
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

        void OnRootValidateCommand(ValidateCommandEvent evt)
        {
            if (evt == null || !_terminalFocused || !TerminalInputHandler.IsPasteCommand(evt.commandName))
            {
                return;
            }

            evt.StopImmediatePropagation();
        }

        void OnRootExecuteCommand(ExecuteCommandEvent evt)
        {
            if (evt == null || !_terminalFocused || !TerminalInputHandler.IsPasteCommand(evt.commandName))
            {
                return;
            }

            HandlePasteInput("root-paste-command");
            evt.StopImmediatePropagation();
        }

        void InitializeTerminal()
        {
            if (_initialized)
            {
                return;
            }

            EnsureQuitHook();
            AcquireOrCreateSharedSession();
            _lastAppliedFontSize = -1;
            _lastAppliedEffectiveFontFamily = null;

            EnsureEditorUpdateSubscription();
            _initialized = true;
        }

        static void EnsureQuitHook()
        {
            if (s_quittingHookRegistered)
            {
                return;
            }

            EditorApplication.quitting += DisposeSharedSession;
            s_quittingHookRegistered = true;
        }

        void AcquireOrCreateSharedSession()
        {
            if (s_sharedSession == null || s_sharedSession.Buffer == null || s_sharedSession.Parser == null || s_sharedSession.ShellProcess == null)
            {
                var buffer = new TerminalBuffer(DefaultRows, DefaultCols, TerminalSettings.ScrollbackLimit);
                var parser = new AnsiParser(buffer);
                var shellProcess = new ShellProcess(TerminalSettings.ResolveShellPath());
                parser.ResponseCallback = response => shellProcess.Write(response);
                s_sharedSession = new TerminalRuntimeSession
                {
                    Buffer = buffer,
                    Parser = parser,
                    ShellProcess = shellProcess
                };
            }

            _buffer = s_sharedSession.Buffer;
            _parser = s_sharedSession.Parser;
            _shellProcess = s_sharedSession.ShellProcess;
            _terminalSurface?.SetMouseProtocolSource(_parser);
        }

        static void DisposeSharedSession()
        {
            if (s_sharedSession == null)
            {
                return;
            }

            s_sharedSession.ShellProcess?.Dispose();
            s_sharedSession = null;
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

            var now = EditorApplication.timeSinceStartup;
            if ((now - _lastPollTime) * 1000d < PollIntervalMs)
            {
                return;
            }

            _lastPollTime = now;
            var hadOutput = false;

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
                    VerboseLog($"[Terminal] Buffer preview after output: {BuildBufferPreview()}");
                }

                _terminalSurface?.ScrollToBottom();
                _terminalSurface?.MarkDirtyRepaint();
            }

            if (_needsResize && _terminalSurface != null)
            {
                var newCols = Mathf.Max(1, _terminalSurface.VisibleCols);
                var newRows = Mathf.Max(1, _terminalSurface.VisibleRows);

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
                var newCols = Mathf.Max(1, _terminalSurface.VisibleCols);
                var newRows = Mathf.Max(1, _terminalSurface.VisibleRows);
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

            var effectiveFontFamily = TerminalSettings.GetEffectiveFontFamily();
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

            var tmuxAutoAttach = GUILayout.Toggle(
                TerminalSettings.TmuxAutoAttach,
                new GUIContent("tmux", "Auto-attach to the persistent tmux session for this Unity workspace."),
                EditorStyles.toolbarButton,
                GUILayout.Width(52f));
            if (tmuxAutoAttach != TerminalSettings.TmuxAutoAttach)
            {
                HandleTmuxToggleChange(tmuxAutoAttach);
                GUIUtility.ExitGUI();
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
            if (TerminalSettings.TmuxAutoAttach)
            {
                GUILayout.Label($"tmux:{TerminalSettings.GetTmuxSessionName()}", EditorStyles.miniLabel);
                GUILayout.Space(8f);
            }

            GUILayout.Label(_terminalFocused ? "Focused" : "Click terminal to focus", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();

            var shellNull = _shellProcess == null;
            var shellRunning = !shellNull && _shellProcess.IsRunning;
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

            LogEventCharacter("surface-keydown-raw", evt.character, evt.keyCode.ToString());

            if (IsTextInputSinkFocused())
            {
                return;
            }

            var isCommandModifier = Application.platform == RuntimePlatform.OSXEditor ? evt.command : evt.control;
            if (isCommandModifier && evt.keyCode == KeyCode.C && _terminalSurface != null && _terminalSurface.HasSelection)
            {
                EditorGUIUtility.systemCopyBuffer = _terminalSurface.GetSelectedText();
                evt.Use();
                return;
            }

            if ((evt.command || evt.control) && evt.keyCode == KeyCode.C)
            {
                WriteUserInputToShell("\x03", "surface-ctrl-c");
                _terminalSurface?.ScrollToBottom();
                evt.Use();
                return;
            }

            if (TerminalInputHandler.IsPrimaryPasteShortcut(Application.platform, evt.keyCode, evt.command, evt.control))
            {
                HandlePasteInput("surface-paste");
                evt.Use();
                return;
            }

            if (!ShouldRouteSurfaceKeyDown(evt))
            {
                return;
            }

            var translated = TerminalInputHandler.TranslateKeyEvent(evt);
            if (translated == null)
            {
                return;
            }

            if (ShouldSuppressImmediateDuplicateWrite(translated))
            {
                evt.Use();
                return;
            }

            WriteUserInputToShell(translated, "surface-keydown");
            _terminalSurface?.ScrollToBottom();
            evt.Use();
        }

        static bool ShouldRouteSurfaceKeyDown(Event evt)
        {
            if (evt == null)
            {
                return false;
            }

            if ((evt.command || evt.control) && evt.keyCode != KeyCode.None)
            {
                return true;
            }

            if (evt.character >= 0x20 && evt.character != 0x7f)
            {
                return false;
            }

            return evt.keyCode switch
            {
                KeyCode.Return => true,
                KeyCode.KeypadEnter => true,
                KeyCode.Backspace => true,
                KeyCode.Tab => true,
                KeyCode.Escape => true,
                KeyCode.Delete => true,
                KeyCode.Home => true,
                KeyCode.End => true,
                KeyCode.PageUp => true,
                KeyCode.PageDown => true,
                KeyCode.Insert => true,
                KeyCode.UpArrow => true,
                KeyCode.DownArrow => true,
                KeyCode.LeftArrow => true,
                KeyCode.RightArrow => true,
                KeyCode.F1 => true,
                KeyCode.F2 => true,
                KeyCode.F3 => true,
                KeyCode.F4 => true,
                KeyCode.F5 => true,
                KeyCode.F6 => true,
                KeyCode.F7 => true,
                KeyCode.F8 => true,
                KeyCode.F9 => true,
                KeyCode.F10 => true,
                KeyCode.F11 => true,
                KeyCode.F12 => true,
                _ => false
            };
        }

        void OnTextInputSinkKeyDown(KeyDownEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            LogEventCharacter("sink-keydown-raw", evt.character, evt.keyCode.ToString());
            bool isSubmitKey = IsTextSinkSubmitKey(evt.keyCode);

            var isCommandModifier = Application.platform == RuntimePlatform.OSXEditor ? evt.commandKey : evt.ctrlKey;
            if (isCommandModifier && evt.keyCode == KeyCode.C && _terminalSurface != null && _terminalSurface.HasSelection)
            {
                EditorGUIUtility.systemCopyBuffer = _terminalSurface.GetSelectedText();
                evt.StopImmediatePropagation();
                return;
            }

            if ((evt.commandKey || evt.ctrlKey) && evt.keyCode == KeyCode.C)
            {
                WriteUserInputToShell("\x03", "sink-ctrl-c");
                _terminalSurface?.ScrollToBottom();
                evt.StopImmediatePropagation();
                return;
            }

            if (!ShouldRouteTextSinkKeyDown(evt))
            {
                return;
            }

            var translated = TerminalInputHandler.TranslateKeyEvent(evt.character, evt.keyCode, evt.ctrlKey, evt.shiftKey, Input.compositionString);
            if (translated == null)
            {
                return;
            }

            if (ShouldSuppressImmediateDuplicateWrite(translated))
            {
                evt.StopImmediatePropagation();
                return;
            }

            WriteUserInputToShell(translated, "sink-keydown");
            _terminalSurface?.ScrollToBottom();
            if (isSubmitKey && string.IsNullOrEmpty(Input.compositionString))
            {
                _restoreTextInputFocusAfterSubmit = true;
                FocusTextInputSink();
            }

            evt.StopImmediatePropagation();
        }

        void OnTextInputSinkValueChanged(ChangeEvent<string> evt)
        {
            VerboseLog($"[TerminalSinkValue] previous={DescribeText(evt?.previousValue)} new={DescribeText(evt?.newValue)} composition={DescribeText(Input.compositionString)}");
            if (evt == null || string.IsNullOrEmpty(evt.newValue))
            {
                _lastTextInputSinkValue = SanitizeCommittedText(evt?.newValue);
                return;
            }

            if (!string.IsNullOrEmpty(Input.compositionString))
            {
                return;
            }

            var sanitizedNewValue = SanitizeCommittedText(evt.newValue);
            var committedText = ExtractCommittedText(_lastTextInputSinkValue, sanitizedNewValue);
            committedText = SanitizeCommittedText(committedText);
            if (string.IsNullOrEmpty(committedText))
            {
                _lastTextInputSinkValue = sanitizedNewValue;
                return;
            }

            WriteUserInputToShell(committedText, "sink-value-changed");
            _terminalSurface?.ScrollToBottom();
            _lastTextInputSinkValue = sanitizedNewValue;
        }

        void HandlePasteInput(string source)
        {
            var paste = TerminalInputHandler.GetPasteText();
            if (string.IsNullOrEmpty(paste))
            {
                return;
            }

            WriteUserInputToShell(paste, source);
            _terminalSurface?.ScrollToBottom();

            if (_textInputSink == null)
            {
                return;
            }

            _textInputSink.SetValueWithoutNotify(string.Empty);
            _lastTextInputSinkValue = string.Empty;
        }

        void HandleMouseInput(string sequence)
        {
            WriteUserInputToShell(sequence, "surface-mouse");
        }

        void HandleImageDropInput(string path)
        {
            var input = TerminalInputHandler.BuildImageDropInput(TerminalSettings.GetProjectRootDirectory(), path);
            if (string.IsNullOrEmpty(input))
            {
                return;
            }

            WriteUserInputToShell(input, "surface-image-drop");
            _terminalSurface?.ScrollToBottom();
        }

        void WriteUserInputToShell(string text, string source)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var sanitizedText = SanitizeCommittedText(text);
            VerboseLog($"[TerminalInput] source={source} raw={DescribeText(text)} sanitized={DescribeText(sanitizedText)}");
            if (string.IsNullOrEmpty(sanitizedText))
            {
                return;
            }

            _shellProcess?.Write(sanitizedText);
        }

        void LogEventCharacter(string source, char character, string keyCode)
        {
            if (character == '\0' && string.IsNullOrEmpty(Input.compositionString))
            {
                return;
            }

            VerboseLog($"[TerminalEvent] source={source} char={DescribeText(character == '\0' ? string.Empty : character.ToString())} keyCode={keyCode} composition={DescribeText(Input.compositionString)} sinkValue={DescribeText(_textInputSink?.value)}");
        }

        static void VerboseLog(string message)
        {
            if (!TerminalSettings.VerboseLogging)
            {
                return;
            }

            D.Log(message);
        }

        static string DescribeText(string text)
        {
            if (text == null)
            {
                return "<null>";
            }

            if (text.Length == 0)
            {
                return "<empty>";
            }

            var builder = new System.Text.StringBuilder();
            builder.Append('[');
            for (var i = 0; i < text.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("U+");
                builder.Append(((int)text[i]).ToString("X4"));
            }

            builder.Append("] \"");
            builder.Append(text.Replace("\r", "\\r", System.StringComparison.Ordinal).Replace("\n", "\\n", System.StringComparison.Ordinal));
            builder.Append('"');
            return builder.ToString();
        }

        static string ExtractCommittedText(string previousValue, string newValue)
        {
            var previous = previousValue ?? string.Empty;
            var current = newValue ?? string.Empty;
            if (current.Length == 0)
            {
                return string.Empty;
            }

            if (current.StartsWith(previous, System.StringComparison.Ordinal))
            {
                return current.Substring(previous.Length);
            }

            var commonLength = 0;
            var maxCommonLength = Mathf.Min(previous.Length, current.Length);
            while (commonLength < maxCommonLength && previous[commonLength] == current[commonLength])
            {
                commonLength++;
            }

            return current.Substring(commonLength);
        }

        static string SanitizeCommittedText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text
                .Replace("\uFEFF", string.Empty, System.StringComparison.Ordinal)
                .Replace("<feff>", string.Empty, System.StringComparison.OrdinalIgnoreCase);
        }

        bool ShouldRouteTextSinkKeyDown(KeyDownEvent evt)
        {
            if (evt == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(Input.compositionString))
            {
                return false;
            }

            if (evt.commandKey && !evt.ctrlKey)
            {
                return false;
            }

            if ((evt.commandKey || evt.ctrlKey) && evt.keyCode == KeyCode.V)
            {
                return false;
            }

            if (evt.ctrlKey)
            {
                return true;
            }

            if (evt.character >= 0x20 && evt.character != 0x7f)
            {
                return false;
            }

            return evt.keyCode switch
            {
                KeyCode.Return => true,
                KeyCode.KeypadEnter => true,
                KeyCode.Backspace => true,
                KeyCode.Tab => true,
                KeyCode.Escape => true,
                KeyCode.Delete => true,
                KeyCode.Home => true,
                KeyCode.End => true,
                KeyCode.PageUp => true,
                KeyCode.PageDown => true,
                KeyCode.Insert => true,
                KeyCode.UpArrow => true,
                KeyCode.DownArrow => true,
                KeyCode.LeftArrow => true,
                KeyCode.RightArrow => true,
                KeyCode.F1 => true,
                KeyCode.F2 => true,
                KeyCode.F3 => true,
                KeyCode.F4 => true,
                KeyCode.F5 => true,
                KeyCode.F6 => true,
                KeyCode.F7 => true,
                KeyCode.F8 => true,
                KeyCode.F9 => true,
                KeyCode.F10 => true,
                KeyCode.F11 => true,
                KeyCode.F12 => true,
                _ => false
            };
        }

        bool IsTextInputSinkFocused()
        {
            return _textInputSink != null && _textInputSink.focusController?.focusedElement == _textInputSink;
        }

        static bool IsTextSinkSubmitKey(KeyCode keyCode)
        {
            return keyCode == KeyCode.Return || keyCode == KeyCode.KeypadEnter;
        }

        bool ShouldSuppressImmediateDuplicateWrite(string translated)
        {
            var now = EditorApplication.timeSinceStartup;
            var isImmediateDuplicate = string.Equals(_lastWrittenSequence, translated, System.StringComparison.Ordinal)
                && now - _lastWriteTime <= 0.03d;

            _lastWrittenSequence = translated;
            _lastWriteTime = now;
            return isImmediateDuplicate;
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
            _terminalSurface?.SetMouseProtocolSource(_parser);
            s_sharedSession = new TerminalRuntimeSession
            {
                Buffer = _buffer,
                Parser = _parser,
                ShellProcess = _shellProcess
            };
            StartShell();
        }

        void StartShell()
        {
            _loggedBufferPreview = false;
            if (_shellProcess == null)
            {
                return;
            }

            var projectRoot = TerminalSettings.GetProjectRootDirectory();
            var cols = DefaultCols;
            var rows = DefaultRows;
            if (_terminalSurface != null)
            {
                var candidateCols = Mathf.Max(1, _terminalSurface.VisibleCols);
                var candidateRows = Mathf.Max(1, _terminalSurface.VisibleRows);
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
            _terminalSurface?.ClearSelection();
            _buffer?.Reset();
            _shellProcess?.Write("\x0c");
            _terminalSurface?.ScrollToBottom();
        }

        string BuildBufferPreview()
        {
            if (_buffer == null)
            {
                return "buffer=null";
            }

            var rows = Mathf.Min(3, _buffer.Rows);
            var cols = Mathf.Min(40, _buffer.Cols);
            var parts = new System.Text.StringBuilder();

            for (var row = 0; row < rows; row++)
            {
                if (row > 0)
                {
                    parts.Append(" | ");
                }

                for (var col = 0; col < cols; col++)
                {
                    var cell = _buffer.GetCell(row, col);
                    var ch = cell.Codepoint == '\0' ? '·' : cell.Codepoint;
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

        void HandleTmuxToggleChange(bool enable)
        {
            if (!enable)
            {
                TerminalSettings.TmuxAutoAttach = false;
                RestartShell();
                return;
            }

            var canonical = TerminalSettings.GetCanonicalTmuxSessionName();
            var existing = ShellProcess.ListTmuxWorkspaceSessions(TerminalSettings.GetProjectRootDirectory());

            if (existing == null || existing.Length == 0)
            {
                TerminalSettings.TmuxSessionNameOverride = string.Empty;
                TerminalSettings.TmuxAutoAttach = true;
                RestartShell();
                return;
            }

            var result = TmuxSessionPicker.ShowModal(existing, canonical, out var selected);
            switch (result)
            {
                case TmuxPickerResult.Attach:
                    TerminalSettings.TmuxSessionNameOverride =
                        string.Equals(selected, canonical, StringComparison.Ordinal)
                            ? string.Empty
                            : selected;
                    TerminalSettings.TmuxAutoAttach = true;
                    RestartShell();
                    break;

                case TmuxPickerResult.CreateNew:
                    var newName = ShellProcess.FindUnusedTmuxSessionName(canonical);
                    TerminalSettings.TmuxSessionNameOverride =
                        string.Equals(newName, canonical, StringComparison.Ordinal)
                            ? string.Empty
                            : newName;
                    TerminalSettings.TmuxAutoAttach = true;
                    RestartShell();
                    break;

                case TmuxPickerResult.Cancel:
                default:
                    break;
            }
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
            Input.imeCompositionMode = IMECompositionMode.Auto;

            if (_terminalSurface != null)
            {
                _terminalSurface.OnGridSizeChanged -= OnGridSizeChanged;
                _terminalSurface.OnInputRequested -= HandleKeyInput;
                _terminalSurface.OnMouseInputRequested -= HandleMouseInput;
                _terminalSurface.OnImageDropRequested -= HandleImageDropInput;
                _terminalSurface.OnInteractionStarted -= OnSurfaceInteractionStarted;
                _terminalSurface.UnregisterCallback<FocusInEvent>(OnSurfaceFocusIn);
                _terminalSurface.UnregisterCallback<FocusOutEvent>(OnSurfaceFocusOut);
            }

            if (_textInputSink != null)
            {
                _textInputSink.UnregisterValueChangedCallback(OnTextInputSinkValueChanged);
                _textInputSink.UnregisterCallback<KeyDownEvent>(OnTextInputSinkKeyDown, TrickleDown.TrickleDown);
                _textInputSink.UnregisterCallback<FocusInEvent>(OnTextInputSinkFocusIn);
                _textInputSink.UnregisterCallback<FocusOutEvent>(OnTextInputSinkFocusOut);
            }

            if (rootVisualElement != null && _rootInputCallbacksRegistered)
            {
                rootVisualElement.UnregisterCallback<MouseDownEvent>(OnRootMouseDown, TrickleDown.TrickleDown);
                rootVisualElement.UnregisterCallback<ValidateCommandEvent>(OnRootValidateCommand, TrickleDown.TrickleDown);
                rootVisualElement.UnregisterCallback<ExecuteCommandEvent>(OnRootExecuteCommand, TrickleDown.TrickleDown);
            }

            _shellProcess = null;
            _parser = null;
            _buffer = null;
            _toolbarContainer = null;
            _surfaceContainer = null;
            _terminalSurface = null;
            _textInputSink = null;
            _rootInputCallbacksRegistered = false;
            _surfaceAttachPending = false;
            _initialized = false;
        }
    }
}
