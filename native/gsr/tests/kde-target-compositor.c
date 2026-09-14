/* SPDX-License-Identifier: GPL-3.0-or-later */
#include <wayland-server.h>
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "plasma-window-management-server-protocol.h"
#include "zkde-screencast-unstable-v1-server-protocol.h"
static struct wl_display *display;
static struct wl_resource *window, *stream;
static struct wl_global *output_global;
static int phase;
static struct wl_event_source *timer;
static const char *uuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
static void destroy(struct wl_client *client, struct wl_resource *resource) { (void)client; wl_resource_destroy(resource); }
static const struct org_kde_plasma_window_interface window_impl = { .destroy = destroy };
static void get_window(struct wl_client *client, struct wl_resource *manager, uint32_t id, const char *requested) {
    assert(!strcmp(uuid, requested));
    window = wl_resource_create(client, &org_kde_plasma_window_interface, wl_resource_get_version(manager), id);
    wl_resource_set_implementation(window, &window_impl, NULL, NULL);
    org_kde_plasma_window_send_title_changed(window, "Fixture");
    org_kde_plasma_window_send_app_id_changed(window, "fixture");
    org_kde_plasma_window_send_pid_changed(window, 1234);
    org_kde_plasma_window_send_state_changed(window, 1);
    org_kde_plasma_window_send_geometry(window, 0, 0, 128, 72);
    org_kde_plasma_window_send_initial_state(window);
}
static const struct org_kde_plasma_window_management_interface windows_impl = { .get_window_by_uuid = get_window };
static void bind_windows(struct wl_client *client, void *data, uint32_t version, uint32_t id) {
    (void)data; struct wl_resource *r = wl_resource_create(client, &org_kde_plasma_window_management_interface, version, id);
    wl_resource_set_implementation(r, &windows_impl, NULL, NULL);
    org_kde_plasma_window_management_send_window_with_uuid(r, 1, uuid);
}
static const struct wl_output_interface output_impl = { .release = destroy };
static void bind_output(struct wl_client *client, void *data, uint32_t version, uint32_t id) {
    (void)data; struct wl_resource *r = wl_resource_create(client, &wl_output_interface, version, id);
    wl_resource_set_implementation(r, &output_impl, NULL, NULL);
    wl_output_send_geometry(r, -128, 0, 100, 100, 0, "Fixture", "Fixture", WL_OUTPUT_TRANSFORM_90);
    wl_output_send_mode(r, WL_OUTPUT_MODE_CURRENT, 128, 72, 60000);
    wl_output_send_scale(r, 2); wl_output_send_name(r, "DP-fixture"); wl_output_send_done(r);
}
static int tick(void *data) {
    (void)data;
    if (++phase == 1) org_kde_plasma_window_send_state_changed(window, 2);
    else if (phase == 2) org_kde_plasma_window_send_state_changed(window, 1);
    else { wl_global_destroy(output_global); output_global = NULL; zkde_screencast_stream_unstable_v1_send_closed(stream); return 0; }
    wl_event_source_timer_update(timer, 80); return 0;
}
static const struct zkde_screencast_stream_unstable_v1_interface stream_impl = { .close = destroy };
static void create_stream(struct wl_client *client, struct wl_resource *manager, uint32_t id) {
    assert(wl_resource_get_version(manager) == 6);
    stream = wl_resource_create(client, &zkde_screencast_stream_unstable_v1_interface, 6, id);
    wl_resource_set_implementation(stream, &stream_impl, NULL, NULL);
    zkde_screencast_stream_unstable_v1_send_serial(stream, 7, 100);
    zkde_screencast_stream_unstable_v1_send_created(stream, 99);
    wl_event_source_timer_update(timer, 80);
}
static void stream_window(struct wl_client *client, struct wl_resource *manager, uint32_t id, const char *requested, uint32_t cursor) {
    assert(!strcmp(uuid, requested)); assert(cursor == 1); create_stream(client, manager, id);
}
static void stream_output(struct wl_client *client, struct wl_resource *manager, uint32_t id, struct wl_resource *output, uint32_t cursor) {
    assert(output); assert(cursor == 1); create_stream(client, manager, id);
}
static const struct zkde_screencast_unstable_v1_interface capture_impl = { .stream_window = stream_window, .stream_output = stream_output, .destroy = destroy };
static void bind_capture(struct wl_client *client, void *data, uint32_t version, uint32_t id) {
    (void)data; struct wl_resource *r = wl_resource_create(client, &zkde_screencast_unstable_v1_interface, version, id);
    wl_resource_set_implementation(r, &capture_impl, NULL, NULL);
}
int main(int argc, char **argv) {
    display = wl_display_create(); assert(display);
    assert(wl_global_create(display, &org_kde_plasma_window_management_interface, 17, NULL, bind_windows));
    output_global = wl_global_create(display, &wl_output_interface, 4, NULL, bind_output); assert(output_global);
    if (argc < 2 || strcmp(argv[1], "missing")) assert(wl_global_create(display, &zkde_screencast_unstable_v1_interface, 6, NULL, bind_capture));
    timer = wl_event_loop_add_timer(wl_display_get_event_loop(display), tick, NULL);
    const char *socket = wl_display_add_socket_auto(display); assert(socket); puts(socket); fflush(stdout);
    wl_display_run(display); return 0;
}
