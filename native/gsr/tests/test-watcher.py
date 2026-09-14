#!/usr/bin/env python3
"""Private compositor fixture. Never connects to the user's compositor."""
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[1]
recorder = Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix="clypdat-watcher-test-") as directory:
    work = Path(directory)
    protocol = root / "protocols/plasma-window-management.xml"
    for mode, output in [("server-header", "plasma-window-management-server-protocol.h"), ("private-code", "protocol.c")]:
        subprocess.run(["wayland-scanner", mode, str(protocol), str(work / output)], check=True)
    flags = shlex.split(subprocess.check_output(["pkg-config", "--cflags", "--libs", "wayland-server"], text=True))
    server = work / "compositor"
    subprocess.run(["cc", "-Wall", "-Wextra", "-Werror", "-I", str(work), str(root / "tests/watcher-compositor.c"), str(work / "protocol.c"), "-o", str(server), *flags], check=True)
    for mode in ["normal", "missing"]:
        environment = dict(os.environ, XDG_RUNTIME_DIR=directory)
        environment.pop("DISPLAY", None)
        environment.pop("WAYLAND_SOCKET", None)
        process = subprocess.Popen([str(server), mode], env=environment, stdout=subprocess.PIPE, text=True)
        try:
            environment["WAYLAND_DISPLAY"] = process.stdout.readline().strip()
            result = subprocess.run([str(recorder), "--clypdat-watch-windows"], env=environment, capture_output=True, text=True, timeout=10)
            assert result.returncode == 2, result.stderr
            events = [json.loads(line) for line in result.stdout.splitlines()]
            if mode == "missing":
                assert not events
                assert "unavailable" in result.stderr
                continue
            assert [e["event"] for e in events] == ["added", "changed", "changed", "removed"], events
            first = events[0]
            assert first["uuid"] == "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
            assert first["appId"] == "steam_app_123" and first["pid"] == 1234
            assert first["title"] == 'Test "game"\nline'
            assert first["focused"] and not first["minimized"] and first["x"] == -1920
            assert not events[1]["focused"] and events[1]["minimized"]
            assert events[2]["width"] == 1280 and events[2]["height"] == 720
        finally:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=5)
    print("PASS: UUID, escaped metadata, focus/minimize, resize, removal, missing protocol, source loss")
