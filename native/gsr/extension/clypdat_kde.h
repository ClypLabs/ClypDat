/* SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef CLYPDAT_KDE_H
#define CLYPDAT_KDE_H
#include <stdint.h>
#include <stdbool.h>
void *clypdat_kde_open(const char *source, bool cursor);
uint64_t clypdat_kde_serial(void *context);
/* -1 source lost, 0 foreground/output, 1 background/minimized */
int clypdat_kde_poll(void *context);
void clypdat_kde_close(void *context);
int clypdat_list_outputs(void);
#endif
