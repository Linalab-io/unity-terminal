using System;
using System.Diagnostics;
using UnityEditor;

namespace Linalab.Terminal.Editor
{
    public static class TerminalAppLauncher
    {
        public static bool LaunchSelected(string workingDirectory, string tmuxSessionName, out string error)
        {
            string command = BuildShellCommand(workingDirectory, tmuxSessionName);
            switch (TerminalSettings.TerminalApp)
            {
                case TerminalAppProfile.Ghostty:
                    return LaunchGhostty(workingDirectory, command, out error);

                case TerminalAppProfile.SystemDefault:
                case TerminalAppProfile.Terminal:
                    return LaunchTerminalApp(workingDirectory, command, out error);

                case TerminalAppProfile.ITerm:
                    return LaunchITerm(workingDirectory, command, out error);

                default:
                    error = $"Launching {TerminalSettings.GetTerminalAppDisplayName(TerminalSettings.TerminalApp)} is not implemented yet. Select Ghostty, Terminal.app, or iTerm.";
                    return false;
            }
        }

        static bool LaunchGhostty(string workingDirectory, string command, out string error)
        {
            string script = $@"
tell application ""Ghostty""
  activate
  set cfg to new surface configuration
  set initial working directory of cfg to ""{EscapeAppleScriptString(workingDirectory)}""
  set command of cfg to ""{EscapeAppleScriptString(command)}""
  new window with configuration cfg
end tell";
            return RunAppleScript(script, out error);
        }

        static bool LaunchTerminalApp(string workingDirectory, string command, out string error)
        {
            string script = $@"
tell application ""Terminal""
  activate
  do script ""{EscapeAppleScriptString(command)}""
end tell";
            return RunAppleScript(script, out error);
        }

        static bool LaunchITerm(string workingDirectory, string command, out string error)
        {
            string script = $@"
tell application ""iTerm""
  activate
  create window with default profile command ""{EscapeAppleScriptString(command)}""
end tell";
            return RunAppleScript(script, out error);
        }

        static string BuildShellCommand(string workingDirectory, string tmuxSessionName)
        {
            string quotedWorkingDirectory = QuoteForShell(workingDirectory);
            string quotedSessionName = QuoteForShell(tmuxSessionName);
            string shellSnippet = $"cd {quotedWorkingDirectory} && (tmux attach -t {quotedSessionName} || tmux new -s {quotedSessionName})";
            return $"/bin/zsh -lc {QuoteForShell(shellSnippet)}";
        }

        static bool RunAppleScript(string script, out string error)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/osascript",
                        Arguments = "-",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.StandardInput.Write(script);
                process.StandardInput.Close();
                string standardError = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit(5000);

                if (process.ExitCode != 0)
                {
                    error = string.IsNullOrWhiteSpace(standardError) ? "Failed to launch selected terminal." : standardError;
                    return false;
                }

                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        static string EscapeAppleScriptString(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static string QuoteForShell(string value)
        {
            return $"'{(value ?? string.Empty).Replace("'", "'\"'\"'")}'";
        }
    }
}
