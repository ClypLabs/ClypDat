#include "compositor.h"
#include <DirectXPackedVector.h>
#include <array>
#include <cmath>
#include <cstdio>
#include <stdexcept>
#include <vector>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;
static void require(bool value, const char *message) {
  if (!value)
    throw std::runtime_error(message);
}
static void check(HRESULT hr) {
  require(SUCCEEDED(hr), "D3D11 harness failure");
}
int main() {
  try {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> immediate;
    check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
                            nullptr, 0, D3D11_SDK_VERSION, &device, nullptr,
                            &immediate));
    constexpr unsigned width = 128, height = 64;
    auto token = cdvo_create(CDVO_ABI);
    require(token != 0, "create context");
    require(cdvo_create(999) == 0, "reject wrong ABI");
    cdvo_blur blur{{.25f, .25f, .5f, .5f}, 0, 100, 3, 0};
    cdvo_state state{sizeof(cdvo_state),
                     CDVO_ABI,
                     1,
                     1,
                     0,
                     1,
                     1000000,
                     1,
                     0,
                     &blur,
                     nullptr};
    require(cdvo_submit(token, &state) != 0, "submit state");
    auto renderer = cdvo_attach(token, device.Get(), immediate.Get());
    require(renderer != nullptr, "attach renderer");
    D3D11_TEXTURE2D_DESC td{};
    td.Width = width;
    td.Height = height;
    td.MipLevels = td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
    td.SampleDesc.Count = 1;
    td.BindFlags = D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> output, staging;
    ComPtr<ID3D11RenderTargetView> rtv;
    check(device->CreateTexture2D(&td, nullptr, &output));
    check(device->CreateRenderTargetView(output.Get(), nullptr, &rtv));
    td.BindFlags = 0;
    td.Usage = D3D11_USAGE_STAGING;
    td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    check(device->CreateTexture2D(&td, nullptr, &staging));
    D3D11_VIEWPORT viewport{0, 0, width, height, 0, 1};
    auto sample = [&](unsigned x, unsigned y) {
      immediate->CopyResource(staging.Get(), output.Get());
      D3D11_MAPPED_SUBRESOURCE mapped{};
      check(immediate->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
      std::array<float, 4> pixel{};
      auto p =
          reinterpret_cast<float *>(static_cast<unsigned char *>(mapped.pData) +
                                    y * mapped.RowPitch) +
          x * 4;
      std::copy_n(p, 4, pixel.begin());
      immediate->Unmap(staging.Get(), 0);
      return pixel;
    };
    unsigned frames = 0;
    for (double fps : {24., 30000. / 1001, 30., 60000. / 1001, 60., 120., 0.}) {
      double time = .91;
      for (unsigned frame = 0; frame < 16; ++frame) {
        time += fps ? 1 / fps : (frame % 2 ? .011 : .047);
        auto source = cdvo_begin_picture(renderer, width, height,
                                         1000000 + int64_t(time * 1000000));
        require(source != nullptr, "retain decoded frame");
        ComPtr<ID3D11Resource> resource;
        source->GetResource(&resource);
        std::vector<uint16_t> pixels(width * height * 4);
        for (unsigned y = 0; y < height; ++y)
          for (unsigned x = 0; x < width; ++x) {
            auto p = &pixels[(y * width + x) * 4];
            p[0] = DirectX::PackedVector::XMConvertFloatToHalf(
                float(frames + 1) / 128);
            p[1] = ((x + frame) % 8 < 4) ? 0x3c00 : 0;
            p[2] = 0;
            p[3] = 0x3c00;
          }
        immediate->UpdateSubresource(resource.Get(), 0, nullptr, pixels.data(),
                                     width * 8, 0);
        require(cdvo_compose(renderer, rtv.Get(), &viewport, 0) != 0,
                "compose decoded picture");
        cdvo_presented(renderer);
        auto outside = sample(2, 32), inside = sample(64, 32);
        require(
            std::abs(outside[0] - float(frames + 1) / 128) < .001f &&
                std::abs(inside[0] - outside[0]) < .001f,
            "blur and surrounding video must have identical frame identity");
        require(inside[1] > .1f && inside[1] < .9f,
                "blur must modify moving bars");
        // A delayed edit never calibrates a clock or selects another frame.
        state.revision++;
        state.rate = 0;
        state.media_seconds = time;
        require(cdvo_submit(token, &state) != 0, "paused edit");
        require(cdvo_compose(renderer, rtv.Get(), &viewport, 1) != 0,
                "retained redraw");
        require(std::abs(sample(64, 32)[0] - outside[0]) < .001f,
                "paused redraw retains picture identity");
        state.rate = 1;
        state.media_seconds = 0;
        frames++;
      }
    }
    // Decoder may prepare a future picture before the paused edit arrives.
    // Redraw must still use the last picture that reached presentation.
    auto retainedIdentity = sample(64, 32)[0];
    auto future = cdvo_begin_picture(renderer, width, height, 9000000);
    frames++;
    const float futureColor[4] = {1, 0, 0, 1};
    immediate->ClearRenderTargetView(future, futureColor);
    state.rate = 0;
    state.media_seconds = 1;
    state.revision++;
    require(cdvo_submit(token, &state) != 0, "delayed edit state");
    require(cdvo_compose(renderer, rtv.Get(), &viewport, 1) != 0,
            "redraw while next picture prepared");
    require(std::abs(sample(64, 32)[0] - retainedIdentity) < .001f,
            "future prepared picture must not leak into paused redraw");

    // Underlying artwork is blurred; text and crop guides remain above it.
    std::vector<uint8_t> artPixels(width * height * 4);
    for (unsigned y = 0; y < height; ++y)
      for (unsigned x = 0; x < width; ++x) {
        auto p = &artPixels[(y * width + x) * 4];
        p[0] = 0;
        p[1] = x % 8 < 4 ? 255 : 0;
        p[2] = 0;
        p[3] = 255;
      }
    require(cdvo_update_artwork(token, state.generation, 1, width, height,
                                width * 4, artPixels.data()) != 0,
            "upload moving artwork");
    cdvo_artwork art{1, {0, 0, 1, 1}, 0, 100, 0, 0};
    state.artwork_count = 1;
    state.artwork = &art;
    state.revision++;
    require(cdvo_submit(token, &state) != 0, "commit artwork");
    require(cdvo_compose(renderer, rtv.Get(), &viewport, 1) != 0,
            "blur artwork");
    require(sample(64, 32)[1] > .1f && sample(64, 32)[1] < .9f,
            "artwork below blur must be sampled");
    blur.bounds.x = .28125f;
    auto outsideBlur = sample(35, 32);
    auto shapeCorner = sample(36, 16);
    for (unsigned shape : {0u, 1u, 2u}) {
      blur.shape = shape;
      state.revision++;
      require(cdvo_submit(token, &state) != 0, "shape state");
      require(cdvo_compose(renderer, rtv.Get(), &viewport, 1) != 0,
              "shape redraw");
      require(std::abs(sample(35, 32)[1] - outsideBlur[1]) < .001f,
              "pixels outside blur remain unchanged");
      if (shape != 0)
        require(std::abs(sample(36, 16)[1] - shapeCorner[1]) < .001f,
                "shape corner remains unblurred");
      require(sample(68, 32)[1] > .1f && sample(68, 32)[1] < .9f,
              "shape center blurred");
    }
    for (unsigned layer : {1u, 2u}) {
      art.layer = layer;
      state.revision++;
      require(cdvo_submit(token, &state) != 0, "upper artwork layer");
      require(cdvo_compose(renderer, rtv.Get(), &viewport, 1) != 0,
              "upper artwork redraw");
      require(sample(64, 32)[1] > .99f, "text and guides remain above blur");
    }
    // Uploads stay pending until their immutable scene revision is committed.
    std::fill(artPixels.begin(), artPixels.end(), 255);
    require(cdvo_update_artwork(token, state.generation, 1, width, height,
                                width * 4, artPixels.data()) != 0,
            "pending artwork");
    require(cdvo_compose(renderer, rtv.Get(), &viewport, 1) != 0,
            "old scene during upload");
    require(sample(64, 32)[0] < .001f,
            "uncommitted artwork cannot change scene");
    state.revision++;
    require(cdvo_submit(token, &state) != 0, "commit pending artwork");
    require(cdvo_compose(renderer, rtv.Get(), &viewport, 1) != 0 &&
                sample(64, 32)[0] > .99f,
            "committed artwork visible");
    state.artwork_count = 0;
    state.artwork = nullptr;
    state.generation++;
    state.revision = 0;
    require(cdvo_submit(token, &state) != 0, "seek barrier");
    auto landed = cdvo_begin_picture(renderer, width, height, 10000000);
    require(landed != nullptr, "retain landing picture before scene");
    ++frames;
    immediate->ClearRenderTargetView(landed, futureColor);
    require(cdvo_compose(renderer, rtv.Get(), &viewport, 0) == 0,
            "reject picture prepared before seek");
    // Scene publication can lag decoder output. The accepted seek generation
    // must retain its landing texture and present it while transport is paused.
    state.revision = 1;
    state.artwork_count = 1;
    state.artwork = &art;
    require(cdvo_submit(token, &state) != 0, "commit delayed seek scene");
    require(cdvo_needs_redraw(renderer) != 0, "delayed seek requests redraw");
    require(cdvo_compose(renderer, rtv.Get(), &viewport, 1) != 0,
            "present retained landing picture after scene");
    cdvo_presented(renderer);
    cdvo_status status{sizeof(cdvo_status), CDVO_ABI};
    require(cdvo_query(token, &status) != 0 &&
                status.presented_picture == status.decoded_picture,
            "delayed scene presents current landing picture");
    // A seek onto the frame the player is already parked on decodes nothing -
    // VLC never delivers a picture for the new generation, so the barrier must
    // adopt the retained landing frame instead of waiting for one forever.
    auto parkedIdentity = sample(64, 32)[0];
    auto decodedBeforeParkedSeek = status.decoded_picture;
    state.generation++;
    state.revision = 0;
    require(cdvo_submit(token, &state) != 0, "parked seek barrier");
    state.revision = 1;
    require(cdvo_submit(token, &state) != 0, "commit parked seek scene");
    require(cdvo_needs_redraw(renderer) != 0, "parked seek requests redraw");
    require(cdvo_compose(renderer, rtv.Get(), &viewport, 1) != 0,
            "present retained frame for parked seek");
    cdvo_presented(renderer);
    require(cdvo_query(token, &status) != 0 &&
                status.generation == state.generation &&
                status.presented_picture != 0 &&
                status.decoded_picture == decodedBeforeParkedSeek,
            "parked seek presents its retained frame without a new decode");
    require(std::abs(sample(64, 32)[0] - parkedIdentity) < .001f,
            "parked seek keeps the landing frame identity");
    // A barrier that moves the position must NOT adopt the stale frame; that
    // generation waits for the picture the decoder is about to deliver.
    state.generation++;
    state.revision = 0;
    state.media_seconds += 5;
    require(cdvo_submit(token, &state) != 0, "moved seek barrier");
    state.revision = 1;
    require(cdvo_submit(token, &state) != 0, "commit moved seek scene");
    require(cdvo_needs_redraw(renderer) == 0,
            "moved seek waits for its own picture");
    require(cdvo_compose(renderer, rtv.Get(), &viewport, 1) == 0,
            "moved seek must not compose the stale frame");
    state.media_seconds -= 5;
    // Recommit the unchanged art after transport's revision-zero barrier. It
    // must reuse its GPU resource; a seek must not require a duplicate upload.
    state.revision = 1;
    state.artwork_count = 1;
    state.artwork = &art;
    require(cdvo_submit(token, &state) != 0, "retain artwork across seek");
    state.generation--;
    require(cdvo_submit(token, &state) == 0, "reject obsolete state");
    state.artwork_count = 0;
    state.artwork = nullptr;
    require(cdvo_query(token, &status) != 0 && !status.failed,
            "status handshake");
    require(status.decoded_picture == frames &&
                status.presented_picture == frames &&
                status.width == width && status.height == height,
            "status frame identity and original dimensions");
    cdvo_fail(renderer, "Injected device failure");
    require(cdvo_query(token, &status) != 0 && status.failed,
            "device failure handshake");
    require(cdvo_begin_picture(renderer, width, height, 10000000) == nullptr,
            "failed device cannot accept pictures");
    cdvo_release(token);
    require(cdvo_request_redraw(token) == 0, "reject released context");
    require(cdvo_compose(renderer, rtv.Get(), &viewport, 1) == 0,
            "closed context cannot present");
    cdvo_detach(renderer);
    for (unsigned replacement = 0; replacement < 3; ++replacement) {
      auto next = cdvo_create(CDVO_ABI);
      require(next != token, "replacement context identity");
      state.generation = 1;
      state.revision = 1;
      state.blur_count = 0;
      require(cdvo_submit(next, &state) != 0, "replacement scene");
      auto outputRenderer = cdvo_attach(next, device.Get(), immediate.Get());
      require(outputRenderer != nullptr, "reopen after failure");
      auto source = cdvo_begin_picture(outputRenderer, width, height, 10000000);
      require(source != nullptr, "replacement source");
      immediate->ClearRenderTargetView(source, futureColor);
      auto resizedViewport = viewport;
      resizedViewport.Width = width / 2;
      resizedViewport.Height = height / 2;
      require(cdvo_compose(outputRenderer, rtv.Get(), &resizedViewport, 0) != 0,
              "resized destination");
      cdvo_presented(outputRenderer);
      require(cdvo_query(next, &status) != 0 && !status.failed &&
                  status.width == width && status.height == height,
              "destination resizing preserves original source dimensions");
      cdvo_detach(outputRenderer);
      cdvo_release(next);
    }
    printf("PASS: %u GPU pictures; seven cadence sequences; pause/edit; "
           "obsolete seek rejection; release.\n",
           frames);
    return 0;
  } catch (const std::exception &error) {
    fprintf(stderr, "FAIL: %s\n", error.what());
    return 1;
  }
}
