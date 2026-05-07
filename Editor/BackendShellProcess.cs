using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Linalab.Terminal.Editor
{
    public sealed class BackendShellProcess : IShellProcess
    {
        const int ConnectTimeoutMs = 2000;
        const int BackendStartupTimeoutMs = 5000;

        readonly string _shellPath;
        readonly object _syncRoot = new();
        readonly ConcurrentQueue<string> _outputQueue = new();
        readonly ConcurrentQueue<string> _errorQueue = new();

        TcpClient _client;
        StreamReader _reader;
        StreamWriter _writer;
        Thread _readerThread;
        bool _disposed;
        bool _running;
        int _processId = -1;

        public BackendShellProcess(string shellPathOverride = null)
        {
            _shellPath = string.IsNullOrWhiteSpace(shellPathOverride) ? ShellProcess.DetectShell() : shellPathOverride;
        }

        public bool IsRunning
        {
            get
            {
                lock (_syncRoot)
                {
                    return !_disposed && _running && _client != null && _client.Connected;
                }
            }
        }

        public int ProcessId => _processId;

        public bool UsedTmuxAttachStartupPath => false;

        public void Start(string workingDirectory = null, int cols = 80, int rows = 24)
        {
            ThrowIfDisposed();
            lock (_syncRoot)
            {
                if (_running)
                {
                    return;
                }
            }

            int port = EnsureBackendPort();
            Connect(port, workingDirectory, cols, rows);
        }

        public void Write(string input)
        {
            if (string.IsNullOrEmpty(input) || _disposed)
            {
                return;
            }

            SendLine("WRITE " + Convert.ToBase64String(Encoding.UTF8.GetBytes(input)));
        }

        public void WriteByte(byte value)
        {
            if (_disposed)
            {
                return;
            }

            SendLine("WRITE " + Convert.ToBase64String(new[] { value }));
        }

        public void Kill()
        {
            if (_disposed)
            {
                return;
            }

            SendLine("KILL");
            CloseConnection();
        }

        public void Resize(int cols, int rows)
        {
            TryResize(cols, rows);
        }

        public bool TryResize(int cols, int rows)
        {
            if (_disposed || cols <= 0 || rows <= 0)
            {
                return false;
            }

            return SendLine($"RESIZE {cols} {rows}");
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

        public void DetachLocalClient()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseConnection();
        }

        void Connect(int port, string workingDirectory, int cols, int rows)
        {
            var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", port);
            if (!connectTask.Wait(ConnectTimeoutMs) || !client.Connected)
            {
                throw new InvalidOperationException("Unable to connect to Unity Terminal backend.");
            }

            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            string sessionKey = BuildSessionKey(workingDirectory);
            string shell = Convert.ToBase64String(Encoding.UTF8.GetBytes(_shellPath ?? string.Empty));
            string workspace = Convert.ToBase64String(Encoding.UTF8.GetBytes(workingDirectory ?? string.Empty));
            writer.WriteLine($"HELLO {sessionKey} {Math.Max(1, cols)} {Math.Max(1, rows)} {shell} {workspace} 0");

            lock (_syncRoot)
            {
                _client = client;
                _reader = reader;
                _writer = writer;
                _running = true;
            }

            _readerThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "Linalab Terminal Backend Reader"
            };
            _readerThread.Start();
        }

        void ReadLoop()
        {
            try
            {
                while (!_disposed)
                {
                    string line = _reader?.ReadLine();
                    if (line == null)
                    {
                        break;
                    }

                    HandleBackendLine(line);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                lock (_syncRoot)
                {
                    _running = false;
                }
            }
        }

        void HandleBackendLine(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            if (line.StartsWith("OUT ", StringComparison.Ordinal))
            {
                _outputQueue.Enqueue(DecodePayload(line.Substring(4)));
                return;
            }

            if (line.StartsWith("ERR ", StringComparison.Ordinal))
            {
                _errorQueue.Enqueue(line.Substring(4));
                return;
            }

            if (line.StartsWith("PID ", StringComparison.Ordinal)
                && int.TryParse(line.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
            {
                _processId = pid;
            }
        }

        bool SendLine(string line)
        {
            try
            {
                lock (_syncRoot)
                {
                    if (_writer == null || !_running)
                    {
                        return false;
                    }

                    _writer.WriteLine(line);
                    return true;
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        void CloseConnection()
        {
            lock (_syncRoot)
            {
                _running = false;
                try { _writer?.Dispose(); } catch (ObjectDisposedException) { }
                try { _reader?.Dispose(); } catch (ObjectDisposedException) { }
                try { _client?.Close(); } catch (SocketException) { }
                _writer = null;
                _reader = null;
                _client = null;
            }
        }

        static int EnsureBackendPort()
        {
            string portFile = GetPortFilePath();
            if (TryReadPort(portFile, out int existingPort) && CanConnect(existingPort))
            {
                return existingPort;
            }

            LaunchBackend(portFile);
            var deadline = DateTime.UtcNow.AddMilliseconds(BackendStartupTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (TryReadPort(portFile, out int port) && CanConnect(port))
                {
                    return port;
                }

                Thread.Sleep(100);
            }

            throw new InvalidOperationException("Unity Terminal backend did not start before timeout.");
        }

        static void LaunchBackend(string portFile)
        {
            string python = ResolvePythonPath();
            string script = ResolveBackendScriptPath();
            if (string.IsNullOrEmpty(python) || string.IsNullOrEmpty(script))
            {
                throw new InvalidOperationException("Python 3 is required to start the Unity Terminal backend.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(portFile));
            try
            {
                File.Delete(portFile);
            }
            catch (IOException)
            {
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"{QuoteArgument(script)} --port-file {QuoteArgument(portFile)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = TerminalSettings.GetProjectRootDirectory()
            };
            Process.Start(startInfo);
        }

        static bool TryReadPort(string portFile, out int port)
        {
            port = 0;
            try
            {
                if (!File.Exists(portFile))
                {
                    return false;
                }

                return int.TryParse(File.ReadAllText(portFile).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                    && port > 0;
            }
            catch (IOException)
            {
                return false;
            }
        }

        static bool CanConnect(int port)
        {
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync("127.0.0.1", port);
                return task.Wait(250) && client.Connected;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        static string GetPortFilePath()
        {
            return Path.Combine(TerminalSettings.GetProjectRootDirectory(), "Library", "LinalabTerminal", "backend-port.txt");
        }

        static string ResolveBackendScriptPath()
        {
            string projectRoot = TerminalSettings.GetProjectRootDirectory();
            string[] candidates =
            {
                Path.Combine(projectRoot, "Packages", "com.linalab.unity-terminal", "Backend~", "linalab_terminal_backend.py"),
                Path.Combine(projectRoot, "Library", "PackageCache", "com.linalab.unity-terminal", "Backend~", "linalab_terminal_backend.py")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            string packageCache = Path.Combine(projectRoot, "Library", "PackageCache");
            if (Directory.Exists(packageCache))
            {
                foreach (var directory in Directory.GetDirectories(packageCache, "com.linalab.unity-terminal*"))
                {
                    string candidate = Path.Combine(directory, "Backend~", "linalab_terminal_backend.py");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        static string ResolvePythonPath()
        {
            string[] candidates = { "python3", "python" };
            foreach (string candidate in candidates)
            {
                string path = ResolveCommandPath(candidate);
                if (!string.IsNullOrEmpty(path))
                {
                    return path;
                }
            }

            return null;
        }

        static string ResolveCommandPath(string command)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory, command);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            string[] commonDirectories = { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin" };
            foreach (string directory in commonDirectories)
            {
                string candidate = Path.Combine(directory, command);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        static string BuildSessionKey(string workingDirectory)
        {
            string source = string.IsNullOrWhiteSpace(workingDirectory)
                ? TerminalSettings.GetProjectRootDirectory()
                : workingDirectory;
            using var sha1 = SHA1.Create();
            byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(source ?? string.Empty));
            return "unity-terminal-" + BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 12).ToLowerInvariant();
        }

        static string DecodePayload(string payload)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }

        static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BackendShellProcess));
            }
        }
    }
}
