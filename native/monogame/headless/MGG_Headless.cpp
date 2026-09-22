// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

// Headless graphics backend (MGG). Satisfies the whole api_MGG.h contract without a GPU, a driver,
// or a display server, so Game.Run()'s Initialize -> Update/Draw -> Present loop completes in CI.
//
// Nothing rasterizes. Draw calls are accepted and discarded, and GetBackBufferData returns a blank
// surface. That is the deliberate scope: this backend exists so tests can exercise the real draw
// path without bypassing it, not so they can assert on pixels. Visual verification still needs a
// real backend.
//
// Where state is cheap to keep, it is kept rather than faked, because a stub that returns a
// plausible-looking wrong value fails far away from the mistake. Buffers and textures retain what
// SetData wrote so GetData round-trips honestly; the swapchain remembers its size so
// GetBackBufferSize reports what was actually requested; and the adapter advertises a real
// non-zero display mode, because a 0x0 mode makes the managed layer pick a 0x0 resolution and
// crash creating render targets.

#include "api_MGG.h"

#include "mg_common.h"

#include "AlphaTestEffect.vk.mgfxo.h"
#include "BasicEffect.vk.mgfxo.h"
#include "DualTextureEffect.vk.mgfxo.h"
#include "EnvironmentMapEffect.vk.mgfxo.h"
#include "SkinnedEffect.vk.mgfxo.h"
#include "SpriteEffect.vk.mgfxo.h"
#include "mg_effect.h"

#include <cassert>
#include <cstring>
#include <string>
#include <vector>

namespace
{
    // Reported when nothing else determines a size. Any non-zero mode will do; this one is chosen
    // because it is unremarkable and matches the most common desktop default.
    constexpr mgint kDefaultWidth = 1920;
    constexpr mgint kDefaultHeight = 1080;

    // Mirrors the Metal backend's limits so a headless run exercises the same slot bookkeeping the
    // real backends do, rather than a more permissive contract that would hide an overflow.
    constexpr mgint kMaxTextureSlots = 16;
    constexpr mgint kMaxVertexBuffers = 16;
}

struct MGG_GraphicsSystem
{
    mgint adapterCount = 1;
};

struct MGG_GraphicsAdapter
{
    std::string name = "MonoGame Headless Adapter";
    std::vector<MGG_DisplayMode> modes;
};

struct MGG_GraphicsDevice
{
    MGG_GraphicsSystem* system = nullptr;
    MGG_GraphicsAdapter* adapter = nullptr;
    mgint backBufferWidth = kDefaultWidth;
    mgint backBufferHeight = kDefaultHeight;
    mgint frame = 0;
    MGG_ShaderPipelineDiagnostics diagnostics {};
};

// The state objects carry no behaviour, but they are still distinct allocations so the managed
// layer's create/destroy pairing is exercised and a double free shows up as one here too.
struct MGG_BlendState { int unused = 0; };
struct MGG_DepthStencilState { int unused = 0; };
struct MGG_RasterizerState { int unused = 0; };
struct MGG_SamplerState { int unused = 0; };
struct MGG_Shader { std::vector<mgbyte> bytecode; };
struct MGG_InputLayout { int unused = 0; };
struct MGG_OcclusionQuery { int unused = 0; };

struct MGG_Buffer
{
    std::vector<mgbyte> data;
};

struct MGG_Texture
{
    mgint width = 0;
    mgint height = 0;
    mgint depth = 0;
    std::vector<mgbyte> data;
};

// --- system and adapter ------------------------------------------------------------------------

MGG_GraphicsSystem* MGG_GraphicsSystem_Create()
{
    return new MGG_GraphicsSystem();
}

void MGG_GraphicsSystem_Destroy(MGG_GraphicsSystem* system)
{
    delete system;
}

MGG_GraphicsAdapter* MGG_GraphicsAdapter_Get(MGG_GraphicsSystem* system, mgint index)
{
    assert(system != nullptr);
    if (index != 0)
        return nullptr;

    static MGG_GraphicsAdapter adapter;
    return &adapter;
}

