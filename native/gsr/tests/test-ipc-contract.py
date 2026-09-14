#!/usr/bin/env python3
"""Exercise the private recorder parser and deferred session acknowledgement."""
from pathlib import Path
import shlex
import subprocess
import tempfile
root = Path(__file__).resolve().parents[1]
source = root / 'build/source'
with tempfile.TemporaryDirectory(prefix='clypdat-ipc-fixture-') as temp:
    work = Path(temp)
    (work / 'probe.c').write_text(r'''#include "src/cli/ipc.c"
#include <assert.h>
int main(void) {
    char command[] = "{\"id\":42,\"name\":\"save-replay\",\"data\":null}";
    char error[256] = {0}; gsr_ipc_request request = {0};
    assert(ipc_request_parse(command, strlen(command), &request, error, sizeof(error)));
    int seconds; bool has_restart, restart;
    assert(ipc_request_get_save_replay_options(&request, &seconds, &has_restart, &restart, error, sizeof(error)));
    assert(seconds == GSR_SAVE_REPLAY_SECONDS_FULL && !has_restart);
    char invalid[] = "{\"id\":43,\"name\":\"save-replay\",\"data\":{\"seconds\":-1}}";
    memset(&request, 0, sizeof(request));
    assert(ipc_request_parse(invalid, strlen(invalid), &request, error, sizeof(error)));
    assert(!ipc_request_get_save_replay_options(&request, &seconds, &has_restart, &restart, error, sizeof(error)));
    gsr_ipc_deferred_request_type type;
    assert(ipc_request_name_to_deferred_request_type("start-replay-recording", &type));
    assert(type == GSR_IPC_DEFERRED_REQUEST_START_REPLAY_RECORDING);
    gsr_ipc ipc = {.initialized = true}; assert(pipe(ipc.wakeup_pipe) == 0); pthread_mutex_init(&ipc.deferred_requests_mutex, NULL);
    assert(ipc_set_deferred_request_pending(&ipc, type, -1, 44));
    gsr_ipc_complete_request(&ipc, type, true, "/staging/session.mkv");
    assert(ipc.deferred_requests[type].state == GSR_IPC_DEFERRED_REQUEST_STATE_COMPLETED);
    assert(ipc.deferred_requests[type].request_id == 44 && ipc.deferred_requests[type].success);
    assert(!strcmp(ipc.deferred_requests[type].filepath, "/staging/session.mkv"));
    pthread_mutex_destroy(&ipc.deferred_requests_mutex); close(ipc.wakeup_pipe[0]); close(ipc.wakeup_pipe[1]);
    puts("PASS: full-buffer command, invalid interval rejection, correlated session-start path");
}
''')
    flags = shlex.split(subprocess.check_output(['pkg-config', '--cflags', '--libs', 'libavcodec', 'libavutil', 'libavformat', 'libdrm', 'libpipewire-0.3', 'dbus-1'], text=True))
    executable = str(work / 'probe')
    subprocess.run(['cc', '-O2', '-ffunction-sections', '-fdata-sections', '-Wl,--gc-sections', '-I', str(source), str(work / 'probe.c'), str(source / 'src/json.c'), str(source / 'src/log.c'), *flags, '-pthread', '-o', executable], check=True)
    subprocess.run([executable], check=True, timeout=10)
