#ifndef CLYPDAT_PARENT_H
#define CLYPDAT_PARENT_H
#include <errno.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <stdint.h>
#include <sys/syscall.h>
#include <unistd.h>
/* PR_SET_PDEATHSIG follows the creating thread, which .NET may retire while
   the owning process stays alive. A pidfd follows the process itself. */
static void *clypdat_parent_wait(void *data) {
    int fd = (int)(intptr_t)data;
    struct pollfd parent = {.fd = fd, .events = POLLIN};
    int result;
    do { result = poll(&parent, 1, -1); } while(result < 0 && errno == EINTR);
    close(fd);
    kill(getpid(), SIGTERM);
    return NULL;
}
static int clypdat_watch_parent(void) {
    pid_t parent = getppid();
    int fd = (int)syscall(SYS_pidfd_open, parent, 0);
    if(fd < 0) return -1;
    if(getppid() != parent) { close(fd); return -1; }
    pthread_t thread;
    if(pthread_create(&thread, NULL, clypdat_parent_wait, (void*)(intptr_t)fd) != 0) { close(fd); return -1; }
    pthread_detach(thread);
    return 0;
}
#endif