void MGG_GraphicsAdapter_GetInfo(MGG_GraphicsAdapter* adapter, MGG_GraphicsAdaptor_Info& info)
{
    assert(adapter != nullptr);
    memset(&info, 0, sizeof(info));

    info.DeviceName = (void*)adapter->name.c_str();
    info.Description = (void*)adapter->name.c_str();

    // Synthesised rather than queried from SDL: under the dummy video driver there are no real
    // display modes to enumerate, and an empty list leaves the managed side with a 0x0 resolution.
    if (adapter->modes.empty())
        adapter->modes.push_back(MGG_DisplayMode { MGSurfaceFormat::Color, kDefaultWidth, kDefaultHeight });

    info.DisplayModes = adapter->modes.data();
    info.DisplayModeCount = (mgint)adapter->modes.size();
    info.CurrentDisplayMode = adapter->modes[0];
}

// --- device ------------------------------------------------------------------------------------

MGG_GraphicsDevice* MGG_GraphicsDevice_Create(MGG_GraphicsSystem* system, MGG_GraphicsAdapter* adapter)
{
    auto device = new MGG_GraphicsDevice();
    device->system = system;
    device->adapter = adapter;
    return device;
}

void MGG_GraphicsDevice_Destroy(MGG_GraphicsDevice* device)
{
    delete device;
}

void MGG_GraphicsDevice_GetCaps(MGG_GraphicsDevice* device, MGG_GraphicsDevice_Caps& caps)
{
    caps.MaxTextureSlots = kMaxTextureSlots;
    caps.MaxVertexTextureSlots = kMaxTextureSlots;
    caps.MaxVertexBufferSlots = kMaxVertexBuffers;

    // Profile 80 is the Vulkan profile, which the Metal backend also reports. Matching it means a
    // headless build consumes exactly the same compiled effect content as the real desktop
    // backends, so nothing about shader loading changes between them.
    caps.ShaderProfile = 80;
}

void MGG_GraphicsDevice_GetShaderPipelineDiagnostics(MGG_GraphicsDevice* device, MGG_ShaderPipelineDiagnostics& diagnostics)
{
    assert(device != nullptr);
    diagnostics = device->diagnostics;
}

void MGG_GraphicsDevice_ResetShaderPipelineDiagnostics(MGG_GraphicsDevice* device)
{
    assert(device != nullptr);
    device->diagnostics = MGG_ShaderPipelineDiagnostics {};
}

mgbool MGG_GraphicsDevice_PrewarmCurrentPipeline(MGG_GraphicsDevice* device, MGPrimitiveType primitiveType)
{
    // There is no pipeline to warm, and reporting failure would make callers retry pointlessly.
    return true;
}

mgint MGG_GraphicsDevice_GetPipelineCacheDataSize(MGG_GraphicsDevice* device)
{
    return 0;
}

mgbool MGG_GraphicsDevice_GetPipelineCacheData(MGG_GraphicsDevice* device, mgbyte* data, mgint dataBytes)
{
    return false;
}

MGPipelineCacheStatus MGG_GraphicsDevice_ImportPipelineCache(MGG_GraphicsDevice* device, mgbyte* data, mgint dataBytes)
{
    // Unsupported rather than Success: there is no cache to import into, and claiming success would
    // let a caller believe a warm cache is in place.
    return MGPipelineCacheStatus::Unsupported;
}

void MGG_GraphicsDevice_ResizeSwapchain(
    MGG_GraphicsDevice* device,
    void* nativeWindowHandle,
    mgint width,
    mgint height,
    MGSurfaceFormat color,
    MGDepthFormat depth,
    mgint multiSampleCount,
    mgint syncInterval)
{
    assert(device != nullptr);

    // Zero arrives when the window is still unrealized, which is the normal case under the dummy
    // video driver. Keeping the previous size avoids handing back a 0x0 back buffer.
    if (width > 0) device->backBufferWidth = width;
    if (height > 0) device->backBufferHeight = height;
}

void MGG_GraphicsDevice_GetBackBufferSize(MGG_GraphicsDevice* device, mgint& width, mgint& height)
{
    assert(device != nullptr);
    width = device->backBufferWidth;
    height = device->backBufferHeight;
}

mgint MGG_GraphicsDevice_GetDrawableSizeMismatchCount(MGG_GraphicsDevice* device)
{
    // The drawable can never disagree with the back buffer here, since both are bookkeeping.
    return 0;
}

