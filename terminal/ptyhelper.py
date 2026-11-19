#!/usr/bin/env python3
"""
PTY helper for Red Star Terminal (Electron cannot allocate a pty without a native module).

usage: ptyhelper.py COLS ROWS CWD PROGRAM [ARGS...]
  stdin / stdout            : terminal input / output (raw bytes)
  fd named by $REDSTAR_CTL_FD : optional control channel, lines "R <cols> <rows>" resize the terminal
Exits with the child's exit status.
"""
import errno
import fcntl
import os
import pty
import select
import signal
import struct
import sys
import termios
import time


def set_size(fd, cols, rows):
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))


def write_all(fd, data):
    while data:
        try:
            n = os.write(fd, data)
        except OSError as e:
            if e.errno in (errno.EINTR, errno.EAGAIN):
                continue
            raise
        data = data[n:]


def reap(pid):
    """Hang the shell up, give it a moment to exit, then kill it. Returns the wait status."""
    for sig, wait in ((signal.SIGHUP, 1.0), (signal.SIGKILL, 2.0)):
        try:
            os.kill(pid, sig)
        except OSError:
            pass
        deadline = time.time() + wait
        while time.time() < deadline:
            try:
                done, status = os.waitpid(pid, os.WNOHANG)
            except OSError:
                return 0
            if done:
                return status
            time.sleep(0.02)
    return 0


def main():
    cols, rows, cwd = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
    argv = sys.argv[4:]
    if not argv:
        sys.exit(2)

    pid, master = pty.fork()
    if pid == 0:                                    # child: becomes the shell
        try:
            os.chdir(cwd)
        except OSError:
            pass
        try:
            set_size(0, cols, rows)
        except OSError:
            pass
        # Start the shell with default signal handling (Python / the parent may ignore some).
        for name in ("SIGPIPE", "SIGHUP", "SIGINT", "SIGQUIT", "SIGTERM", "SIGTSTP", "SIGTTIN", "SIGTTOU", "SIGCHLD"):
            try:
                signal.signal(getattr(signal, name), signal.SIG_DFL)
            except (OSError, ValueError, AttributeError):
                pass
        env = dict(os.environ)
        env["TERM"] = "xterm-256color"
        env["COLORTERM"] = "truecolor"
        env.pop("REDSTAR_CTL_FD", None)
        try:
            os.execvpe(argv[0], argv, env)
        finally:
            os._exit(127)

    ctl = -1
    try:
        ctl = int(os.environ.get("REDSTAR_CTL_FD", "-1"))
        os.fstat(ctl)
    except (ValueError, OSError):
        ctl = -1

    out = sys.stdout.buffer
    fds = [master, 0] + ([ctl] if ctl >= 0 else [])
    buf = b""
    status = 0
    try:
        while True:
            try:
                ready, _, _ = select.select(fds, [], [])
            except InterruptedError:
                continue
            if master in ready:
                try:
                    data = os.read(master, 65536)
                except OSError:
                    data = b""                      # EIO: the shell has exited
                if not data:
                    break
                out.write(data)
                out.flush()
            if 0 in ready:
                data = os.read(0, 65536)
                if not data:                        # the app closed our stdin
                    break
                write_all(master, data)
            if ctl in ready:
                try:
                    chunk = os.read(ctl, 4096)
                except OSError:
                    chunk = b""
                if not chunk:
                    fds.remove(ctl)
                    ctl = -1
                buf += chunk
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    parts = line.split()
                    if len(parts) == 3 and parts[0] == b"R":
                        try:
                            set_size(master, int(parts[1]), int(parts[2]))
                        except (ValueError, OSError):
                            pass
    finally:
        status = reap(pid)
    code = os.waitstatus_to_exitcode(status) if hasattr(os, "waitstatus_to_exitcode") else (status >> 8)
    sys.exit(code if code >= 0 else 128 - code)


if __name__ == "__main__":
    main()
