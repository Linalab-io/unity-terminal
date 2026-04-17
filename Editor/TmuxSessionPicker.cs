using System;
using UnityEditor;
using UnityEngine;

namespace Linalab.Terminal.Editor
{
    public enum TmuxPickerResult
    {
        Cancel,
        Attach,
        CreateNew
    }

    internal sealed class TmuxSessionPicker : EditorWindow
    {
        string[] _sessions;
        string _canonical;
        TmuxPickerResult _result = TmuxPickerResult.Cancel;
        string _selected;
        Vector2 _scroll;

        public static TmuxPickerResult ShowModal(string[] sessions, string canonical, out string selected)
        {
            var window = CreateInstance<TmuxSessionPicker>();
            window.titleContent = new GUIContent("Tmux Session");
            window._sessions = sessions ?? Array.Empty<string>();
            window._canonical = canonical ?? string.Empty;
            window.minSize = new Vector2(380f, 220f);
            window.maxSize = new Vector2(640f, 500f);
            window.ShowModalUtility();
            selected = window._selected;
            return window._result;
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Existing tmux sessions for this workspace", EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(_canonical))
            {
                EditorGUILayout.LabelField($"Canonical: {_canonical}", EditorStyles.miniLabel);
            }
            EditorGUILayout.Space();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_sessions == null || _sessions.Length == 0)
            {
                EditorGUILayout.HelpBox("No existing tmux sessions for this workspace.", MessageType.Info);
            }
            else
            {
                foreach (var session in _sessions)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var label = string.Equals(session, _canonical, StringComparison.Ordinal)
                            ? session + "  (canonical)"
                            : session;
                        GUILayout.Label(label, EditorStyles.label, GUILayout.ExpandWidth(true));
                        if (GUILayout.Button("Attach", GUILayout.Width(80f)))
                        {
                            _result = TmuxPickerResult.Attach;
                            _selected = session;
                            Close();
                            GUIUtility.ExitGUI();
                            return;
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Create New", GUILayout.Width(100f)))
                {
                    _result = TmuxPickerResult.CreateNew;
                    Close();
                    GUIUtility.ExitGUI();
                    return;
                }
                if (GUILayout.Button("Cancel", GUILayout.Width(80f)))
                {
                    _result = TmuxPickerResult.Cancel;
                    Close();
                    GUIUtility.ExitGUI();
                    return;
                }
            }
        }
    }
}
