using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Linalab;

namespace Linalab.Terminal.Editor
{
    sealed class TmuxSessionInfo
    {
        public string Name;
        public string WorkspacePath;
    }

    public interface IShellProcess : IDisposable
    {
        bool IsRunning { get; }
        int ProcessId { get; }
        void Start(string workingDirectory = null, int cols = 80, int rows = 24);
        void Write(string input);
        void WriteByte(byte value);
        void Kill();
        void Resize(int cols, int rows);
    }

    public sealed class ShellProcess : IShellProcess
    {
        readonly string _shellPath;
        readonly object _syncRoot = new();
        readonly ConcurrentQueue<string> _outputQueue = new();
        readonly ConcurrentQueue<string> _errorQueue = new();

        Process _process;
        CancellationTokenSource _readerCancellation;
        Thread _outputReaderThread;
        Thread _errorReaderThread;
        bool _disposed;
        int _exitEventRaised;

        public ShellProcess(string shellPathOverride = null)
        {
            _shellPath = string.IsNullOrWhiteSpace(shellPathOverride) ? DetectShell() : shellPathOverride;
        }

        public bool IsRunning
        {
            get
            {
                lock (_syncRoot)
                {
                    return IsProcessRunning(_process);
                }
            }
        }

        public int ProcessId
        {
            get
            {
                lock (_syncRoot)
                {
                    if (!IsProcessRunning(_process))
                    {
                        return -1;
                    }

                    try
                    {
                        return _process.Id;
                    }
                    catch (InvalidOperationException)
                    {
                        return -1;
                    }
                }
            }
        }

        public static string DetectShell()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (CommandExists("pwsh.exe"))
                {
                    return "pwsh.exe";
                }

                if (CommandExists("powershell.exe"))
                {
                    return "powershell.exe";
                }

