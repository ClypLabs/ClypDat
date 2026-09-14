#include "replay_buffer/replay_buffer_ram.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <time.h>
double clock_get_monotonic_seconds(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec + t.tv_nsec / 1e9; }
static void destroy(gsr_replay_buffer *b) { b->destroy(b); free(b); }
static unsigned long long disk_writes(void) {
    FILE *f = fopen("/proc/self/io", "r"); assert(f); char line[128]; unsigned long long value = 0;
    while (fgets(line, sizeof(line), f)) if (sscanf(line, "write_bytes: %llu", &value) == 1) break;
    fclose(f); return value;
}
int main(void) {
    gsr_replay_buffer *ring = gsr_replay_buffer_ram_create(1024); assert(ring);
    gsr_replay_buffer *snapshots[2] = {NULL, NULL};
    int64_t starts[2]; unsigned char values[2];
    unsigned char data[32768];
    unsigned long long before = disk_writes();
    for (int i = 0; i < 20000; i++) {
        memset(data, i % 251, sizeof(data));
        AVPacket packet = { .data = data, .size = sizeof(data), .pts = i, .dts = i, .duration = 1, .flags = i % 60 == 0 ? AV_PKT_FLAG_KEY : 0 };
        assert(ring->append(ring, &packet, clock_get_monotonic_seconds()));
        if (i == 2000 || i == 4000) {
            int n = i == 2000 ? 0 : 1; snapshots[n] = ring->clone(ring); assert(snapshots[n]);
            AVPacket *p = snapshots[n]->iterator_get_packet(snapshots[n], (gsr_replay_buffer_iterator){0});
            starts[n] = p->pts; values[n] = p->data[0];
        }
    }
    for (int n = 0; n < 2; n++) {
        gsr_replay_buffer_iterator it = {0}; int count = 0;
        do { AVPacket *p = snapshots[n]->iterator_get_packet(snapshots[n], it); assert(p->pts == starts[n] + count); assert(p->data[0] == p->pts % 251); count++; } while (snapshots[n]->iterator_next(snapshots[n], &it));
        assert(count == 1024); assert(values[n] == starts[n] % 251); destroy(snapshots[n]);
    }
    ring->clear(ring); destroy(ring);
    assert(disk_writes() == before);
    struct rusage usage; getrusage(RUSAGE_SELF, &usage); assert(usage.ru_maxrss < 180 * 1024);
    printf("PASS: two immutable overlapping RAM snapshots, ring wrap, 20,000 packets; peak RSS %ld KiB; media write bytes 0\n", usage.ru_maxrss);
}
