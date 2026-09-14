#!/usr/bin/env python3
"""Register this build folder. Rerun after moving it to a different path."""
import os
from pathlib import Path
import shutil
import subprocess
import sys

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parent

def quote_exec(path):
    text = str(path)
    if not path.is_absolute() or any(ord(c) < 32 or ord(c) == 127 for c in text):
        raise ValueError("Desktop executable must be an absolute path without control characters")
    for old, new in [("\\", "\\\\"), ('"', '\\"'), ("`", "\\`"), ("$", "\\$"), ("%", "%%")]:
        text = text.replace(old, new)
    return '"' + text.replace("\\", "\\\\") + '"'

app = root / "ClypDat"
recorder = root / "libexec/clypdat-gsr"
for executable in [app, recorder]:
    if not executable.is_file() or not os.access(executable, os.X_OK):
        raise SystemExit(f"Missing executable: {executable}")
data = Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local/share")))
if not data.is_absolute():
    data = Path.home() / ".local/share"
desktop = data / "applications"
desktop.mkdir(parents=True, exist_ok=True)
entries = {
    "com.clyplabs.ClypDat.desktop": f"[Desktop Entry]\nType=Application\nName=ClypDat (Experimental Linux)\nExec={quote_exec(app)}\nIcon={root / 'clypdat-icon.png'}\nTerminal=false\nCategories=AudioVideo;Recorder;\n",
    "com.clyplabs.ClypDat.Recorder.desktop": f"[Desktop Entry]\nType=Application\nName=ClypDat Private Recorder\nExec={quote_exec(recorder)}\nNoDisplay=true\nTerminal=false\nX-KDE-Wayland-Interfaces=org_kde_plasma_window_management,zkde_screencast_unstable_v1\n",
}
for name, text in entries.items():
    destination = desktop / name
    destination.write_text(text)
    if shutil.which("desktop-file-validate"):
        subprocess.run(["desktop-file-validate", str(destination)], check=True)
if shutil.which("update-desktop-database"):
    subprocess.run(["update-desktop-database", str(desktop)], check=True)
print(f"Desktop entries registered for {root}")

# A persistent user unit keeps the launch environment reproducible across builds.
config = Path(os.environ.get("XDG_CONFIG_HOME", str(Path.home() / ".config")))
if not config.is_absolute():
    config = Path.home() / ".config"
units = config / "systemd/user"
units.mkdir(parents=True, exist_ok=True)
def systemd_quote(value):
    return '"' + str(value).replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%").replace("$", "$$") + '"'
(units / "clypdat-experimental.service").write_text(
    "[Unit]\nDescription=ClypDat experimental KDE application\nAfter=graphical-session.target\n\n"
    "[Service]\nType=simple\nExecStart=" + systemd_quote(app) + "\nWorkingDirectory=" + str(root).replace("%", "%%") + "\n"
    "PassEnvironment=WAYLAND_DISPLAY XDG_RUNTIME_DIR DBUS_SESSION_BUS_ADDRESS\nUnsetEnvironment=DISPLAY\n"
    "Restart=on-failure\nRestartSec=3\nTimeoutStopSec=90\n")
print("User service registered. Run systemctl --user daemon-reload, then systemctl --user start clypdat-experimental.service")
