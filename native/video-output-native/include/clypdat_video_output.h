#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#define CDVO_ABI 1u
#define CDVO_API __declspec(dllexport)
typedef struct cdvo_rect {
  float x, y, width, height;
} cdvo_rect;
typedef struct cdvo_blur {
  cdvo_rect bounds;
  double start, end;
  float sigma;
  uint32_t shape; /* 0 rectangle, 1 rounded, 2 ellipse */
} cdvo_blur;
typedef struct cdvo_artwork {
  uint64_t id;
  cdvo_rect bounds;
  double start, end;
  uint32_t layer; /* 0 underneath blur, 1 text, 2 guides */
  uint32_t reserved;
} cdvo_artwork;
typedef struct cdvo_state {
  uint32_t size, abi;
  uint64_t generation, revision;
  double media_seconds, rate;
  int64_t clock_us;
  uint32_t blur_count, artwork_count;
  const cdvo_blur *blurs;
  const cdvo_artwork *artwork;
} cdvo_state;
typedef struct cdvo_status {
  uint32_t size, abi;
  uint64_t generation, revision, decoded_picture, presented_picture, redraws;
  uint32_t width, height, attached, failed;
  char error[256];
} cdvo_status;
CDVO_API uint64_t cdvo_create(uint32_t abi);
CDVO_API int cdvo_bind_player(uint64_t token, void *player);
CDVO_API int cdvo_submit(uint64_t token, const cdvo_state *state);
CDVO_API int cdvo_update_artwork(uint64_t token, uint64_t generation,
                                 uint64_t id, uint32_t width, uint32_t height,
                                 uint32_t stride, const void *bgra);
CDVO_API int cdvo_request_redraw(uint64_t token);
// Lets a seek generation present the picture already retained when the player
// will not decode a new one (it is parked on the requested position).
CDVO_API int cdvo_adopt_retained(uint64_t token, uint64_t generation);
CDVO_API int cdvo_query(uint64_t token, cdvo_status *status);
CDVO_API void cdvo_release(uint64_t token);
#ifdef __cplusplus
}
#endif
