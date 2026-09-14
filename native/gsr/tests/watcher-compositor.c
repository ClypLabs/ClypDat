/* SPDX-License-Identifier: GPL-3.0-or-later */
#include <wayland-server.h>
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "plasma-window-management-server-protocol.h"

static struct wl_display *display;
static const char *uuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
static void destroy_window(struct wl_client *client, struct wl_resource *resource) {
    (void)client;
    wl_resource_destroy(resource);
    wl_display_terminate(display);
}
static const struct org_kde_plasma_window_interface window_impl = { .destroy = destroy_window };
static void get_window(struct wl_client *client, struct wl_resource *manager, uint32_t id, const char *requested_uuid) {
    assert(strcmp(requested_uuid, uuid) == 0);
    struct wl_resource *window = wl_resource_create(client, &org_kde_plasma_window_interface,
        wl_resource_get_version(manager), id);
    wl_resource_set_implementation(window, &window_impl, NULL, NULL);
    org_kde_plasma_window_send_title_changed(window, "Test \"game\"\nline");
    org_kde_plasma_window_send_app_id_changed(window, "steam_app_123");
    org_kde_plasma_window_send_pid_changed(window, 1234);
    org_kde_plasma_window_send_state_changed(window, 1);
    org_kde_plasma_window_send_geometry(window, -1920, 0, 1920, 1080);
    org_kde_plasma_window_send_initial_state(window);
    org_kde_plasma_window_send_state_changed(window, 2);
    org_kde_plasma_window_send_geometry(window, 10, 20, 1280, 720);
    org_kde_plasma_window_send_unmapped(window);
}
static const struct org_kde_plasma_window_management_interface manager_impl = { .get_window_by_uuid = get_window };
static void bind_manager(struct wl_client *client, void *data, uint32_t version, uint32_t id) {
    (void)data;
    struct wl_resource *manager = wl_resource_create(client, &org_kde_plasma_window_management_interface, version, id);
    wl_resource_set_implementation(manager, &manager_impl, NULL, NULL);
    org_kde_plasma_window_management_send_window_with_uuid(manager, 99, uuid);
}
int main(int argc, char **argv) {
    display = wl_display_create();
    assert(display);
    if (argc == 1 || strcmp(argv[1], "missing") != 0)
        assert(wl_global_create(display, &org_kde_plasma_window_management_interface, 17, NULL, bind_manager));
    const char *socket = wl_display_add_socket_auto(display);
    assert(socket);
    puts(socket); fflush(stdout);
    wl_display_run(display);
    wl_display_destroy_clients(display);
    wl_display_destroy(display);
    return 0;
}
