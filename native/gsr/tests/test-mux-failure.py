#!/usr/bin/env python3
from pathlib import Path
import shlex
import subprocess
import tempfile
root = Path(__file__).resolve().parents[1]
source = root / 'build/source'
with tempfile.TemporaryDirectory(prefix='clypdat-mux-fixture-') as temp:
    executable = str(Path(temp) / 'mux-test')
    flags = shlex.split(subprocess.check_output(['pkg-config', '--cflags', '--libs', 'libavcodec', 'libavutil', 'libavformat', 'libdrm', 'libpipewire-0.3', 'dbus-1'], text=True))
    subprocess.run(['cc', '-O2', '-ffunction-sections', '-fdata-sections', '-Wl,--gc-sections', '-I', str(source / 'include'), str(root / 'tests/mux-failure.c'), str(source / 'src/recorder/muxer.c'), str(source / 'src/ffmpeg_utils.c'), str(source / 'src/log.c'), *flags, '-o', executable], check=True)
    subprocess.run([executable], check=True, timeout=10)
