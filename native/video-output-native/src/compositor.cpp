#include "compositor.h"
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <d3dcompiler.h>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <unordered_map>
#include <vector>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;
namespace {
void check(HRESULT hr) {
  if (FAILED(hr))
    throw std::runtime_error("D3D11 compositor operation failed");
}
struct Image {
  uint32_t width, height;
  std::vector<uint8_t> pixels;
  uint64_t serial;
};
struct State {
  cdvo_state clock{};
  std::vector<cdvo_blur> blurs;
  std::vector<cdvo_artwork> artwork;
};
struct Context {
  std::mutex mutex;
  State state;
  std::unordered_map<uint64_t, std::shared_ptr<const Image>> images;
  std::unordered_map<uint64_t, std::shared_ptr<const Image>> pending_images;
  cdvo_status status{sizeof(cdvo_status), CDVO_ABI};
  bool closed = false, dirty = true;
  uint64_t image_serial = 0;
};
std::mutex registry_mutex;
std::unordered_map<uint64_t, std::shared_ptr<Context>> registry;
std::atomic<uint64_t> next_token{1};
std::shared_ptr<Context> lookup(uint64_t token) {
  std::lock_guard lock(registry_mutex);
  auto it = registry.find(token);
  return it == registry.end() ? nullptr : it->second;
}
bool finite(cdvo_rect r) {
  return std::isfinite(r.x) && std::isfinite(r.y) && std::isfinite(r.width) &&
         std::isfinite(r.height) && r.width > 0 && r.height > 0;
}
bool active(double start, double end, double time) {
  return start <= time && time < end;
}
struct Texture {
  ComPtr<ID3D11Texture2D> texture;
  ComPtr<ID3D11RenderTargetView> rtv;
  ComPtr<ID3D11ShaderResourceView> srv;
};
struct Constants {
  float rect[4], step[4], parameters[4];
};
const char *shader = R"(
cbuffer Settings : register(b0) { float4 rect; float4 step; float4 parameters; };
Texture2D source : register(t0); SamplerState linearClamp : register(s0);
struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
Vertex vs(uint id : SV_VertexID) {
    Vertex v; float2 p=float2((id<<1)&2,id&2);
    v.position=float4(p*float2(2,-2)+float2(-1,1),0,1); v.uv=p; return v;
}
float4 copy(Vertex v) : SV_Target { return source.Sample(linearClamp,v.uv); }
float4 artwork(Vertex v) : SV_Target {
    float2 p=(v.uv-rect.xy)/rect.zw;
    if(any(p<0)||any(p>1)) discard;
    return source.Sample(linearClamp,p);
}
float4 gaussian(Vertex v) : SV_Target {
    float sigma=parameters.x; int radius=(int)ceil(3*sigma);
    float4 sum=0; float weight=0;
    for(int i=-radius;i<=radius;++i) {
        float w=exp(-.5*i*i/(sigma*sigma));
        sum+=w*source.SampleLevel(linearClamp,v.uv+i*step.xy,0); weight+=w;
    }
    return sum/weight;
}
float4 mask(Vertex v) : SV_Target {
    float2 p=(v.uv-rect.xy)/rect.zw;
    float2 size=rect.zw*step.zw;
    float2 localPosition=(p-.5)*size+.5;
    float distance=min(size.x,size.y)*.5-max(abs(localPosition.x),abs(localPosition.y));
    if(parameters.y==2) {
        float2 radius=size*.5;
        distance=(1-length(localPosition/radius))*min(radius.x,radius.y);
    }
    if(parameters.y==1) {
        float radius=min(size.x,size.y)*.2;
        float2 q=abs(localPosition)-(size*.5-radius);
        distance=radius-(length(max(q,0))+min(max(q.x,q.y),0));
    }
    float shortSide=min(size.x,size.y);
    float feather=min(max(1,shortSide*.05),shortSide*.25);
    float coverage=smoothstep(0,feather,distance);
    return source.Sample(linearClamp,v.uv)*coverage;
}
)";
struct Renderer {
  std::shared_ptr<Context> context;
  ComPtr<ID3D11Device> device;
  ComPtr<ID3D11DeviceContext> immediate;
  ComPtr<ID3D11VertexShader> vs;
  ComPtr<ID3D11PixelShader> copy, artwork, gaussian, mask;
  ComPtr<ID3D11Buffer> constants;
  ComPtr<ID3D11SamplerState> sampler;
  ComPtr<ID3D11BlendState> blend;
  ComPtr<ID3D11RasterizerState> rasterizer;
  Texture pending, retained, scene, horizontal, vertical;
  struct Cached {
    Texture texture;
    uint64_t serial;
  };
  std::unordered_map<uint64_t, Cached> images;
  uint32_t width = 0, height = 0;
  uint64_t generation = 0, picture = 0, revision = 0;
  uint64_t pending_generation = 0, pending_picture = 0, composed_revision = 0,
           next_picture = 0;
  int64_t date = 0;
  int64_t pending_date = 0;
  bool valid = false, pending_valid = false, composed_pending = false;
  Renderer(std::shared_ptr<Context> c, ID3D11Device *d, ID3D11DeviceContext *i)
      : context(std::move(c)), device(d), immediate(i) {
    auto compile = [&](const char *entry, const char *target) {
      ComPtr<ID3DBlob> bytes, errors;
      check(D3DCompile(shader, strlen(shader), "ClypDat compositor", nullptr,
                       nullptr, entry, target, D3DCOMPILE_OPTIMIZATION_LEVEL3,
                       0, &bytes, &errors));
      return bytes;
    };
    auto vertex = compile("vs", "vs_4_0");
    check(device->CreateVertexShader(vertex->GetBufferPointer(),
                                     vertex->GetBufferSize(), nullptr, &vs));
    auto pixel = [&](const char *entry, ComPtr<ID3D11PixelShader> &output) {
      auto b = compile(entry, "ps_4_0");
      check(device->CreatePixelShader(b->GetBufferPointer(), b->GetBufferSize(),
                                      nullptr, &output));
    };
    pixel("copy", copy);
    pixel("artwork", artwork);
    pixel("gaussian", gaussian);
    pixel("mask", mask);
    D3D11_BUFFER_DESC bd{};
    bd.ByteWidth = sizeof(Constants);
    bd.Usage = D3D11_USAGE_DEFAULT;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    check(device->CreateBuffer(&bd, nullptr, &constants));
    D3D11_SAMPLER_DESC sd{};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    check(device->CreateSamplerState(&sd, &sampler));
    D3D11_BLEND_DESC blendDesc{};
    auto &b = blendDesc.RenderTarget[0];
    b.BlendEnable = TRUE;
    b.SrcBlend = D3D11_BLEND_ONE;
    b.DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    b.BlendOp = D3D11_BLEND_OP_ADD;
    b.SrcBlendAlpha = D3D11_BLEND_ONE;
    b.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    b.BlendOpAlpha = D3D11_BLEND_OP_ADD;
    b.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    check(device->CreateBlendState(&blendDesc, &blend));
    D3D11_RASTERIZER_DESC rd{};
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;
    rd.DepthClipEnable = TRUE;
    rd.ScissorEnable = TRUE;
    check(device->CreateRasterizerState(&rd, &rasterizer));
  }
  Texture texture(uint32_t w, uint32_t h, const Image *image = nullptr) {
    Texture t;
    D3D11_TEXTURE2D_DESC td{};
    td.Width = w;
    td.Height = h;
    td.MipLevels = td.ArraySize = 1;
    td.Format =
        image ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R16G16B16A16_FLOAT;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags =
        D3D11_BIND_SHADER_RESOURCE | (image ? 0 : D3D11_BIND_RENDER_TARGET);
    D3D11_SUBRESOURCE_DATA data{};
    if (image) {
      data.pSysMem = image->pixels.data();
      data.SysMemPitch = w * 4;
    }
    check(device->CreateTexture2D(&td, image ? &data : nullptr, &t.texture));
    check(device->CreateShaderResourceView(t.texture.Get(), nullptr, &t.srv));
    if (!image)
      check(device->CreateRenderTargetView(t.texture.Get(), nullptr, &t.rtv));
    return t;
  }
  void resize(uint32_t w, uint32_t h) {
    if (w == width && h == height)
      return;
    valid = pending_valid = false;
    pending = texture(w, h);
    retained = texture(w, h);
    scene = texture(w, h);
    horizontal = texture(w, h);
    vertical = texture(w, h);
    width = w;
    height = h;
  }
  void draw(ID3D11ShaderResourceView *input, ID3D11RenderTargetView *output,
            ID3D11PixelShader *pixel, const Constants &settings,
            const D3D11_VIEWPORT &viewport, const D3D11_RECT &scissor,
            bool alpha = false) {
    immediate->UpdateSubresource(constants.Get(), 0, nullptr, &settings, 0, 0);
    immediate->OMSetRenderTargets(1, &output, nullptr);
    immediate->OMSetBlendState(alpha ? blend.Get() : nullptr, nullptr, ~0u);
    immediate->RSSetState(rasterizer.Get());
    immediate->RSSetViewports(1, &viewport);
    immediate->RSSetScissorRects(1, &scissor);
    immediate->IASetInputLayout(nullptr);
    immediate->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    immediate->VSSetShader(vs.Get(), nullptr, 0);
    immediate->PSSetShader(pixel, nullptr, 0);
    auto cb = constants.Get();
    auto ss = sampler.Get();
    immediate->PSSetConstantBuffers(0, 1, &cb);
    immediate->PSSetSamplers(0, 1, &ss);
    immediate->PSSetShaderResources(0, 1, &input);
    immediate->Draw(3, 0);
    ID3D11ShaderResourceView *empty = nullptr;
    immediate->PSSetShaderResources(0, 1, &empty);
    immediate->OMSetRenderTargets(0, nullptr, nullptr);
  }
};
// VLC installs sampler/blend/rasterizer state during initialization, not each
// picture. Restore those states before returning to its decoder/output code.
struct Restore {
  ID3D11DeviceContext *i;
  ComPtr<ID3D11BlendState> blend;
  ComPtr<ID3D11RasterizerState> raster;
  ComPtr<ID3D11SamplerState> sampler;
  FLOAT factor[4]{};
  UINT mask = 0;
  explicit Restore(ID3D11DeviceContext *value) : i(value) {
    i->OMGetBlendState(&blend, factor, &mask);
    i->RSGetState(&raster);
    i->PSGetSamplers(0, 1, &sampler);
  }
  ~Restore() {
    i->OMSetBlendState(blend.Get(), factor, mask);
    i->RSSetState(raster.Get());
    auto s = sampler.Get();
    i->PSSetSamplers(0, 1, &s);
  }
};
void fail(const std::shared_ptr<Context> &c, const char *message) {
  std::lock_guard lock(c->mutex);
  c->status.failed = 1;
  strncpy_s(c->status.error, message, _TRUNCATE);
}
} // namespace
uint64_t cdvo_create(uint32_t abi) {
  if (abi != CDVO_ABI)
    return 0;
  try {
    auto c = std::make_shared<Context>();
    auto token = next_token.fetch_add(1);
    std::lock_guard lock(registry_mutex);
    registry.emplace(token, std::move(c));
    return token;
  } catch (...) {
    return 0;
  }
}
int cdvo_submit(uint64_t token, const cdvo_state *s) {
  auto c = lookup(token);
  if (!c || !s || s->size != sizeof(*s) || s->abi != CDVO_ABI ||
      s->blur_count > 1024 || s->artwork_count > 1024 ||
      !std::isfinite(s->media_seconds) || !std::isfinite(s->rate) ||
      s->rate < 0 || s->rate > 16 || (s->blur_count && !s->blurs) ||
      (s->artwork_count && !s->artwork))
    return 0;
  try {
    State next;
    next.clock = *s;
    next.clock.blurs = nullptr;
    next.clock.artwork = nullptr;
    for (uint32_t i = 0; i < s->blur_count; ++i) {
      auto b = s->blurs[i];
      if (!finite(b.bounds) || !std::isfinite(b.sigma) || b.sigma <= 0 ||
          b.sigma > 256 || b.shape > 2 || !std::isfinite(b.start) ||
          !std::isfinite(b.end) || b.end <= b.start)
        return 0;
      next.blurs.push_back(b);
    }
    for (uint32_t i = 0; i < s->artwork_count; ++i) {
      auto a = s->artwork[i];
      if (!finite(a.bounds) || a.layer > 2 || !std::isfinite(a.start) ||
          !std::isfinite(a.end) || a.end <= a.start)
        return 0;
      next.artwork.push_back(a);
    }
    std::lock_guard lock(c->mutex);
    if (c->closed || s->generation < c->state.clock.generation ||
        (s->generation == c->state.clock.generation &&
         s->revision < c->state.clock.revision))
      return 0;
    // A seek invalidates pictures, not clip artwork. Revision zero is a
    // transport barrier; retain images until the complete scene commits.
    if (s->generation != c->state.clock.generation)
      c->pending_images.clear();
    auto images = c->images;
    for (auto &item : c->pending_images)
      images[item.first] = item.second;
    for (auto &a : next.artwork)
      if (!images.contains(a.id))
        return 0;
    if (s->revision != 0) std::erase_if(images, [&](auto &item) {
      return std::none_of(next.artwork.begin(), next.artwork.end(),
                          [&](auto &a) { return a.id == item.first; });
    });
    c->images = std::move(images);
    c->pending_images.clear();
    c->state = std::move(next);
    c->dirty = true;
    return 1;
  } catch (...) {
    fail(c, "Composition state allocation failed. Pause and reopen the clip.");
    return 0;
  }
}
int cdvo_update_artwork(uint64_t token, uint64_t generation, uint64_t id,
                        uint32_t w, uint32_t h, uint32_t stride,
                        const void *pixels) {
  auto c = lookup(token);
  if (!c || !id || !pixels || !w || !h || w > 16384 || h > 16384 ||
      stride < w * 4 || uint64_t(w) * h > 67108864)
    return 0;
  try {
    auto image = std::make_shared<Image>();
    image->width = w;
    image->height = h;
    image->pixels.resize(size_t(w) * h * 4);
    for (uint32_t y = 0; y < h; ++y)
      memcpy(image->pixels.data() + size_t(y) * w * 4,
             static_cast<const uint8_t *>(pixels) + size_t(y) * stride, w * 4);
    std::lock_guard lock(c->mutex);
    if (c->closed || generation != c->state.clock.generation)
      return 0;
    image->serial = ++c->image_serial;
    c->pending_images[id] = std::move(image);
    return 1;
  } catch (...) {
    fail(c, "Artwork allocation failed. Pause and reopen the clip.");
    return 0;
  }
}
int cdvo_request_redraw(uint64_t token) {
  auto c = lookup(token);
  if (!c)
    return 0;
  std::lock_guard lock(c->mutex);
  if (c->closed)
    return 0;
  c->dirty = true;
  return 1;
}
int cdvo_query(uint64_t token, cdvo_status *s) {
  auto c = lookup(token);
  if (!c || !s || s->size != sizeof(*s) || s->abi != CDVO_ABI)
    return 0;
  std::lock_guard lock(c->mutex);
  *s = c->status;
  return 1;
}
void cdvo_release(uint64_t token) {
  std::shared_ptr<Context> c;
  {
    std::lock_guard lock(registry_mutex);
    auto it = registry.find(token);
    if (it == registry.end())
      return;
    c = it->second;
    registry.erase(it);
  }
  std::lock_guard lock(c->mutex);
  c->closed = true;
  c->images.clear();
  c->pending_images.clear();
  c->state = {};
}
void *cdvo_attach(uint64_t token, ID3D11Device *device,
                  ID3D11DeviceContext *immediate) {
  auto c = lookup(token);
  if (!c)
    return nullptr;
  try {
    std::lock_guard lock(c->mutex);
    if (c->closed || c->status.attached)
      return nullptr;
    auto r = new Renderer(c, device, immediate);
    c->status.attached = 1;
    return r;
  } catch (...) {
    fail(c, "GPU compositor initialization failed. Update the graphics driver, "
            "then reopen the clip.");
    return nullptr;
  }
}
void cdvo_detach(void *renderer) {
  if (!renderer)
    return;
  auto r = static_cast<Renderer *>(renderer);
  {
    std::lock_guard lock(r->context->mutex);
    r->context->status.attached = 0;
  }
  delete r;
}
void cdvo_fail(void *renderer, const char *message) {
  if (renderer)
    fail(static_cast<Renderer *>(renderer)->context, message);
}
ID3D11RenderTargetView *cdvo_begin_picture(void *renderer, uint32_t width,
                                           uint32_t height, int64_t date) {
  if (!renderer)
    return nullptr;
  auto &r = *static_cast<Renderer *>(renderer);
  try {
    std::lock_guard lock(r.context->mutex);
    if (r.context->closed || r.context->status.failed)
      return nullptr;
    r.resize(width, height);
    r.pending_generation = r.context->state.clock.generation;
    r.pending_date = date;
    r.pending_picture = ++r.next_picture;
    r.pending_valid = true;
    r.context->status.decoded_picture = r.pending_picture;
    r.context->status.width = width;
    r.context->status.height = height;
    return r.pending.rtv.Get();
  } catch (...) {
    fail(r.context,
         "Source texture allocation failed. Pause and reopen the clip.");
    return nullptr;
  }
}
int cdvo_has_retained_picture(void *renderer) {
  if (!renderer)
    return 0;
  auto &r = *static_cast<Renderer *>(renderer);
  std::lock_guard lock(r.context->mutex);
  return r.valid && !r.context->closed && !r.context->status.failed &&
         r.generation == r.context->state.clock.generation;
}
int cdvo_needs_redraw(void *renderer) {
  if (!renderer)
    return 0;
  auto &r = *static_cast<Renderer *>(renderer);
  std::lock_guard lock(r.context->mutex);
  const auto generation = r.context->state.clock.generation;
  // A seek can decode its landing picture before Avalonia commits its scene.
  // Keep that texture and let the first paused redraw present it after commit.
  return r.context->state.clock.rate == 0 && r.context->dirty &&
         !r.context->closed && !r.context->status.failed &&
         ((r.valid && r.generation == generation) ||
          (r.pending_valid && r.pending_generation == generation));
}