mgint MGG_GraphicsDevice_BeginFrame(MGG_GraphicsDevice* device)
{
    assert(device != nullptr);
    return device->frame;
}

void MGG_GraphicsDevice_Clear(MGG_GraphicsDevice* device, MGClearOptions options, Vector4& color, mgfloat depth, mgint stencil)
{
}

void MGG_GraphicsDevice_Present(MGG_GraphicsDevice* device, mgint currentFrame, mgint syncInterval)
{
    assert(device != nullptr);
    ++device->frame;
}

void MGG_GraphicsDevice_SetBlendState(MGG_GraphicsDevice* device, MGG_BlendState* state, mgfloat factorR, mgfloat factorG, mgfloat factorB, mgfloat factorA) {}
void MGG_GraphicsDevice_SetDepthStencilState(MGG_GraphicsDevice* device, MGG_DepthStencilState* state) {}
void MGG_GraphicsDevice_SetRasterizerState(MGG_GraphicsDevice* device, MGG_RasterizerState* state) {}

void MGG_GraphicsDevice_GetTitleSafeArea(mgint& x, mgint& y, mgint& width, mgint& height)
{
    x = 0;
    y = 0;
    width = kDefaultWidth;
    height = kDefaultHeight;
}

void MGG_GraphicsDevice_SetViewport(MGG_GraphicsDevice* device, mgint x, mgint y, mgint width, mgint height, mgfloat minDepth, mgfloat maxDepth) {}
void MGG_GraphicsDevice_SetScissorRectangle(MGG_GraphicsDevice* device, mgint x, mgint y, mgint width, mgint height) {}
void MGG_GraphicsDevice_SetRenderTargets(MGG_GraphicsDevice* device, MGG_Texture** targets, mgint* arraySlices, mgint count) {}
void MGG_GraphicsDevice_SetConstantBuffer(MGG_GraphicsDevice* device, MGShaderStage stage, mgint slot, MGG_Buffer* buffer) {}
void MGG_GraphicsDevice_SetTexture(MGG_GraphicsDevice* device, MGShaderStage stage, mgint slot, MGG_Texture* texture) {}
void MGG_GraphicsDevice_SetSamplerState(MGG_GraphicsDevice* device, MGShaderStage stage, mgint slot, MGG_SamplerState* state) {}
void MGG_GraphicsDevice_SetIndexBuffer(MGG_GraphicsDevice* device, MGIndexElementSize size, MGG_Buffer* buffer) {}
void MGG_GraphicsDevice_SetVertexBuffer(MGG_GraphicsDevice* device, mgint slot, MGG_Buffer* buffer, mgint vertexOffset) {}
void MGG_GraphicsDevice_SetShader(MGG_GraphicsDevice* device, MGShaderStage stage, MGG_Shader* shader) {}
void MGG_GraphicsDevice_SetInputLayout(MGG_GraphicsDevice* device, MGG_InputLayout* layout) {}

void MGG_GraphicsDevice_Draw(MGG_GraphicsDevice* device, MGPrimitiveType primitiveType, mgint vertexStart, mgint vertexCount) {}
void MGG_GraphicsDevice_DrawIndexed(MGG_GraphicsDevice* device, MGPrimitiveType primitiveType, mgint primitiveCount, mgint indexStart, mgint vertexStart) {}
void MGG_GraphicsDevice_DrawIndexedInstanced(MGG_GraphicsDevice* device, MGPrimitiveType primitiveType, mgint primitiveCount, mgint indexStart, mgint vertexStart, mgint instanceCount) {}
void MGG_GraphicsDevice_ResolveRenderTargets(MGG_GraphicsDevice* device) {}

void MGG_GraphicsDevice_GetBackBufferData(
    MGG_GraphicsDevice* device,
    mgint x,
    mgint y,
    mgint width,
    mgint height,
    void* data,
    mgint count,
    mgint dataBytes)
{
    // Blank, not uninitialised. Nothing was drawn, and handing back stale heap contents would let a
    // screenshot-style test appear to capture something.
    if (data != nullptr && dataBytes > 0)
        memset(data, 0, (size_t)dataBytes);
}

// --- state objects -------------------------------------------------------------------------------