                return "cmd.exe";
            }

            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrWhiteSpace(shell))
            {
                return shell;
            }

            if (File.Exists("/bin/zsh"))
            {
                return "/bin/zsh";
            }

            return "/bin/bash";
        }

        public void Start(string workingDirectory = null, int cols = 80, int rows = 24)
        {
            ThrowIfDisposed();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("com.linalab.unity-terminal currently supports POSIX Unity Editor shells only.");
            }

            lock (_syncRoot)
            {
                if (IsProcessRunning(_process))
                {
                    return;
                }

                DisposeProcess();
                var shellPath = ResolveStartShellPath(_shellPath);
                var startInfo = CreateStartInfo(shellPath, workingDirectory, cols, rows);

                _process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                _exitEventRaised = 0;
                _process.OutputDataReceived += OnOutputDataReceived;
                _process.ErrorDataReceived += OnErrorDataReceived;
                _process.Exited += OnProcessExited;
                _process.Start();
                StartReaders();
                TryResize(cols, rows);
            }
        }

        public void Write(string input)
        {
            if (string.IsNullOrEmpty(input) || _disposed)
            {
                return;
            }

            if (input.Contains("\uFEFF", StringComparison.Ordinal) || string.Equals(input, "\r", StringComparison.Ordinal))
            {
                VerboseLog($"[ShellWrite] payload={DescribePayload(input)} stack={new StackTrace(1, false)}");
            }

            try
            {
                lock (_syncRoot)
                {
                    if (!IsProcessRunning(_process))
                    {
                        return;
                    }

                    _process.StandardInput.Write(input);
                    _process.StandardInput.Flush();
                }
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        static string DescribePayload(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return "<empty>";
            }

            var builder = new StringBuilder();
            builder.Append('[');
            for (int i = 0; i < input.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("U+");
                builder.Append(((int)input[i]).ToString("X4", CultureInfo.InvariantCulture));
            }

            builder.Append("] \"");
            builder.Append(input.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal));
            builder.Append('"');
            return builder.ToString();
        }

        public void WriteByte(byte value)
        {
            if (_disposed)
            {
                return;
            }

            if (value == 0xEF || value == 0xBB || value == 0xBF || value == 0x0D)
            {
                VerboseLog($"[ShellWriteByte] value=0x{value:X2} stack={new StackTrace(1, false)}");
            }

            try
            {
                lock (_syncRoot)
                {
                    if (!IsProcessRunning(_process))
                    {
                        return;
                    }

                    _process.StandardInput.BaseStream.WriteByte(value);
                    _process.StandardInput.BaseStream.Flush();
                }
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Kill()
        {
            if (_disposed)
            {
                return;
            }

            Process process;
            lock (_syncRoot)
            {
                process = _process;
                if (!IsProcessRunning(process))
                {
                    return;
                }
            }

            TerminateProcess(process);
        }

        public void Resize(int cols, int rows)
        {
            TryResize(cols, rows);
        }

        static void VerboseLog(string message)
        {
            if (!TerminalSettings.VerboseLogging)
            {
                return;
            }

            D.Log(message);
        }

        public bool TryResize(int cols, int rows)
        {
            if (_disposed || cols <= 0 || rows <= 0)
            {
                return false;
            }

            int processId;
            lock (_syncRoot)
            {
                if (!IsProcessRunning(_process))
                {
                    return false;
                }

                try
                {
                    processId = _process.Id;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }

            if (!TryResolveResizeTarget(processId, out int targetProcessId, out var terminalPath))
            {
                return false;
            }

            ApplyTerminalSize(terminalPath, cols, rows);
            int processGroupId = GetForegroundProcessGroupId(targetProcessId);
            SignalWindowSizeChanged(targetProcessId, processGroupId);
            return true;
        }

        public void DrainOutput(Action<string> handler)
        {
            if (handler == null)
            {
                return;
            }

            while (_outputQueue.TryDequeue(out var line))
            {
                handler(line);
            }
        }

        public void DrainErrors(Action<string> handler)
        {
            if (handler == null)
            {
                return;
            }

            while (_errorQueue.TryDequeue(out var line))
            {
                handler(line);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Process process;
            lock (_syncRoot)
            {
                process = _process;
            }

            try
            {
                TerminateProcess(process);
            }
            catch (InvalidOperationException)
            {
            }

            _disposed = true;
            lock (_syncRoot)
            {
                DisposeProcess();
            }
        }

        void OnOutputDataReceived(object sender, DataReceivedEventArgs eventArgs)
        {
        }

        void OnErrorDataReceived(object sender, DataReceivedEventArgs eventArgs)
        {
        }

        void OnProcessExited(object sender, EventArgs eventArgs)
        {
            if (Interlocked.Exchange(ref _exitEventRaised, 1) != 0)
            {
                return;
            }
        }

        void DisposeProcess()
        {
            if (_process == null)
            {
                return;
            }

            StopReaders();
            _process.OutputDataReceived -= OnOutputDataReceived;
            _process.ErrorDataReceived -= OnErrorDataReceived;
            _process.Exited -= OnProcessExited;

            try
            {
                _process.Dispose();
            }
            catch (InvalidOperationException)
            {
            }

            _process = null;
        }

        static bool IsProcessRunning(Process process)
        {
            if (process == null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        static string ResolveStartShellPath(string shellPath)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return shellPath;
            }

            if (!string.IsNullOrWhiteSpace(shellPath) && File.Exists(shellPath))
            {
                return shellPath;
            }

            if (File.Exists("/bin/zsh"))
            {
                return "/bin/zsh";
            }

            return "/bin/bash";
        }

        static ProcessStartInfo CreateStartInfo(string shellPath, string workingDirectory, int cols, int rows)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = shellPath,
                Arguments = GetShellArguments(shellPath),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            startInfo.Environment["TERM"] = "xterm-256color";
            startInfo.Environment["LANG"] = "en_US.UTF-8";
            startInfo.Environment["LC_ALL"] = "en_US.UTF-8";
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";
            startInfo.Environment["COLUMNS"] = Math.Max(1, cols).ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["LINES"] = Math.Max(1, rows).ToString(CultureInfo.InvariantCulture);
            startInfo.Environment.Remove("TMUX");

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists("/usr/bin/script"))
            {
                startInfo.FileName = "/usr/bin/script";
                startInfo.Arguments = CreatePseudoTerminalArguments(shellPath);
            }

            return startInfo;
        }

        static bool TryGetControllingTerminalPath(int processId, out string terminalPath)
        {
            terminalPath = null;

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/ps",
                        Arguments = $"-o tty= -p {processId}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(250);
                if (string.IsNullOrWhiteSpace(output) || output == "?" || output == "??")
                {
                    return false;
                }

                terminalPath = output.StartsWith("/dev/", StringComparison.Ordinal)
                    ? output
                    : $"/dev/{output}";

                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        static bool TryResolveResizeTarget(int processId, out int targetProcessId, out string terminalPath)
        {
            targetProcessId = processId;
            if (TryGetControllingTerminalPath(processId, out terminalPath))
            {
                return true;
            }

            foreach (int childProcessId in GetChildProcessIds(processId))
            {
                if (!TryGetControllingTerminalPath(childProcessId, out terminalPath))
                {
                    continue;
                }

                targetProcessId = childProcessId;
                return true;
            }

            terminalPath = null;
            return false;
        }

        static int[] GetChildProcessIds(int parentProcessId)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/pgrep",
                        Arguments = $"-P {parentProcessId}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(250);
                if (string.IsNullOrWhiteSpace(output))
                {
                    return Array.Empty<int>();
                }

                string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var childProcessIds = new int[lines.Length];
                int count = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int childProcessId))
                    {
                        continue;
                    }

                    childProcessIds[count++] = childProcessId;
                }

                if (count == childProcessIds.Length)
                {
                    return childProcessIds;
                }

                Array.Resize(ref childProcessIds, count);
                return childProcessIds;
            }
            catch (InvalidOperationException)
            {
                return Array.Empty<int>();
            }
            catch (IOException)
            {
                return Array.Empty<int>();
            }
        }

        static int GetForegroundProcessGroupId(int processId)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/ps",
                        Arguments = $"-o tpgid= -p {processId}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(250);
                return int.TryParse(output, out int processGroupId) && processGroupId > 0
                    ? processGroupId
                    : -1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
            catch (IOException)
            {
                return -1;
            }
        }

        static void ApplyTerminalSize(string terminalPath, int cols, int rows)
        {
            try
            {
                string terminalFlag = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "-f" : "-F";
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/stty",
                        Arguments = $"{terminalFlag} {terminalPath} rows {rows} cols {cols}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit(250);
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
        }

        static void SignalWindowSizeChanged(int processId, int processGroupId)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/kill",
                        Arguments = processGroupId > 0
                            ? $"-WINCH -- -{processGroupId}"
                            : $"-WINCH {processId}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit(250);
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
        }

        static string CreatePseudoTerminalArguments(string shellPath)
        {
            var innerCommand = BuildPseudoTerminalInnerCommand(shellPath);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return $"-q /dev/null {innerCommand}".Trim();
            }

            return $"-q -c {QuoteArgument(innerCommand)} /dev/null";
        }

        static string BuildPseudoTerminalInnerCommand(string shellPath)
        {
            if (TerminalSettings.TmuxAutoAttach)
            {
                string tmuxPath = ResolveCommandPath("tmux");
                if (!string.IsNullOrEmpty(tmuxPath))
                {
                    string sessionName = TerminalSettings.GetTmuxSessionName();
                    string quotedTmuxPath = QuoteShellArgument(tmuxPath);
                    string quotedSessionName = QuoteShellArgument(sessionName);
                    string tmuxCommand = $"if {quotedTmuxPath} has-session -t {quotedSessionName} 2>/dev/null; then exec {quotedTmuxPath} attach-session -t {quotedSessionName}; else exec {quotedTmuxPath} new-session -s {quotedSessionName}; fi";
                    return BuildInteractiveShellCommand(shellPath, tmuxCommand);
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return $"{QuoteArgument(shellPath)} {GetShellArguments(shellPath)}".Trim();
            }

            return $"{shellPath} {GetShellArguments(shellPath)}".Trim();
        }

        static string GetShellArguments(string shellPath)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string fileName = Path.GetFileName(shellPath);
                if (string.Equals(fileName, "cmd.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return "-NoLogo";
            }

            return GetInteractiveShellArguments(shellPath);
        }

        static string BuildInteractiveShellCommand(string shellPath, string command)
        {
            return $"{QuoteArgument(shellPath)} {GetShellArguments(shellPath)} -c {QuoteArgument(command)}".Trim();
        }

        static string GetInteractiveShellArguments(string shellPath)
        {
            // Use a plain interactive shell so the user's profile/rc files run and
            // the default shell prompt remains visible.
            return "-i";
        }

        static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }

        static string QuoteShellArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "''";
            }

            return $"'{value.Replace("'", "'\\''")}'";
        }

        static bool CommandExists(string command)
        {
            return !string.IsNullOrEmpty(ResolveCommandPath(command));
        }

        static string ResolveCommandPath(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                return null;
            }

            if (Path.IsPathRooted(command) && File.Exists(command))
            {
                return command;
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory, command);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string[] commonMacBinaryDirectories =
                {
                    "/opt/homebrew/bin",
                    "/usr/local/bin"
                };

                foreach (var directory in commonMacBinaryDirectories)
                {
                    var candidate = Path.Combine(directory, command);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        public static string[] ListTmuxSessions()
        {
            var sessions = ListTmuxSessionInfos();
            if (sessions.Length == 0)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>(sessions.Length);
            for (int i = 0; i < sessions.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(sessions[i]?.Name))
                {
                    names.Add(sessions[i].Name);
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        static TmuxSessionInfo[] ListTmuxSessionInfos()
        {
            var tmuxPath = ResolveCommandPath("tmux");
            if (string.IsNullOrEmpty(tmuxPath))
            {
                return Array.Empty<TmuxSessionInfo>();
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = tmuxPath,
                        Arguments = "list-sessions -F #{session_name}\t#{session_path}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(1500);
                if (process.ExitCode != 0)
                {
                    return Array.Empty<TmuxSessionInfo>();
                }

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var result = new List<TmuxSessionInfo>(lines.Length);
                foreach (var rawLine in lines)
                {
                    var line = rawLine?.Trim();
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    var parts = line.Split('\t');
                    var name = parts.Length > 0 ? parts[0].Trim() : string.Empty;
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    var workspacePath = parts.Length > 1 ? NormalizeWorkspacePath(parts[1]) : null;
                    result.Add(new TmuxSessionInfo
                    {
                        Name = name,
                        WorkspacePath = workspacePath
                    });
                }

                return result.ToArray();
            }
            catch (InvalidOperationException)
            {
                return Array.Empty<TmuxSessionInfo>();
            }
            catch (IOException)
            {
                return Array.Empty<TmuxSessionInfo>();
            }
        }

        public static string[] ListTmuxWorkspaceSessions(string workspacePath)
        {
            var normalizedWorkspacePath = NormalizeWorkspacePath(workspacePath);
            if (string.IsNullOrEmpty(normalizedWorkspacePath))
            {
                return Array.Empty<string>();
            }

            var all = ListTmuxSessionInfos();
            if (all.Length == 0)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>();
            foreach (var session in all)
            {
                if (!string.Equals(session.WorkspacePath, normalizedWorkspacePath, StringComparison.Ordinal))
                {
                    continue;
                }

                result.Add(session.Name);
            }

            result.Sort(StringComparer.Ordinal);
            return result.ToArray();
        }

        public static string FindUnusedTmuxSessionName(string canonical)
        {
            if (string.IsNullOrEmpty(canonical))
            {
                return canonical;
            }

            var existing = new HashSet<string>(ListTmuxSessions(), StringComparer.Ordinal);
            if (!existing.Contains(canonical))
            {
                return canonical;
            }

            for (int i = 2; i < 1000; i++)
            {
                var candidate = canonical + "-" + i.ToString(CultureInfo.InvariantCulture);
                if (!existing.Contains(candidate))
                {
                    return candidate;
                }
            }

            return canonical + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        static string NormalizeWorkspacePath(string workspacePath)
        {
            var trimmedPath = workspacePath?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(trimmedPath))
            {
                return null;
            }

            var hasDrivePrefix = trimmedPath.Length >= 3
                && char.IsLetter(trimmedPath[0])
                && trimmedPath[1] == ':'
                && (trimmedPath[2] == '/' || trimmedPath[2] == '\\');
            var withoutTrailingSlash = trimmedPath.Replace('\\', '/').TrimEnd('/');
            var absolutePath = hasDrivePrefix || withoutTrailingSlash.StartsWith("/", StringComparison.Ordinal)
                ? withoutTrailingSlash
                : Path.GetFullPath(withoutTrailingSlash);
            var normalized = absolutePath.Replace('\\', '/');

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return normalized.ToLowerInvariant();
            }

            return normalized;
        }

        void StartReaders()
        {
            StopReaders();
            _readerCancellation = new CancellationTokenSource();
            _outputReaderThread = CreateReaderThread(_process.StandardOutput.BaseStream, _outputQueue, _readerCancellation.Token, "UnityTerminal-stdout");
            _errorReaderThread = CreateReaderThread(_process.StandardError.BaseStream, _errorQueue, _readerCancellation.Token, "UnityTerminal-stderr");
            _outputReaderThread.Start();
            _errorReaderThread.Start();
        }

        void StopReaders()
        {
            _readerCancellation?.Cancel();
            JoinReader(_outputReaderThread);
            JoinReader(_errorReaderThread);
            _outputReaderThread = null;
            _errorReaderThread = null;
            _readerCancellation?.Dispose();
            _readerCancellation = null;
        }

        static Thread CreateReaderThread(Stream stream, ConcurrentQueue<string> queue, CancellationToken cancellationToken, string name)
        {
            return new Thread(() => ReadLoop(stream, queue, cancellationToken))
            {
                IsBackground = true,
                Name = name
            };
        }

        static void ReadLoop(Stream stream, ConcurrentQueue<string> queue, CancellationToken cancellationToken)
        {
            var byteBuffer = new byte[4096];
            var charBuffer = new char[4096];
            Decoder decoder = Encoding.UTF8.GetDecoder();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int read = stream.Read(byteBuffer, 0, byteBuffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    int charCount = decoder.GetChars(byteBuffer, 0, read, charBuffer, 0, flush: false);
                    if (charCount > 0)
                    {
                        queue.Enqueue(new string(charBuffer, 0, charCount));
                    }
                }

                int flushedCount = decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuffer, 0, flush: true);
                if (flushedCount > 0)
                {
                    queue.Enqueue(new string(charBuffer, 0, flushedCount));
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        static void JoinReader(Thread thread)
        {
            if (thread == null)
            {
                return;
            }

            try
            {
                thread.Join(150);
            }
            catch (ThreadStateException)
            {
            }
        }

        static void TerminateProcess(Process process)
        {
            if (!IsProcessRunning(process))
            {
                return;
            }

            try
            {
                process.StandardInput.Write("\x03");
                process.StandardInput.Flush();
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                if (process.WaitForExit(750))
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(500);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ShellProcess));
            }
        }
    }
}
