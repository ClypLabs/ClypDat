#include "clypdat_video_output.h"
#include <vlc_common.h>

/* VLC 3.0 creates its output beneath the media player, bypassing input/media
 * variables. Mirror the registered media option onto that inheritance chain.
 * libvlc_media_player_t starts with VLC_COMMON_MEMBERS in the pinned revision.
 */
int cdvo_bind_player(uint64_t token, void *player) {
  cdvo_status status = {sizeof(status), CDVO_ABI};
  if (!player || !cdvo_query(token, &status))
    return 0;
  vlc_object_t *object = (vlc_object_t *)player;
  if (var_Create(object, "clypdat-context", VLC_VAR_INTEGER) != VLC_SUCCESS)
    return 0;
  if (var_SetInteger(object, "clypdat-context", token) != VLC_SUCCESS)
    return 0;
  return var_SetString(object, "vout", "clypdat_d3d11,none") == VLC_SUCCESS;
}