MGG_BlendState* MGG_BlendState_Create(MGG_GraphicsDevice* device, MGG_BlendState_Info* infos) { return new MGG_BlendState(); }
void MGG_BlendState_Destroy(MGG_GraphicsDevice* device, MGG_BlendState* state) { delete state; }

MGG_DepthStencilState* MGG_DepthStencilState_Create(MGG_GraphicsDevice* device, MGG_DepthStencilState_Info* info) { return new MGG_DepthStencilState(); }
void MGG_DepthStencilState_Destroy(MGG_GraphicsDevice* device, MGG_DepthStencilState* state) { delete state; }

MGG_RasterizerState* MGG_RasterizerState_Create(MGG_GraphicsDevice* device, MGG_RasterizerState_Info* info) { return new MGG_RasterizerState(); }
void MGG_RasterizerState_Destroy(MGG_GraphicsDevice* device, MGG_RasterizerState* state) { delete state; }

MGG_SamplerState* MGG_SamplerState_Create(MGG_GraphicsDevice* device, MGG_SamplerState_Info* info) { return new MGG_SamplerState(); }
void MGG_SamplerState_Destroy(MGG_GraphicsDevice* device, MGG_SamplerState* state) { delete state; }

// --- buffers -------------------------------------------------------------------------------------

MGG_Buffer* MGG_Buffer_Create(MGG_GraphicsDevice* device, MGBufferType type, mgbool dynamic, mgint sizeInBytes)
{
    auto buffer = new MGG_Buffer();
    if (sizeInBytes > 0)
        buffer->data.resize((size_t)sizeInBytes, 0);
    return buffer;
}

void MGG_Buffer_Destroy(MGG_GraphicsDevice* device, MGG_Buffer* buffer)
{
    delete buffer;
}

void MGG_Buffer_SetData(
    MGG_GraphicsDevice* device,
    MGG_Buffer*& buffer,
    mgint offset,
    mgbyte* data,
    mgint elementCount,
    mgint vertexStride,
    mgint elementSizeInBytes,
    mgbool discard)
{
    // Retained so GetData round-trips what was written. Cheap, and it turns a whole class of
    // "did the upload path run?" question into something a test can answer.
    if (buffer == nullptr || data == nullptr)
        return;

    const size_t bytes = (size_t)elementCount * (size_t)elementSizeInBytes;
    if (bytes == 0)
        return;

    const size_t end = (size_t)offset + bytes;
    if (buffer->data.size() < end)
        buffer->data.resize(end, 0);

    memcpy(buffer->data.data() + offset, data, bytes);
}

void MGG_Buffer_GetData(
    MGG_GraphicsDevice* device,
    MGG_Buffer* buffer,
    mgint offset,
    mgbyte* data,
    mgint dataCount,
    mgint dataBytes,
    mgint dataStride)
{
    if (data == nullptr || dataBytes <= 0)
        return;

    memset(data, 0, (size_t)dataBytes);
    if (buffer == nullptr)
        return;

    const size_t available = buffer->data.size() > (size_t)offset ? buffer->data.size() - (size_t)offset : 0;
    const size_t copy = available < (size_t)dataBytes ? available : (size_t)dataBytes;
    if (copy > 0)
        memcpy(data, buffer->data.data() + offset, copy);
}

// --- textures ------------------------------------------------------------------------------------

namespace
{
    MGG_Texture* CreateTexture(mgint width, mgint height, mgint depth)
    {
        auto texture = new MGG_Texture();
        texture->width = width > 0 ? width : 1;
        texture->height = height > 0 ? height : 1;
        texture->depth = depth > 0 ? depth : 1;

        // Four bytes per texel regardless of the declared format. The backend never samples, so the
        // only thing the allocation has to do is be large enough for SetData/GetData to round-trip.
        texture->data.resize((size_t)texture->width * texture->height * texture->depth * 4, 0);
        return texture;
    }
}

MGG_Texture* MGG_Texture_Create(
    MGG_GraphicsDevice* device,
    MGTextureType type,
    MGSurfaceFormat format,
    mgint width,
    mgint height,
    mgint depth,
    mgint mipmaps,
    mgint slices)
{
    return CreateTexture(width, height, depth);
}

