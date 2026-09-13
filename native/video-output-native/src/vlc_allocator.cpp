#include <cstdlib>
#include <windows.h>
// The bundled MinGW core allocates through msvcrt.dll. Its strings, pictures,
// and arrays must be released through that same heap, not MSVC's static UCRT.
namespace {
HMODULE runtime() {
  static HMODULE module = LoadLibraryW(L"msvcrt.dll");
  return module;
}
template <class T> T function(const char *name) {
  auto result = reinterpret_cast<T>(GetProcAddress(runtime(), name));
  if (!result)
    std::abort();
  return result;
}
} // namespace
extern "C" void *cdvo_vlc_malloc(size_t n) {
  static auto f = function<void *(__cdecl *)(size_t)>("malloc");
  return f(n);
}
extern "C" void *cdvo_vlc_calloc(size_t n, size_t size) {
  static auto f = function<void *(__cdecl *)(size_t, size_t)>("calloc");
  return f(n, size);
}
extern "C" void *cdvo_vlc_realloc(void *p, size_t n) {
  static auto f = function<void *(__cdecl *)(void *, size_t)>("realloc");
  return f(p, n);
}
extern "C" void cdvo_vlc_free(void *p) {
  static auto f = function<void(__cdecl *)(void *)>("free");
  f(p);
}
extern "C" char *cdvo_vlc_strdup(const char *p) {
  static auto f = function<char *(__cdecl *)(const char *)>("_strdup");
  return f(p);
}
