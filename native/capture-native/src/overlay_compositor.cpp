#include "overlay_compositor.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <cstdio>
#include <stdexcept>
#include <string>

namespace clypdat {
namespace {
using Microsoft::WRL::ComPtr;
void checked(HRESULT result, const char* what) {
    if (SUCCEEDED(result)) return;
    char code[16]{}; std::snprintf(code, sizeof(code), "0x%08X", unsigned(result));
    throw std::runtime_error(std::string(what) + " (hr=" + code + ")");
}
// A full-screen triangle drawn through a viewport set to the layer's
// placement covers exactly its pixels. Each pixel loads the texel the CPU
// reference picks: (x - left) * bitmap width / placed width.
constexpr char kShader[] =
    "struct V{float4 p:SV_Position;};"
    "V VS(uint id:SV_VertexID){V o;o.p=float4(id==2?3:-1,id==1?3:-1,0,1);return o;}"
    "Texture2D<float4> Layer:register(t0);"
    "cbuffer Placement:register(b0){int4 bounds;int4 size;};"
    "float4 PS(V i):SV_Target{int2 pixel=int2(i.p.xy)-bounds.xy;return Layer.Load(int3(pixel*size.xy/bounds.zw,0));}";
struct Constants { int32_t x, y, width, height, bitmap_width, bitmap_height, unused[2]; };
}

struct OverlayCompositor::Impl {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11BlendState> straight, premultiplied;
    ComPtr<ID3D11RasterizerState> raster;
    struct Layer {
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<ID3D11ShaderResourceView> view;
        std::shared_ptr<const OverlayBitmap> uploaded; // What the texture holds.
    };
    Layer camera, keyboard;
    Stats stats;

