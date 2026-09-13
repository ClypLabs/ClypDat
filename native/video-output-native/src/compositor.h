#pragma once
#include "clypdat_video_output.h"
#include <d3d11.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
void *cdvo_attach(uint64_t token, ID3D11Device *device,
                  ID3D11DeviceContext *immediate);
void cdvo_detach(void *renderer);
ID3D11RenderTargetView *cdvo_begin_picture(void *renderer, uint32_t width,
                                           uint32_t height, int64_t date);
int cdvo_compose(void *renderer, ID3D11RenderTargetView *output,
                 const D3D11_VIEWPORT *viewport, int redraw);
void cdvo_presented(void *renderer);
void cdvo_fail(void *renderer, const char *message);
int cdvo_needs_redraw(void *renderer);
int cdvo_has_retained_picture(void *renderer);
#ifdef __cplusplus
}
#endif
