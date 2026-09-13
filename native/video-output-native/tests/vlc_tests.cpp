#include <windows.h>

#include "clypdat_video_output.h"
#include <atomic>
#include <cstdio>
#include <cstring>
#include <dbghelp.h>
#include <filesystem>
#include <future>
#include <vlc/vlc.h>
static std::atomic<bool> hardware{false};
static void log(void *, int level, const libvlc_log_t *, const char *fmt,
                va_list args) {
  char message[2048];
  vsnprintf(message, sizeof(message), fmt, args);
  if (strstr(message, "Using D3D11VA") || strstr(message, "Using DXVA2"))
    hardware = true;
  if (level >= 4)
    fprintf(stderr, "%s\n", message);
}
static void pump(unsigned milliseconds) {
  auto start = GetTickCount64();
  do {
    MSG msg;
    while (PeekMessage(&msg, nullptr, 0, 0, PM_REMOVE)) {
      TranslateMessage(&msg);
      DispatchMessage(&msg);
    }
    Sleep(1);
  } while (GetTickCount64() - start < milliseconds);
}
template <class T> static void wait(std::future<T> &task) {
  while (task.wait_for(std::chrono::milliseconds(0)) !=
         std::future_status::ready)
    pump(1);
  task.get();
}
static LONG fault(EXCEPTION_POINTERS *e) {
  auto process = GetCurrentProcess();
  SymInitialize(process, nullptr, TRUE);
  fprintf(stderr, "FAULT %lx at %p\n", e->ExceptionRecord->ExceptionCode,
          e->ExceptionRecord->ExceptionAddress);
  STACKFRAME64 frame{};
  frame.AddrPC.Offset = e->ContextRecord->Rip;
  frame.AddrPC.Mode = AddrModeFlat;
  frame.AddrStack.Offset = e->ContextRecord->Rsp;
  frame.AddrStack.Mode = AddrModeFlat;
  frame.AddrFrame.Offset = e->ContextRecord->Rbp;
  frame.AddrFrame.Mode = AddrModeFlat;
  for (int n = 0; n < 24; ++n) {
    char buffer[sizeof(SYMBOL_INFO) + 512]{};
    auto symbol = reinterpret_cast<SYMBOL_INFO *>(buffer);
    symbol->SizeOfStruct = sizeof(SYMBOL_INFO);
    symbol->MaxNameLen = 511;
    DWORD64 displacement = 0;
    if (SymFromAddr(process, frame.AddrPC.Offset, &displacement, symbol))
      fprintf(stderr, "STACK %s + %llu\n", symbol->Name, displacement);
    else
      fprintf(stderr, "STACK %llx\n", frame.AddrPC.Offset);
    IMAGEHLP_LINE64 line{};
    line.SizeOfStruct = sizeof(line);
    DWORD offset = 0;
    if (SymGetLineFromAddr64(process, frame.AddrPC.Offset, &offset, &line))
      fprintf(stderr, "LINE %s:%lu\n", line.FileName, line.LineNumber);
    if (!StackWalk64(IMAGE_FILE_MACHINE_AMD64, process, GetCurrentThread(),
                     &frame, e->ContextRecord, nullptr,
                     SymFunctionTableAccess64, SymGetModuleBase64, nullptr))
      break;
  }
  fflush(stderr);
  return EXCEPTION_EXECUTE_HANDLER;
}
int main(int argc, char **argv) {
  SetUnhandledExceptionFilter(fault);
  if (argc < 2) {
    fprintf(stderr, "usage: vlc_tests clip [baseline|transport|software] "
                    "[measurement-seconds]\n");
    return 2;
  }
  bool baseline = argc > 2 && !strcmp(argv[2], "baseline");
  bool transport = argc > 2 && !strcmp(argv[2], "transport");
  bool software = argc > 2 && !strcmp(argv[2], "software");
  unsigned seconds = argc > 3 ? unsigned(atoi(argv[3])) : 3;
  HWND hwnd = CreateWindowExW(0, L"STATIC", L"ClypDat compositor test",
                              WS_OVERLAPPEDWINDOW, 0, 0, 640, 360, nullptr,
                              nullptr, GetModuleHandleW(nullptr), nullptr);
  const char *options[] = {"--no-audio",
                           "--no-osd",
                           "--no-plugins-cache",
                           "--stats",
                           "--no-drop-late-frames",
                           "--no-skip-frames",
                           "--file-caching=50"};
  auto vlc = libvlc_new(sizeof(options) / sizeof(options[0]), options);
  if (!vlc)
    return 3;
  libvlc_log_set(vlc, log, nullptr);
  auto token = baseline ? 0 : cdvo_create(CDVO_ABI);
  cdvo_blur blur{{.25f, .25f, .5f, .5f}, 0, 10000, 20, 0};
  cdvo_state state{sizeof(cdvo_state), CDVO_ABI, 1, 1,     0,      1,
                   libvlc_clock(),     1,        0, &blur, nullptr};
  if (token)
    cdvo_submit(token, &state);
  auto media = libvlc_media_new_path(vlc, argv[1]);
  libvlc_media_add_option(media,
                          software ? ":avcodec-hw=none" : ":avcodec-hw=any");
  if (token) {
    char option[96];
    snprintf(option, sizeof(option), ":clypdat-context=%llu", token);
    libvlc_media_add_option(media, option);
  }
  auto player = libvlc_media_player_new_from_media(media);
  libvlc_media_player_set_hwnd(player, hwnd);
  if (token)
    cdvo_bind_player(token, player);
  auto playing = std::async(std::launch::async,
                            [&] { return libvlc_media_player_play(player); });
  wait(playing);
  pump(2000);
  cdvo_status status{sizeof(cdvo_status), CDVO_ABI};
  if (token) {
    cdvo_query(token, &status);
    blur.sigma = 20.f * status.height / 1080;
    state.revision++;
    cdvo_submit(token, &state);
  }
  libvlc_media_stats_t before{}, after{};
  libvlc_media_get_stats(media, &before);
  pump(seconds * 1000);
  libvlc_media_get_stats(media, &after);
  if (token)
    cdvo_query(token, &status);
  bool ok = after.i_displayed_pictures > before.i_displayed_pictures &&
            (baseline ||
             (status.attached && status.presented_picture && !status.failed));
  if (transport && ok) {
    libvlc_media_player_set_pause(player, 1);
    pump(350);
    cdvo_query(token, &status);
    auto picture = status.decoded_picture, redraws = status.redraws;
    auto time = libvlc_media_player_get_time(player);
    blur.shape = 2;
    state.revision++;
    state.rate = 0;
    state.media_seconds = time / 1000.;
    state.clock_us = libvlc_clock();
    ok &= cdvo_submit(token, &state) != 0;
    pump(350);
    cdvo_query(token, &status);
    bool paused = status.redraws > redraws &&
                  status.decoded_picture == picture &&
                  libvlc_media_player_get_time(player) == time;
    printf("paused_edit=%s picture=%llu redraws=%llu\n",
           paused ? "PASS" : "FAIL", status.decoded_picture, status.redraws);
    ok &= paused;
    libvlc_media_player_set_rate(player, 2);
    state.rate = 2;
    state.clock_us = libvlc_clock();
    state.revision++;
    cdvo_submit(token, &state);
    libvlc_media_player_set_pause(player, 0);
    pump(400);
    for (auto target : {999LL, 1001LL, 1999LL, 2001LL}) {
      state.generation++;
      state.revision = 0;
      state.artwork_count = state.blur_count = 0;
      cdvo_submit(token, &state);
      libvlc_media_player_set_time(player, target);
      state.revision = 1;
      state.blur_count = 1;
      state.media_seconds = target / 1000.;
      state.clock_us = libvlc_clock();
      cdvo_submit(token, &state);
      pump(300);
      cdvo_query(token, &status);
      ok &= !status.failed && status.generation == state.generation;
    }
    printf("seek_boundaries=%s\n", ok ? "PASS" : "FAIL");
  }
  auto stopping =
      std::async(std::launch::async, [&] { libvlc_media_player_stop(player); });
  wait(stopping);
  libvlc_media_player_release(player);
  libvlc_media_release(media);
  libvlc_release(vlc);
  DestroyWindow(hwnd);
  if (token) {
    cdvo_query(token, &status);
    ok &= status.attached == 0;
    cdvo_release(token);
  }
  auto path = std::filesystem::u8path(argv[1]);
  auto handle = CreateFileW(path.c_str(), GENERIC_READ, 0, nullptr,
                            OPEN_EXISTING, 0, nullptr);
  bool released = handle != INVALID_HANDLE_VALUE;
  if (released)
    CloseHandle(handle);
  ok &= released;
  printf("RESULT mode=%s displayed=%d lost=%d hardware=%d width=%u height=%u "
         "released=%d failed=%u seconds=%u\n",
         baseline ? "baseline" : "compositor",
         after.i_displayed_pictures - before.i_displayed_pictures,
         after.i_lost_pictures - before.i_lost_pictures, int(hardware.load()),
         status.width, status.height, int(released), status.failed, seconds);
  if (status.failed)
    fprintf(stderr, "%s\n", status.error);
  return ok ? 0 : 1;
}
