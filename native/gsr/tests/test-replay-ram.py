#!/usr/bin/env python3
"""Exercise the pinned RAM implementation without a desktop or capture device."""
from pathlib import Path
import shlex
import subprocess
import tempfile
root = Path(__file__).resolve().parents[1]
source = root / 'build/source'
with tempfile.TemporaryDirectory(prefix='clypdat-ram-fixture-') as temp:
    executable = str(Path(temp) / 'replay-test')
    flags = shlex.split(subprocess.check_output(['pkg-config', '--cflags', '--libs', 'libavcodec', 'libavutil'], text=True))
    subprocess.run(['cc', '-O2', '-Wall', '-Wextra', '-I', str(source / 'include'), str(root / 'tests/replay-ram.c'), str(source / 'src/replay_buffer/replay_buffer_ram.c'), *flags, '-o', executable], check=True)
    subprocess.run([executable], check=True, timeout=30)
