#!/usr/bin/env python3
import argparse
import base64
import os
import queue
import select
import shlex
import signal
import socket
import socketserver
import subprocess
import sys
import threading
import time


sessions = {}
sessions_lock = threading.Lock()


def b64(value):
    return base64.b64encode(value).decode("ascii")


def unb64(value):
    return base64.b64decode(value.encode("ascii"))


class TerminalSession:
    def __init__(self, key, shell, workspace, cols, rows):
        self.key = key
        self.shell = shell
        self.workspace = workspace
        self.cols = max(1, int(cols))
        self.rows = max(1, int(rows))
        self.clients = set()
        self.output_history = []
        self.history_limit = 4096
        self.lock = threading.Lock()
        self.dead = False
        self.master_fd = None
        self.process = None
        self._start_process()

    @property
    def pid(self):
        return self.process.pid if self.process is not None else -1

    def _start_process(self):
        if os.name == "nt":
            self.process = subprocess.Popen(
                [self.shell],
                cwd=self.workspace or None,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                bufsize=0,
            )
            threading.Thread(target=self._read_pipe_loop, daemon=True).start()
            return

        import pty
        import termios
        import fcntl
        import struct

        self.master_fd, slave_fd = pty.openpty()
        self._apply_winsize(self.rows, self.cols)
        env = os.environ.copy()
        env["TERM"] = "xterm-256color"
        env["COLUMNS"] = str(self.cols)
        env["LINES"] = str(self.rows)
        self.process = subprocess.Popen(
            [self.shell, "-i"],
            cwd=self.workspace or None,
            stdin=slave_fd,
            stdout=slave_fd,
            stderr=slave_fd,
            preexec_fn=os.setsid,
            env=env,
            close_fds=True,
        )
        os.close(slave_fd)
        threading.Thread(target=self._read_pty_loop, daemon=True).start()

    def _apply_winsize(self, rows, cols):
        if os.name == "nt" or self.master_fd is None:
            return
        import fcntl
        import termios
        import struct

        packed = struct.pack("HHHH", max(1, rows), max(1, cols), 0, 0)
        try:
            fcntl.ioctl(self.master_fd, termios.TIOCSWINSZ, packed)
        except OSError:
            pass

    def attach(self, client):
        with self.lock:
            self.clients.add(client)
            history = list(self.output_history)
        for payload in history:
            client.send_output(payload)

    def detach(self, client):
        with self.lock:
            self.clients.discard(client)

    def write(self, payload):
        if self.dead:
            return
        try:
            if os.name == "nt":
                if self.process.stdin:
                    self.process.stdin.write(payload)
                    self.process.stdin.flush()
            elif self.master_fd is not None:
                os.write(self.master_fd, payload)
        except (BrokenPipeError, OSError):
            self.dead = True

    def resize(self, cols, rows):
        self.cols = max(1, int(cols))
        self.rows = max(1, int(rows))
        self._apply_winsize(self.rows, self.cols)
        if os.name != "nt" and self.process is not None:
            try:
                os.killpg(os.getpgid(self.process.pid), signal.SIGWINCH)
            except OSError:
                pass

    def kill(self):
        self.dead = True
        try:
            if os.name == "nt":
                self.process.terminate()
            else:
                os.killpg(os.getpgid(self.process.pid), signal.SIGTERM)
        except OSError:
            pass

    def _publish(self, payload):
        with self.lock:
            self.output_history.append(payload)
            if len(self.output_history) > self.history_limit:
                del self.output_history[: len(self.output_history) - self.history_limit]
            clients = list(self.clients)
        for client in clients:
            client.send_output(payload)

    def _read_pipe_loop(self):
        while not self.dead and self.process.poll() is None:
            data = self.process.stdout.read(4096)
            if not data:
                break
            self._publish(data)
        self.dead = True

    def _read_pty_loop(self):
        while not self.dead:
            try:
                ready, _, _ = select.select([self.master_fd], [], [], 0.25)
                if not ready:
                    if self.process.poll() is not None:
                        break
                    continue
                data = os.read(self.master_fd, 4096)
                if not data:
                    break
                self._publish(data)
            except OSError:
                break
        self.dead = True


class TerminalClient:
    def __init__(self, request):
        self.request = request
        self.lock = threading.Lock()

    def send_line(self, line):
        with self.lock:
            self.request.sendall((line + "\n").encode("utf-8"))

    def send_output(self, payload):
        self.send_line("OUT " + b64(payload))


class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        client = TerminalClient(self.request)
        session = None
        try:
            first = self.rfile.readline().decode("utf-8", "replace").rstrip("\n")
            parts = first.split(" ", 6)
            if len(parts) != 7 or parts[0] != "HELLO":
                client.send_line("ERR invalid-handshake")
                return

            key = parts[1]
            cols = int(parts[2])
            rows = int(parts[3])
            shell = unb64(parts[4]).decode("utf-8", "replace")
            workspace = unb64(parts[5]).decode("utf-8", "replace")

            with sessions_lock:
                session = sessions.get(key)
                if session is None or session.dead:
                    session = TerminalSession(key, shell, workspace, cols, rows)
                    sessions[key] = session

            client.send_line("PID " + str(session.pid))
            session.attach(client)
            for raw in self.rfile:
                line = raw.decode("utf-8", "replace").rstrip("\n")
                if line.startswith("WRITE "):
                    session.write(unb64(line[6:]))
                elif line.startswith("RESIZE "):
                    resize_parts = line.split(" ")
                    if len(resize_parts) == 3:
                        session.resize(int(resize_parts[1]), int(resize_parts[2]))
                elif line == "KILL":
                    session.kill()
                    with sessions_lock:
                        sessions.pop(session.key, None)
                    break
                elif line == "PING":
                    client.send_line("PONG")
        finally:
            if session is not None:
                session.detach(client)


class ThreadedServer(socketserver.ThreadingMixIn, socketserver.TCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--port-file", required=True)
    args = parser.parse_args()

    with ThreadedServer((args.host, args.port), Handler) as server:
        host, port = server.server_address
        os.makedirs(os.path.dirname(args.port_file), exist_ok=True)
        with open(args.port_file, "w", encoding="utf-8") as port_file:
            port_file.write(str(port))
        server.serve_forever(poll_interval=0.25)


if __name__ == "__main__":
    main()
