using UnityEditor;
using UnityEngine;

namespace Linalab.Terminal.Editor
{
    public static class TerminalSettingsProvider
    {
        const string PreferencesPath = "Preferences/Linalab/Unity Terminal";

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider(PreferencesPath, SettingsScope.User)
            {
                label = "Unity Terminal",
                guiHandler = _ => DrawGui(),
                keywords = new System.Collections.Generic.HashSet<string>(new[] { "terminal", "shell", "zsh", "bash", "workspace", "font", "tmux", "session", "logging" })
            };
        }

        public static void Open()
        {
            SettingsService.OpenUserPreferences("Linalab/Unity Terminal");
        }

        static void DrawGui()
        {
            var projectRoot = TerminalSettings.GetProjectRootDirectory();

            EditorGUILayout.LabelField("Shell", EditorStyles.boldLabel);
            TerminalSettings.ShellProfile = (TerminalShellProfile)EditorGUILayout.EnumPopup("Shell Profile", TerminalSettings.ShellProfile);

            using (new EditorGUI.DisabledScope(TerminalSettings.ShellProfile != TerminalShellProfile.Custom))
            {
                TerminalSettings.ShellPathOverride = EditorGUILayout.TextField("Custom Shell Path", TerminalSettings.ShellPathOverride);
            }

            EditorGUILayout.HelpBox($"Resolved shell: {TerminalSettings.ResolveShellPath()}", MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Workspace", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox($"Project root: {projectRoot}", MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Session", EditorStyles.boldLabel);
            var tmuxAutoAttach = EditorGUILayout.Toggle("Tmux Auto Attach", TerminalSettings.TmuxAutoAttach);
            if (tmuxAutoAttach != TerminalSettings.TmuxAutoAttach)
            {
                TerminalEditorWindow.HandleTmuxToggleChangeFromSettings(tmuxAutoAttach);
                GUIUtility.ExitGUI();
            }

            if (TerminalSettings.TmuxAutoAttach)
            {
                var sessionName = TerminalSettings.GetTmuxSessionName();
                var restartMessage = TerminalEditorWindow.HasOpenWindow()
                    ? "Changes restart the open terminal window automatically."
                    : "Open or restart the terminal window to apply changes.";
                EditorGUILayout.HelpBox($"Session: {sessionName}\nRuntime behavior: if tmux session '{sessionName}' exists, Unity Terminal attaches to it; otherwise it creates a new tmux session with that name.\n{restartMessage}", MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Display", EditorStyles.boldLabel);
            TerminalSettings.FontFamily = EditorGUILayout.TextField("Font Family", TerminalSettings.FontFamily);
            EditorGUILayout.HelpBox($"Effective font family: {TerminalSettings.GetEffectiveFontFamily()}", MessageType.None);
            TerminalSettings.FontSize = EditorGUILayout.IntSlider("Font Size", TerminalSettings.FontSize, 8, 32);
            TerminalSettings.ScrollbackLimit = EditorGUILayout.IntField("Scrollback Limit", TerminalSettings.ScrollbackLimit);
            TerminalSettings.CursorBlinkRate = EditorGUILayout.Slider("Cursor Blink Rate", TerminalSettings.CursorBlinkRate, 0.1f, 2f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);
            TerminalSettings.VerboseLogging = EditorGUILayout.Toggle("Verbose Logging", TerminalSettings.VerboseLogging);
        }
    }
}
