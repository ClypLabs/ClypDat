#pragma once
#include <stdint.h>
#include <stddef.h>
#include <malloc.h>
#define N_(str) str
#define _(str) str
#define ssize_t intptr_t
#define _USE_MATH_DEFINES
#define strcasecmp _stricmp
#define strncasecmp _strnicmp


#include <winsock2.h>
static inline int poll(struct pollfd *fds, unsigned n, int timeout) { return WSAPoll(fds, n, timeout); }
#include <math.h>
static inline void sincosf(float x, float *s, float *c) { *s = sinf(x); *c = cosf(x); }
#ifdef __cplusplus
extern "C" { extern const char vlc_module_name[]; }
#endif
#ifndef __cplusplus
#include <stdlib.h>
#include <string.h>
void *cdvo_vlc_malloc(size_t);
void *cdvo_vlc_calloc(size_t, size_t);
void *cdvo_vlc_realloc(void *, size_t);
void cdvo_vlc_free(void *);
char *cdvo_vlc_strdup(const char *);
#define malloc cdvo_vlc_malloc
#define calloc cdvo_vlc_calloc
#define realloc cdvo_vlc_realloc
#define free cdvo_vlc_free
#define strdup cdvo_vlc_strdup
#endif
