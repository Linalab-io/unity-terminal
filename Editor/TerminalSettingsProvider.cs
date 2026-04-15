using System.Collections.Generic;
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
                keywords = new System.Collections.Generic.HashSet<string>(new[] { "terminal", "shell", "zsh", "bash", "tmux", "workspace" })
            };
        }

        public static void Open()
        {
            SettingsService.OpenUserPreferences("Linalab/Unity Terminal");
        }

        static void DrawGui()
        {
            string projectRoot = TerminalSettings.GetProjectRootDirectory();

            EditorGUILayout.LabelField("Terminal App", EditorStyles.boldLabel);
            DrawTerminalAppSelector();
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Shell", EditorStyles.boldLabel);
            TerminalSettings.ShellProfile = (TerminalShellProfile)EditorGUILayout.EnumPopup("Shell Profile", TerminalSettings.ShellProfile);

            using (new EditorGUI.DisabledScope(TerminalSettings.ShellProfile != TerminalShellProfile.Custom))
            {
                TerminalSettings.ShellPathOverride = EditorGUILayout.TextField("Custom Shell Path", TerminalSettings.ShellPathOverride);
            }

            EditorGUILayout.HelpBox($"Resolved shell: {TerminalSettings.ResolveShellPath()}", MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Workspace", EditorStyles.boldLabel);
            TerminalSettings.AutoAttachTmux = EditorGUILayout.Toggle("Auto Attach tmux", TerminalSettings.AutoAttachTmux);
            EditorGUILayout.HelpBox($"Project root: {projectRoot}", MessageType.None);
            EditorGUILayout.HelpBox($"tmux session name: {TerminalSettings.GetTmuxSessionName(projectRoot)}", MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Display", EditorStyles.boldLabel);
            TerminalSettings.FontFamily = EditorGUILayout.TextField("Font Family", TerminalSettings.FontFamily);
            EditorGUILayout.HelpBox($"Effective font family: {TerminalSettings.GetEffectiveFontFamily()}", MessageType.None);
            TerminalSettings.FontSize = EditorGUILayout.IntSlider("Font Size", TerminalSettings.FontSize, 8, 32);
            TerminalSettings.ScrollbackLimit = EditorGUILayout.IntField("Scrollback Limit", TerminalSettings.ScrollbackLimit);
            TerminalSettings.CursorBlinkRate = EditorGUILayout.Slider("Cursor Blink Rate", TerminalSettings.CursorBlinkRate, 0.1f, 2f);
        }

        static void DrawTerminalAppSelector()
        {
            TerminalAppProfile[] installedProfiles = TerminalSettings.GetInstalledTerminalApps();
            string[] displayNames = new string[installedProfiles.Length];
            int selectedIndex = 0;

            for (int i = 0; i < installedProfiles.Length; i++)
            {
                displayNames[i] = TerminalSettings.GetTerminalAppDisplayName(installedProfiles[i]);
                if (installedProfiles[i] == TerminalSettings.TerminalApp)
                {
                    selectedIndex = i;
                }
            }

            int nextIndex = EditorGUILayout.Popup("Installed Terminal", selectedIndex, displayNames);
            TerminalSettings.TerminalApp = installedProfiles[Mathf.Clamp(nextIndex, 0, installedProfiles.Length - 1)];
            EditorGUILayout.HelpBox($"Selected terminal app: {TerminalSettings.GetTerminalAppDisplayName(TerminalSettings.TerminalApp)}", MessageType.None);
            if (TerminalSettings.TerminalApp == TerminalAppProfile.Ghostty)
            {
                EditorGUILayout.HelpBox("Ghostty selection can now launch the real Ghostty app into the shared tmux session via the Unity Terminal toolbar. The in-Unity surface remains the built-in renderer.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Installed terminal selection is used by the Unity Terminal toolbar to launch the selected external terminal into the shared tmux session. The in-Unity surface remains the built-in renderer.", MessageType.None);
            }
        }
    }
}
