#!/usr/bin/env python3
from pathlib import Path
import subprocess
import tempfile
root = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='clypdat-parent-fixture-') as temp:
    work = Path(temp)
    (work / 'probe.c').write_text(r'''#include "clypdat_parent.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <sys/wait.h>
static pid_t child;
static char *executable;
static void *launcher(void *unused) {
    (void)unused;
    child = fork(); assert(child >= 0);
    if(!child) { execl(executable, executable, "child", NULL); _exit(3); }
    usleep(300000);
    return NULL;
}
int main(int argc, char **argv) {
    if(argc == 2) { assert(clypdat_watch_parent() == 0); puts("child ready"); fflush(stdout); for(;;) pause(); }
    executable = argv[0]; pthread_t thread; assert(pthread_create(&thread, NULL, launcher, NULL) == 0);
    pthread_join(thread, NULL); usleep(200000);
    int status; assert(waitpid(child, &status, WNOHANG) == 0);
    puts("survived launching thread"); fflush(stdout);
    return 0;
}
''')
    executable = str(work / 'probe')
    subprocess.run(['cc', '-Wall', '-Wextra', '-Werror', '-I', str(root / 'extension'), str(work / 'probe.c'), '-pthread', '-o', executable], check=True)
    # Child inherits stdout. EOF proves it exits after its parent process exits.
    result = subprocess.run([executable], capture_output=True, text=True, timeout=5, check=True)
    assert 'child ready' in result.stdout and 'survived launching thread' in result.stdout, result
print('PASS: thread retirement preserves recorder; owning process exit terminates it')
