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
        TmuxSessionInfo[] _sessions;
        string _canonical;
        TmuxPickerResult _result = TmuxPickerResult.Cancel;
        string _selected;
        Vector2 _scroll;

        public static TmuxPickerResult ShowModal(TmuxSessionInfo[] sessions, string canonical, out string selected)
        {
            var window = CreateInstance<TmuxSessionPicker>();
            window.titleContent = new GUIContent("Tmux Sessions");
            window._sessions = sessions ?? Array.Empty<TmuxSessionInfo>();
            window._canonical = canonical ?? string.Empty;
            window.minSize = new Vector2(520f, 360f);
            window.maxSize = new Vector2(960f, 900f);
            window.ShowModalUtility();
            selected = window._selected;
            return window._result;
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("How should Unity Terminal handle tmux?", EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(_canonical))
            {
                EditorGUILayout.LabelField($"Recommended session for this workspace: {_canonical}", EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField($"Detected tmux sessions: {_sessions?.Length ?? 0}", EditorStyles.miniLabel);
            EditorGUILayout.Space();

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (_sessions == null || _sessions.Length == 0)
            {
                EditorGUILayout.HelpBox("No existing tmux sessions were found. Create New starts a new session, or Cancel leaves auto tmux disabled.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("All detected tmux sessions are listed below. Attach selects that exact session, Create New makes a new one for this workspace, and Cancel leaves auto tmux disabled.", MessageType.None);
                EditorGUILayout.Space();

                foreach (var session in _sessions)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var label = string.Equals(session.Name, _canonical, StringComparison.Ordinal)
                            ? session.Name + "  (canonical)"
                            : session.Name;
                        GUILayout.Label(label, EditorStyles.label, GUILayout.ExpandWidth(true));
                        
                        var attachLabel = string.IsNullOrEmpty(session.WorkspacePath) 
                            ? "Attach" 
                            : $"Attach {System.IO.Path.GetFileName(session.WorkspacePath)}";
                            
                        if (GUILayout.Button(attachLabel, GUILayout.MinWidth(80f)))
                        {
                            _result = TmuxPickerResult.Attach;
                            _selected = session.Name;
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
