#!/usr/bin/env python3
"""Private compositor protocol fixtures; no connection to the user's desktop."""
import os
from pathlib import Path
import shlex
import subprocess
import tempfile
root = Path(__file__).resolve().parents[1]
source = root / 'build/source'
with tempfile.TemporaryDirectory(prefix='clypdat-kde-fixture-') as temp:
    work = Path(temp)
    generated = []
    for protocol in ['plasma-window-management', 'zkde-screencast-unstable-v1']:
        xml = root / 'protocols' / (protocol + '.xml')
        for mode, suffix in [('server-header', '-server-protocol.h'), ('client-header', '-client-protocol.h'), ('private-code', '.c')]:
            subprocess.run(['wayland-scanner', mode, str(xml), str(work / (protocol + suffix))], check=True)
        generated.append(str(work / (protocol + '.c')))
    flags = shlex.split(subprocess.check_output(['pkg-config', '--cflags', '--libs', 'wayland-server', 'wayland-client'], text=True))
    subprocess.run(['cc', '-Wall', '-Wextra', '-Werror', '-I', temp, str(root / 'tests/kde-target-compositor.c'), *generated, *flags, '-o', str(work / 'server')], check=True)
    (work / 'probe.c').write_text('''#include "clypdat_kde.h"
#include <stdio.h>
#include <inttypes.h>
#include <unistd.h>
int main(int argc, char **argv) {
    if (argc != 2) return 3;
    if (!strcmp(argv[1], "outputs")) return clypdat_list_outputs();
    void *s = clypdat_kde_open(argv[1], false); if (!s) return 2;
    printf("serial=%" PRIu64 "\\n", clypdat_kde_serial(s));
    int previous = -2;
    for (int n = 0; n < 200; n++) { int state = clypdat_kde_poll(s);
        if (state != previous) { printf("state=%d\\n", state); previous = state; }
        if (state < 0) break; usleep(5000);
    }
    clypdat_kde_close(s); return 0;
}'''.replace('#include <unistd.h>', '#include <unistd.h>\n#include <string.h>'))
    subprocess.run(['cc', '-I', temp, '-I', str(source / 'src'), str(work / 'probe.c'), str(source / 'src/clypdat_kde.c'), *generated, *flags, '-o', str(work / 'probe')], check=True)
    targets = [('normal', 'kde-window:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'), ('normal', 'kde-output:DP-fixture'), ('normal', 'kde-output:missing'), ('missing', 'kde-output:DP-fixture'), ('normal', 'outputs')]
    for mode, target in targets:
        env = dict(os.environ, XDG_RUNTIME_DIR=temp)
        env.pop('DISPLAY', None); env.pop('WAYLAND_SOCKET', None)
        server = subprocess.Popen([str(work / 'server'), mode], env=env, stdout=subprocess.PIPE, text=True)
        try:
            env['WAYLAND_DISPLAY'] = server.stdout.readline().strip()
            result = subprocess.run([str(work / 'probe'), target], env=env, capture_output=True, text=True, timeout=5)
            if mode == 'missing' or target.endswith(':missing'):
                assert result.returncode == 2, result
            elif target == 'outputs':
                import json
                output = json.loads(result.stdout)
                assert output['name'] == 'DP-fixture' and output['width'] == 72 and output['height'] == 128 and output['scale'] == 2, output
            else:
                assert result.returncode == 0 and 'serial=30064771172' in result.stdout, result
                assert 'state=-1' in result.stdout, result
                if target.startswith('kde-window:'): assert 'state=1' in result.stdout and result.stdout.count('state=0') == 2, result
        finally:
            server.terminate(); server.wait(timeout=5)
print('PASS: v6 object serial, exact window/output targeting, focus/minimize, rotation/scale, missing source/protocol, source loss')