MGG_Texture* MGG_RenderTarget_Create(
    MGG_GraphicsDevice* device,
    MGTextureType type,
    MGSurfaceFormat format,
    mgint width,
    mgint height,
    mgint depth,
    mgint mipmaps,
    mgint slices,
    MGDepthFormat depthFormat,
    mgint multiSampleCount,
    MGRenderTargetUsage usage)
{
    return CreateTexture(width, height, depth);
}

void MGG_Texture_Destroy(MGG_GraphicsDevice* device, MGG_Texture* texture)
{
    delete texture;
}

void MGG_Texture_SetData(
    MGG_GraphicsDevice* device,
    MGG_Texture* texture,
    mgint level,
    mgint slice,
    mgint x,
    mgint y,
    mgint z,
    mgint width,
    mgint height,
    mgint depth,
    mgbyte* data,
    mgint dataBytes)
{
    if (texture == nullptr || data == nullptr || dataBytes <= 0)
        return;

    // Only a full-surface upload at mip 0 is stored. A sub-rect or mip upload would need real
    // layout arithmetic to round-trip correctly, and storing it at the wrong offset would be worse
    // than not storing it: GetData would return confidently misplaced texels.
    if (level != 0 || x != 0 || y != 0 || z != 0)
        return;

    const size_t copy = texture->data.size() < (size_t)dataBytes ? texture->data.size() : (size_t)dataBytes;
    memcpy(texture->data.data(), data, copy);
}

void MGG_Texture_GetData(
    MGG_GraphicsDevice* device,
    MGG_Texture* texture,
    mgint level,
    mgint slice,
    mgint x,
    mgint y,
    mgint z,
    mgint width,
    mgint height,
    mgint depth,
    mgbyte* data,
    mgint dataBytes)
{
    if (data == nullptr || dataBytes <= 0)
        return;

    memset(data, 0, (size_t)dataBytes);
    if (texture == nullptr || level != 0 || x != 0 || y != 0 || z != 0)
        return;

    const size_t copy = texture->data.size() < (size_t)dataBytes ? texture->data.size() : (size_t)dataBytes;
    memcpy(data, texture->data.data(), copy);
}

// --- shaders and layout --------------------------------------------------------------------------

MGG_InputLayout* MGG_InputLayout_Create(
    MGG_GraphicsDevice* device,
    MGG_Shader* vertexShader,
    mgint* strides,
    mgint streamCount,
    MGG_InputElement* elements,
    mgint elementCount)
{
    return new MGG_InputLayout();
}

void MGG_InputLayout_Destroy(MGG_GraphicsDevice* device, MGG_InputLayout* layout)
{
    delete layout;
}

MGG_Shader* MGG_Shader_Create(MGG_GraphicsDevice* device, MGShaderStage stage, mgbyte* bytecode, mgint sizeInBytes)
{
    auto shader = new MGG_Shader();

    // Kept so the diagnostics count reflects real work having been requested, and so a caller that
    // inspects the handle sees the bytecode it supplied rather than an empty one.
    if (bytecode != nullptr && sizeInBytes > 0)
        shader->bytecode.assign(bytecode, bytecode + sizeInBytes);

    if (device != nullptr)
        ++device->diagnostics.ShaderCreationCount;

    return shader;
}

void MGG_Shader_Destroy(MGG_GraphicsDevice* device, MGG_Shader* shader)
{
    delete shader;
}

// --- occlusion queries ---------------------------------------------------------------------------

MGG_OcclusionQuery* MGG_OcclusionQuery_Create(MGG_GraphicsDevice* device) { return new MGG_OcclusionQuery(); }
void MGG_OcclusionQuery_Destroy(MGG_GraphicsDevice* device, MGG_OcclusionQuery* query) { delete query; }
void MGG_OcclusionQuery_Begin(MGG_GraphicsDevice* device, MGG_OcclusionQuery* query) {}
void MGG_OcclusionQuery_End(MGG_GraphicsDevice* device, MGG_OcclusionQuery* query) {}

mgbyte MGG_OcclusionQuery_GetResult(MGG_GraphicsDevice* device, MGG_OcclusionQuery* query, mgint& pixelCount)
{
    // Complete, with nothing drawn. Reporting "not ready" would hang a caller that polls.
    pixelCount = 0;
    return 1;
}
