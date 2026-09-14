/* SPDX-License-Identifier: GPL-3.0-or-later */
#include "clypdat_watcher.c"
#include "clypdat_kde.h"
#include "zkde-screencast-unstable-v1-client-protocol.h"
#include <poll.h>
#include <time.h>

struct kde_output {
    struct kde_output *next;
    struct wl_output *proxy;
    uint32_t global;
    char *name;
    int x, y, width, height, scale, rotation;
};
struct kde_capture {
    struct watcher watcher; /* must be first, reused metadata callbacks */
    struct wl_display *display;
    struct wl_registry *registry;
    struct zkde_screencast_unstable_v1 *manager;
    struct zkde_screencast_stream_unstable_v1 *stream;
    struct kde_output *outputs;
    uint32_t manager_global, output_global;
    char *uuid;
    uint64_t serial;
};
static void output_geometry(void *data, struct wl_output *proxy, int32_t x, int32_t y,
    int32_t pw, int32_t ph, int32_t subpixel, const char *make, const char *model, int32_t transform) {
    (void)proxy; (void)pw; (void)ph; (void)subpixel; (void)make; (void)model;
    struct kde_output *o = data; o->x = x; o->y = y; o->rotation = transform;
}
static void output_mode(void *data, struct wl_output *proxy, uint32_t flags, int32_t width, int32_t height, int32_t refresh) {
    (void)proxy; (void)refresh;
    if (flags & WL_OUTPUT_MODE_CURRENT) { struct kde_output *o = data; o->width = width; o->height = height; }
}
static void output_done(void *data, struct wl_output *proxy) { (void)data; (void)proxy; }
static void output_scale(void *data, struct wl_output *proxy, int32_t scale) { (void)proxy; ((struct kde_output*)data)->scale = scale; }
static void output_name(void *data, struct wl_output *proxy, const char *name) {
    (void)proxy; struct kde_output *o = data; free(o->name); o->name = strdup(name);
}
static void output_description(void *data, struct wl_output *proxy, const char *description) { (void)data; (void)proxy; (void)description; }
static const struct wl_output_listener output_listener = {
    .geometry = output_geometry, .mode = output_mode, .done = output_done,
    .scale = output_scale, .name = output_name, .description = output_description
};
static void kde_global(void *data, struct wl_registry *registry, uint32_t name, const char *interface, uint32_t version) {
    struct kde_capture *s = data;
    global(&s->watcher, registry, name, interface, version);
    if (!strcmp(interface, "zkde_screencast_unstable_v1") && version >= 6 && !s->manager) {
        s->manager_global = name;
        s->manager = wl_registry_bind(registry, name, &zkde_screencast_unstable_v1_interface, 6);
    } else if (!strcmp(interface, "wl_output") && version >= 4) {
        struct kde_output *o = calloc(1, sizeof(*o));
        if (!o) { s->watcher.failed = true; return; }
        o->global = name; o->scale = 1;
        o->proxy = wl_registry_bind(registry, name, &wl_output_interface, 4);
        o->next = s->outputs; s->outputs = o;
        wl_output_add_listener(o->proxy, &output_listener, o);
    }
}
static void kde_remove(void *data, struct wl_registry *registry, uint32_t name) {
    struct kde_capture *s = data;
    global_remove(&s->watcher, registry, name);
    if (name == s->manager_global || name == s->output_global) s->watcher.failed = true;
    struct kde_output **link = &s->outputs;
    while (*link) {
        struct kde_output *o = *link;
        if (o->global == name) { *link = o->next; wl_output_release(o->proxy); free(o->name); free(o); break; }
        link = &o->next;
    }
}
static const struct wl_registry_listener kde_listener = { kde_global, kde_remove };
static void stream_closed(void *data, struct zkde_screencast_stream_unstable_v1 *stream) {
    (void)stream; ((struct kde_capture*)data)->watcher.failed = true;
}
static void stream_created(void *data, struct zkde_screencast_stream_unstable_v1 *stream, uint32_t node) {
    (void)data; (void)stream; (void)node; /* never target a reusable node ID */
}
static void stream_failed(void *data, struct zkde_screencast_stream_unstable_v1 *stream, const char *error) {
    fprintf(stderr, "ClypDat KDE source failed: %s\n", error); stream_closed(data, stream);
}
static void stream_serial(void *data, struct zkde_screencast_stream_unstable_v1 *stream, uint32_t hi, uint32_t lo) {
    (void)stream; ((struct kde_capture*)data)->serial = ((uint64_t)hi << 32) | lo;
}
static const struct zkde_screencast_stream_unstable_v1_listener stream_listener = {
    .closed = stream_closed, .created = stream_created, .failed = stream_failed, .serial = stream_serial
};
static bool dispatch(struct kde_capture *s, int timeout) {
    if (wl_display_dispatch_pending(s->display) < 0) return false;
    while (wl_display_prepare_read(s->display) != 0)
        if (wl_display_dispatch_pending(s->display) < 0) return false;
    wl_display_flush(s->display);
    struct pollfd fd = { .fd = wl_display_get_fd(s->display), .events = POLLIN };
    int result = poll(&fd, 1, timeout);
    if (result > 0 && (fd.revents & POLLIN)) {
        if (wl_display_read_events(s->display) < 0) return false;
        return wl_display_dispatch_pending(s->display) >= 0;
    }
    wl_display_cancel_read(s->display);
    return result >= 0 && !(fd.revents & (POLLERR | POLLHUP | POLLNVAL));
}
static struct kde_capture *connect_kde(void) {
    struct kde_capture *s = calloc(1, sizeof(*s));
    if (!s) return NULL;
    watcher_silent = true;
    s->display = wl_display_connect(NULL);
    if (!s->display) { free(s); return NULL; }
    s->registry = wl_display_get_registry(s->display);
    wl_registry_add_listener(s->registry, &kde_listener, s);
    if (wl_display_roundtrip(s->display) < 0 || wl_display_roundtrip(s->display) < 0 || wl_display_roundtrip(s->display) < 0) {
        clypdat_kde_close(s); return NULL;
    }
    return s;
}
void *clypdat_kde_open(const char *source, bool cursor) {
    struct kde_capture *s = connect_kde();
    if (!s) return NULL;
    if (!s->manager) goto failed;
    uint32_t pointer = cursor ? 2 : 1; /* embedded cursor frozen with owned frame */
    if (!strncmp(source, "kde-window:", 11)) {
        if (!s->watcher.manager) goto failed;
        s->uuid = strdup(source + 11);
        if (!s->uuid || clypdat_kde_poll(s) < 0) goto failed;
        s->stream = zkde_screencast_unstable_v1_stream_window(s->manager, s->uuid, pointer);
    } else if (!strncmp(source, "kde-output:", 11)) {
        for (struct kde_output *o = s->outputs; o; o = o->next) {
            if (o->name && !strcmp(o->name, source + 11)) {
                s->output_global = o->global;
                s->stream = zkde_screencast_unstable_v1_stream_output(s->manager, o->proxy, pointer); break;
            }
        }
    }
    if (!s->stream) goto failed;
    zkde_screencast_stream_unstable_v1_add_listener(s->stream, &stream_listener, s);
    for (int i = 0; i < 100 && !s->serial && !s->watcher.failed; ++i)
        if (!dispatch(s, 50)) goto failed;
    if (!s->serial || s->watcher.failed) goto failed;
    return s;
failed:
    fprintf(stderr, "ClypDat KDE source unavailable: %s (requires screencast v6 and registered private recorder).\n", source);
    clypdat_kde_close(s); return NULL;
}
uint64_t clypdat_kde_serial(void *context) { return ((struct kde_capture*)context)->serial; }
int clypdat_kde_poll(void *context) {
    struct kde_capture *s = context;
    if (!dispatch(s, 0) || s->watcher.failed) return -1;
    if (!s->uuid) return 0;
    for (struct window *w = s->watcher.windows; w; w = w->next)
        if (!strcmp(w->uuid, s->uuid)) return (w->flags & 1) && !(w->flags & 2) ? 0 : 1;
    return -1;
}
void clypdat_kde_close(void *context) {
    struct kde_capture *s = context;
    if (!s) return;
    if (s->stream) zkde_screencast_stream_unstable_v1_close(s->stream);
    if (s->manager) zkde_screencast_unstable_v1_destroy(s->manager);
    while (s->watcher.windows) release_window(s->watcher.windows);
    if (s->watcher.manager) org_kde_plasma_window_management_destroy(s->watcher.manager);
    while (s->outputs) { struct kde_output *o = s->outputs; s->outputs = o->next; wl_output_release(o->proxy); free(o->name); free(o); }
    if (s->registry) wl_registry_destroy(s->registry);
    if (s->display) wl_display_disconnect(s->display);
    free(s->uuid); free(s);
}
int clypdat_list_outputs(void) {
    struct kde_capture *s = connect_kde();
    if (!s) return 2;
    for (struct kde_output *o = s->outputs; o; o = o->next) {
        if (!o->name || o->width <= 0 || o->height <= 0) continue;
        int width = o->width, height = o->height;
        if (o->rotation == 1 || o->rotation == 3 || o->rotation == 5 || o->rotation == 7) { width = o->height; height = o->width; }
        printf("{\"name\":"); json_string(o->name);
        printf(",\"x\":%d,\"y\":%d,\"width\":%d,\"height\":%d,\"scale\":%d}\n", o->x, o->y, width, height, o->scale);
    }
    clypdat_kde_close(s); return 0;
}