    // Makes `layer` hold `bitmap`: nothing when it already does, a texture
    // update when the size is unchanged, a new texture otherwise.
    bool upload(Layer& layer, const std::shared_ptr<const OverlayBitmap>& bitmap) {
        if (layer.uploaded == bitmap) return true;
        layer.uploaded.reset();
        try {
            D3D11_TEXTURE2D_DESC current{}; if (layer.texture) layer.texture->GetDesc(&current);
            if (!layer.texture || current.Width != UINT(bitmap->width) || current.Height != UINT(bitmap->height)) {
                layer.view.Reset(); layer.texture.Reset();
                D3D11_TEXTURE2D_DESC desc{};
                desc.Width = UINT(bitmap->width); desc.Height = UINT(bitmap->height); desc.MipLevels = 1; desc.ArraySize = 1;
                desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_DEFAULT;
                desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                checked(device->CreateTexture2D(&desc, nullptr, &layer.texture), "Create overlay layer texture");
                checked(device->CreateShaderResourceView(layer.texture.Get(), nullptr, &layer.view), "Create overlay layer view");
                ++stats.allocations;
            }
            context->UpdateSubresource(layer.texture.Get(), 0, nullptr, bitmap->bgra.data(), UINT(bitmap->stride), 0);
            layer.uploaded = bitmap; ++stats.uploads;
            return true;
        } catch (const std::exception&) {
            ++stats.upload_failures; layer.view.Reset(); layer.texture.Reset();
            return false;
        }
    }
    bool draw(Layer& layer, const OverlayLayer& source, int width, int height) {
        if (!source.requested || !source.bitmap || !upload(layer, source.bitmap)) return false;
        const auto& bitmap = *source.bitmap;
        const auto placed = overlay_placement(bitmap, source.transform, width, height);
        const Constants values{placed.x, placed.y, placed.width, placed.height, bitmap.width, bitmap.height, {}};
        context->UpdateSubresource(constants.Get(), 0, nullptr, &values, 0, 0);
        const D3D11_VIEWPORT viewport{float(placed.x), float(placed.y), float(placed.width), float(placed.height), 0, 1};
        context->RSSetViewports(1, &viewport);
        auto* view = layer.view.Get();
        context->PSSetShaderResources(0, 1, &view);
        context->OMSetBlendState(bitmap.premultiplied ? premultiplied.Get() : straight.Get(), nullptr, 0xffffffff);
        context->Draw(3, 0);
        return true;
    }
};

OverlayCompositor::OverlayCompositor(ID3D11Device* device) : impl_(std::make_unique<Impl>()) {
    auto& s = *impl_;
    if (!device) throw std::invalid_argument("Overlay compositor requires a device");
    s.device = device; device->GetImmediateContext(&s.context);
    ComPtr<ID3DBlob> vs, ps, error;
    checked(D3DCompile(kShader, sizeof(kShader) - 1, nullptr, nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vs, &error), "Compile overlay vertex shader");
    checked(D3DCompile(kShader, sizeof(kShader) - 1, nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &ps, &error), "Compile overlay pixel shader");
    checked(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &s.vertex), "Create overlay vertex shader");
    checked(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &s.pixel), "Create overlay pixel shader");
    D3D11_BUFFER_DESC buffer{}; buffer.ByteWidth = sizeof(Constants); buffer.Usage = D3D11_USAGE_DEFAULT; buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    checked(device->CreateBuffer(&buffer, nullptr, &s.constants), "Create overlay constants");
    // Colour only: the canvas alpha is unused by the NV12 conversion.
    auto blend = [&](D3D11_BLEND source, ComPtr<ID3D11BlendState>& state) {
        D3D11_BLEND_DESC desc{};
        auto& target = desc.RenderTarget[0];
        target.BlendEnable = TRUE; target.SrcBlend = source; target.DestBlend = D3D11_BLEND_INV_SRC_ALPHA; target.BlendOp = D3D11_BLEND_OP_ADD;
        target.SrcBlendAlpha = D3D11_BLEND_ONE; target.DestBlendAlpha = D3D11_BLEND_ZERO; target.BlendOpAlpha = D3D11_BLEND_OP_ADD;
        target.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_RED | D3D11_COLOR_WRITE_ENABLE_GREEN | D3D11_COLOR_WRITE_ENABLE_BLUE;
        checked(device->CreateBlendState(&desc, &state), "Create overlay blend state");
    };
    blend(D3D11_BLEND_SRC_ALPHA, s.straight);
    blend(D3D11_BLEND_ONE, s.premultiplied);
    D3D11_RASTERIZER_DESC raster{}; raster.FillMode = D3D11_FILL_SOLID; raster.CullMode = D3D11_CULL_NONE; raster.DepthClipEnable = TRUE;
    checked(device->CreateRasterizerState(&raster, &s.raster), "Create overlay rasterizer state");
}
OverlayCompositor::~OverlayCompositor() = default;

OverlayCompositionResult OverlayCompositor::draw(ID3D11RenderTargetView* canvas, int width, int height, const OverlayFrame& layers) {
    auto& s = *impl_;
    if (!canvas || width <= 0 || height <= 0) throw std::invalid_argument("Invalid overlay canvas");
    OverlayCompositionResult result;
    if (!layers.drawable()) return result;
    s.context->OMSetRenderTargets(1, &canvas, nullptr);
    s.context->IASetInputLayout(nullptr);
    s.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    s.context->VSSetShader(s.vertex.Get(), nullptr, 0);
    s.context->PSSetShader(s.pixel.Get(), nullptr, 0);
    auto* constants = s.constants.Get();
    s.context->PSSetConstantBuffers(0, 1, &constants);
    s.context->RSSetState(s.raster.Get());
    result.camera = s.draw(s.camera, layers.camera, width, height);
    result.keyboard = s.draw(s.keyboard, layers.keyboard, width, height);
    // Leave no binding behind for other users of the immediate context.
    ID3D11RenderTargetView* no_target = nullptr; ID3D11ShaderResourceView* no_view = nullptr; ID3D11Buffer* no_buffer = nullptr;
    s.context->OMSetRenderTargets(1, &no_target, nullptr);
    s.context->PSSetShaderResources(0, 1, &no_view);
    s.context->PSSetConstantBuffers(0, 1, &no_buffer);
    s.context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
    s.context->RSSetState(nullptr);
    return result;
}
OverlayCompositor::Stats OverlayCompositor::stats() const { return impl_->stats; }
}
