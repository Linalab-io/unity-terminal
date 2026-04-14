using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Linalab.Terminal.Editor
{
    public interface IShellProcess : IDisposable
    {
        bool IsRunning { get; }
        int ProcessId { get; }
        void Start(string workingDirectory = null, bool attachToTmux = false);
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

        public void Start(string workingDirectory = null, bool attachToTmux = false)
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
                var startInfo = CreateStartInfo(shellPath, workingDirectory, attachToTmux);

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
            }
        }

        public void Write(string input)
        {
            if (string.IsNullOrEmpty(input) || _disposed)
            {
                return;
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

        public void WriteByte(byte value)
        {
            if (_disposed)
            {
                return;
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

        static ProcessStartInfo CreateStartInfo(string shellPath, string workingDirectory, bool attachToTmux)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = shellPath,
                Arguments = GetShellArguments(shellPath, workingDirectory, attachToTmux),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
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
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";
            startInfo.Environment["COLUMNS"] = "80";
            startInfo.Environment["LINES"] = "24";

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists("/usr/bin/script"))
            {
                startInfo.FileName = "/usr/bin/script";
                startInfo.Arguments = CreatePseudoTerminalArguments(shellPath, workingDirectory, attachToTmux);
            }

            return startInfo;
        }

        static string CreatePseudoTerminalArguments(string shellPath, string workingDirectory, bool attachToTmux)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return $"-q /dev/null {QuoteArgument(shellPath)} {GetShellArguments(shellPath, workingDirectory, attachToTmux)}".Trim();
            }

            return $"-q -c {QuoteArgument($"{shellPath} {GetShellArguments(shellPath, workingDirectory, attachToTmux)}".Trim())} /dev/null";
        }

        static string GetShellArguments(string shellPath, string workingDirectory, bool attachToTmux)
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

            string tmuxStartupCommand = BuildTmuxStartupCommand(shellPath, workingDirectory, attachToTmux);
            if (!string.IsNullOrEmpty(tmuxStartupCommand))
            {
                return $"-i -c {QuoteArgument(tmuxStartupCommand)}";
            }

            return GetInteractiveShellArguments(shellPath);
        }

        static string GetInteractiveShellArguments(string shellPath)
        {
            string fileName = Path.GetFileName(shellPath);
            if (string.Equals(fileName, "bash", StringComparison.OrdinalIgnoreCase))
            {
                return "-i";
            }

            if (string.Equals(fileName, "zsh", StringComparison.OrdinalIgnoreCase))
            {
                return "-i";
            }

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

        static bool CommandExists(string command)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(Path.Combine(directory, command)))
                {
                    return true;
                }
            }

            return false;
        }

        static string BuildTmuxStartupCommand(string shellPath, string workingDirectory, bool attachToTmux)
        {
            if (!attachToTmux)
            {
                return null;
            }

            string sessionName = TerminalSettings.GetTmuxSessionName(workingDirectory);
            string tmuxCommand = $"env TMUX= tmux new-session -A -s {QuoteForShell(sessionName)}";
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                tmuxCommand += $" -c {QuoteForShell(workingDirectory)}";
            }

            return $"if command -v tmux >/dev/null 2>&1; then {tmuxCommand}; fi; exec {QuoteForShell(shellPath)} -i";
        }

        static string QuoteForShell(string value)
        {
            return $"'{value.Replace("'", "'\"'\"'")}'";
        }

        void StartReaders()
        {
            StopReaders();
            _readerCancellation = new CancellationTokenSource();
            _outputReaderThread = CreateReaderThread(_process.StandardOutput, _outputQueue, _readerCancellation.Token, "UnityTerminal-stdout");
            _errorReaderThread = CreateReaderThread(_process.StandardError, _errorQueue, _readerCancellation.Token, "UnityTerminal-stderr");
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

        static Thread CreateReaderThread(StreamReader reader, ConcurrentQueue<string> queue, CancellationToken cancellationToken, string name)
        {
            return new Thread(() => ReadLoop(reader, queue, cancellationToken))
            {
                IsBackground = true,
                Name = name
            };
        }

        static void ReadLoop(StreamReader reader, ConcurrentQueue<string> queue, CancellationToken cancellationToken)
        {
            var buffer = new char[1024];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int read = reader.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    queue.Enqueue(new string(buffer, 0, read));
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