int cdvo_compose(void *renderer, ID3D11RenderTargetView *output,
                 const D3D11_VIEWPORT *viewport, int redraw) {
  if (!renderer || !output || !viewport)
    return 0;
  auto &r = *static_cast<Renderer *>(renderer);
  try {
    State state;
    std::unordered_map<uint64_t, std::shared_ptr<const Image>> images;
    const auto scene_generation = r.context->state.clock.generation;
    const auto use_pending = !redraw ||
                             ((!r.valid || r.generation != scene_generation) &&
                              r.pending_valid &&
                              r.pending_generation == scene_generation);
    const auto generation = use_pending ? r.pending_generation : r.generation;
    const auto date = use_pending ? r.pending_date : r.date;
    {
      std::lock_guard lock(r.context->mutex);
      if (!(use_pending ? r.pending_valid : r.valid) || r.context->closed ||
          r.context->status.failed || r.context->state.clock.revision == 0 ||
          generation != r.context->state.clock.generation)
        return 0;
      state = r.context->state;
      images = r.context->images;
      r.context->dirty = false;
    }
    Restore restore(r.immediate.Get());
    auto time = state.clock.media_seconds +
                (state.clock.rate == 0 ? 0
                                       : double(date - state.clock.clock_us) /
                                             1000000 * state.clock.rate);
    const D3D11_VIEWPORT full{0, 0, float(r.width), float(r.height), 0, 1};
    const D3D11_RECT all{0, 0, LONG(r.width), LONG(r.height)};
    Constants c{};
    r.immediate->CopyResource(r.scene.texture.Get(),
                              use_pending ? r.pending.texture.Get()
                                          : r.retained.texture.Get());
    auto drawArt = [&](uint32_t layer) {
      for (auto &a : state.artwork)
        if (a.layer == layer && active(a.start, a.end, time)) {
          auto image = images.find(a.id);
          if (image == images.end())
            throw std::runtime_error("missing artwork");
          auto &cached = r.images[a.id];
          if (cached.serial != image->second->serial) {
            cached.texture =
                r.texture(image->second->width, image->second->height,
                          image->second.get());
            cached.serial = image->second->serial;
          }
          Constants settings{};
          memcpy(settings.rect, &a.bounds, sizeof(a.bounds));
          r.draw(cached.texture.srv.Get(), r.scene.rtv.Get(), r.artwork.Get(),
                 settings, full, all, true);
        }
    };
    drawArt(0);
    for (auto &b : state.blurs)
      if (active(b.start, b.end, time)) {
        c = {};
        memcpy(c.rect, &b.bounds, sizeof(b.bounds));
        c.parameters[0] = b.sigma;
        c.parameters[1] = float(b.shape);
        c.step[2] = float(r.width);
        c.step[3] = float(r.height);
        auto region = [&](float margin) {
          return D3D11_RECT{
              LONG(std::clamp(std::floor(b.bounds.x * r.width - margin), 0.f,
                              float(r.width))),
              LONG(std::clamp(std::floor(b.bounds.y * r.height - margin), 0.f,
                              float(r.height))),
              LONG(std::clamp(
                  std::ceil((b.bounds.x + b.bounds.width) * r.width + margin),
                  0.f, float(r.width))),
              LONG(std::clamp(
                  std::ceil((b.bounds.y + b.bounds.height) * r.height + margin),
                  0.f, float(r.height)))};
        };
        auto padded = region(std::ceil(3 * b.sigma) + 1);
        auto box = region(0);
        c.step[0] = 1.f / r.width;
        c.step[1] = 0;
        r.draw(r.scene.srv.Get(), r.horizontal.rtv.Get(), r.gaussian.Get(), c,
               full, padded);
        c.step[0] = 0;
        c.step[1] = 1.f / r.height;
        r.draw(r.horizontal.srv.Get(), r.vertical.rtv.Get(), r.gaussian.Get(),
               c, full, box);
        r.draw(r.vertical.srv.Get(), r.scene.rtv.Get(), r.mask.Get(), c, full,
               box, true);
      }
    drawArt(1);
    drawArt(2);
    D3D11_RECT target{LONG(viewport->TopLeftX), LONG(viewport->TopLeftY),
                      LONG(std::ceil(viewport->TopLeftX + viewport->Width)),
                      LONG(std::ceil(viewport->TopLeftY + viewport->Height))};
    c = {};
    r.draw(r.scene.srv.Get(), output, r.copy.Get(), c, *viewport, target);
    std::erase_if(r.images,
                  [&](auto &item) { return !images.contains(item.first); });
    {
      std::lock_guard lock(r.context->mutex);
      if (generation != r.context->state.clock.generation)
        return 0;
      r.composed_revision = state.clock.revision;
      r.composed_pending = use_pending;
      if (redraw) {
        r.context->status.redraws++;
        r.context->status.revision = state.clock.revision;
      }
    }
    check(r.device->GetDeviceRemovedReason());
    return 1;
  } catch (...) {
    fail(r.context, "GPU composition failed. Playback paused; reopen the clip "
                    "or update the graphics driver.");
    return 0;
  }
}
void cdvo_presented(void *renderer) {
  if (!renderer)
    return;
  auto &r = *static_cast<Renderer *>(renderer);
  if (r.composed_pending) {
    std::swap(r.pending, r.retained);
    r.valid = true;
    r.pending_valid = false;
    r.generation = r.pending_generation;
    r.date = r.pending_date;
    r.picture = r.pending_picture;
  }
  r.revision = r.composed_revision;
  std::lock_guard lock(r.context->mutex);
  r.context->status.generation = r.generation;
  r.context->status.revision = r.revision;
  r.context->status.presented_picture = r.picture;
}
