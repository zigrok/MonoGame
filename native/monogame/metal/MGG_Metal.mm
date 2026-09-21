// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.
//
// Native Metal graphics backend (macOS/iOS). Renders straight to a CAMetalLayer — no MoltenVK.
// Reuses the Vulkan backend's compiled effect blobs (SPIR-V + reflection header) and translates
// SPIR-V -> MSL at runtime via SPIRV-Cross. See METAL-BACKEND-PLAN.md.
//
// Structure mirrors the DirectX12 backend (single-god-object device with lazily-applied draw
// state) and the Vulkan backend's behaviour (buffer discard/orphan, reflection-header parse).
// Compiled with ARC (see premake metal()).

#import <Metal/Metal.h>
#import <QuartzCore/CAMetalLayer.h>
#import <QuartzCore/CABase.h> // CACurrentMediaTime (perf diagnostics)
#import <AppKit/AppKit.h>      // NSView/NSWindow/NSScreen geometry (perf diagnostics)

#if defined(MG_SDL3)
#include <SDL3/SDL.h>
#include <SDL3/SDL_metal.h>
#elif defined(MG_SDL2)
#include <SDL.h>
#include <SDL_metal.h>
#else
#error "Metal backend requires an SDL platform layer (MG_SDL2 or MG_SDL3)."
#endif

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdint>
#include <vector>
#include <unordered_map>
#include <string>

#include "api_MGG.h"
#include "MGMetalShaderTranspiler.h"
#include "MGMetalShaderPayload.h"

// Shared, backend-agnostic compiled effects (same SPIR-V blobs the Vulkan backend consumes).
#include "AlphaTestEffect.vk.mgfxo.h"
#include "BasicEffect.vk.mgfxo.h"
#include "DualTextureEffect.vk.mgfxo.h"
#include "EnvironmentMapEffect.vk.mgfxo.h"
#include "SkinnedEffect.vk.mgfxo.h"
#include "SpriteEffect.vk.mgfxo.h"
// Defines MGG_EffectResource_GetBytecode over the *_vk_mgfxo arrays above.
#include "mg_effect.h"

#include "GraphicsEnums.h"

using namespace mgmetal;

// ---------------------------------------------------------------------------------------------
// Constants and small helpers
// ---------------------------------------------------------------------------------------------

static const int MAX_TEXTURE_SLOTS = 16;
static const int MAX_VERTEX_BUFFERS = 16;
static const int NUM_STAGES = 2; // Vertex, Pixel
static const int MAX_FRAMES_IN_FLIGHT = 3;

// Metal buffer-index convention for the translated shaders:
//   buffer(0)               -> the constant buffer (cbuffer _MG_Globals), both stages
//   buffer(1 + slot)        -> vertex stream 'slot' (via the vertex descriptor), vertex stage only
// This keeps vertex streams from colliding with the cbuffer. Textures/samplers use slot index
// directly: texture(slot), sampler(slot). All pinned via SPIRV-Cross add_msl_resource_binding.
static const int MG_MTL_VBO_BASE = 1;

// The reused SPIR-V was compiled with -fvk-invert-y (Vulkan's Y-down NDC). Metal's NDC is Y-up
// (like D3D), so we cancel that baked-in flip with SPIRV-Cross flip_vert_y. If the image ever
// comes out upside-down, flip this one flag.
#define MGMTL_STRINGIFY_IMPL(value) #value
#define MGMTL_STRINGIFY(value) MGMTL_STRINGIFY_IMPL(value)

struct MGMTL_TranslationCacheEntry
{
    std::string source;
    std::string entryPoint;
    id<MTLLibrary> library = nil;
};

struct MGMTL_PipelineCacheEntry
{
    id<MTLRenderPipelineState> pipeline = nil;
};

static inline void MGMTL_Log(const char* fmt, ...)
{
    va_list args;
    va_start(args, fmt);
    vfprintf(stderr, fmt, args);
    va_end(args);
    fprintf(stderr, "\n");
}

static inline uint64_t MGMTL_HashBytes(const void* data, size_t len, uint64_t h = 1469598103934665603ULL)
{
    const uint8_t* p = (const uint8_t*)data;
    while (len--) { h ^= *p++; h *= 1099511628211ULL; }
    return h;
}
template <typename T> static inline uint64_t MGMTL_HashValue(const T& v, uint64_t h) { return MGMTL_HashBytes(&v, sizeof(T), h); }

// ---------------------------------------------------------------------------------------------
// Handle structs
// ---------------------------------------------------------------------------------------------

struct MGG_GraphicsAdapter
{
    id<MTLDevice> device = nil;
    MGG_DisplayMode current = { MGSurfaceFormat::Color, 0, 0 };
    std::vector<MGG_DisplayMode> modes;
    std::string name;
};

struct MGG_GraphicsSystem
{
    std::vector<MGG_GraphicsAdapter*> adapters;
};

struct MGG_Buffer
{
    MGBufferType type = MGBufferType::Vertex;
    int size = 0;
    // Constant buffers are CPU-side "push" buffers (uploaded per-draw via setVertexBytes); vertex/
    // index buffers are real MTLBuffers with shared storage.
    id<MTLBuffer> buffer = nil;
    std::vector<uint8_t> push;
    bool isConstant = false;
};

struct MGG_Texture
{
    MGTextureType type = MGTextureType::_2D;
    MGSurfaceFormat format = MGSurfaceFormat::Color;
    int width = 0, height = 0, depth = 1, mipmaps = 1, slices = 1;
    int multiSampleCount = 1;
    bool isTarget = false;

    id<MTLTexture> texture = nil;     // the sampleable / resolve texture
    id<MTLTexture> msaaTexture = nil; // multisample color (targets with MSAA)

    MGDepthFormat depthFormat = MGDepthFormat::None;
    id<MTLTexture> depthTexture = nil; // owned depth for render targets
};

struct MGG_Shader
{
    MGShaderStage stage = MGShaderStage::Vertex;
    id<MTLFunction> function = nil;
    id<MTLLibrary> library = nil;

    uint32_t uniformSlots = 0;
    uint32_t textureSlots = 0;
    uint32_t samplerSlots = 0;
    MGTextureType textureTypes[MAX_TEXTURE_SLOTS] = {};
    int maxTextureSlot = -1;
    int maxSamplerSlot = -1;

    uint64_t id = 0;
};

struct MGG_BlendState
{
    MGG_BlendState_Info infos[4];
    bool blending = false;
    uint64_t id = 0;
};

struct MGG_DepthStencilState
{
    id<MTLDepthStencilState> state = nil;
    int referenceStencil = 0;
    uint64_t id = 0;
};

struct MGG_RasterizerState
{
    MGCullMode cullMode = MGCullMode::None;
    MGFillMode fillMode = MGFillMode::Solid;
    bool scissorTestEnable = false;
    float depthBias = 0.0f;
    float slopeScaleDepthBias = 0.0f;
    bool multiSampleAntiAlias = true;
    uint64_t id = 0;
};

struct MGG_SamplerState
{
    id<MTLSamplerState> state = nil;
    MGG_SamplerState_Info info;
    uint64_t id = 0;
};

struct MGG_InputLayout
{
    MTLVertexDescriptor* descriptor = nil;
    uint64_t id = 0;
};

struct MGG_OcclusionQuery
{
    int index = -1;
    mgint pixelCount = 0;
    bool inBeginEnd = false;
};

struct MGG_GraphicsDevice
{
    id<MTLDevice> mtlDevice = nil;
    id<MTLCommandQueue> queue = nil;

    // Swapchain / surface.
    SDL_Window* window = nullptr;
    SDL_MetalView metalView = nullptr;
    CAMetalLayer* layer = nil;
    MTLPixelFormat backbufferFormat = MTLPixelFormatBGRA8Unorm; // CAMetalLayer requires BGRA
    MTLPixelFormat backbufferDepthFormat = MTLPixelFormatInvalid;
    int backbufferWidth = 0, backbufferHeight = 0;
    int drawableSizeMismatchCount = 0;
    bool validateDrawableSize = false;
    int multiSampleCount = 1;
    int syncInterval = 1;

    id<MTLTexture> backbufferDepth = nil;
    id<MTLTexture> backbufferMsaa = nil;

    // Per-frame command recording.
    dispatch_semaphore_t inFlight = nil;
    id<MTLCommandBuffer> commandBuffer = nil;
    id<CAMetalDrawable> drawable = nil;
    id<MTLRenderCommandEncoder> encoder = nil;
    bool backbufferColorInitialized = false;
    bool backbufferDepthInitialized = false;
    bool backbufferStencilInitialized = false;
    uint64_t frame = 0;
    int begin_frame_index = 0;

    // Bound render targets.
    bool usingBackbuffer = true;
    MGG_Texture* colorTargets[8] = {};
    int colorTargetSlices[8] = {};
    int colorTargetCount = 0;
    MGG_Texture* depthTarget = nullptr;

    // Pending clears (deferred to render-pass begin as load actions).
    bool clearColor = false, clearDepth = false, clearStencil = false;
    MTLClearColor clearColorValue = MTLClearColorMake(0, 0, 0, 1);
    double clearDepthValue = 1.0;
    uint32_t clearStencilValue = 0;

    // Pending pipeline / draw state.
    MGG_Shader* shaders[NUM_STAGES] = {};
    MGG_BlendState* blendState = nullptr;
    MGG_DepthStencilState* depthStencilState = nullptr;
    MGG_RasterizerState* rasterizerState = nullptr;
    MGG_InputLayout* inputLayout = nullptr;
    MGG_InputLayout* boundLayout = nullptr;

    float blendFactor[4] = { 1, 1, 1, 1 };

    MTLViewport viewport = { 0, 0, 0, 0, 0, 1 };
    MTLScissorRect scissor = { 0, 0, 0, 0 };
    bool scissorSet = false;

    MGG_Buffer* vertexBuffers[MAX_VERTEX_BUFFERS] = {};
    uint32_t vertexOffsets[MAX_VERTEX_BUFFERS] = {};
    MGG_Buffer* indexBuffer = nullptr;
    MGIndexElementSize indexBufferSize = MGIndexElementSize::SixteenBits;

    MGG_Buffer* uniforms[NUM_STAGES] = {};
    MGG_Texture* textures[NUM_STAGES][MAX_TEXTURE_SLOTS] = {};
    MGG_SamplerState* samplers[NUM_STAGES][MAX_TEXTURE_SLOTS] = {};

    // Dirty flags: applied lazily at draw time onto the current encoder.
    bool pipelineDirty = true;
    bool viewportDirty = true;
    bool scissorDirty = true;
    bool rasterDirty = true;
    bool blendFactorDirty = true;
    bool depthStencilDirty = true;
    bool indexDirty = true;
    uint32_t vertexDirty = 0xFFFFFFFF;
    bool bindingsDirty = true; // textures/samplers/uniforms

    // Pipeline cache.
    std::unordered_map<uint64_t, MGMTL_PipelineCacheEntry> pipelines;
    std::unordered_map<std::string, MGMTL_TranslationCacheEntry> translations;

    // Fallbacks for used-but-unbound slots.
    id<MTLTexture> nullTexture = nil;
    id<MTLSamplerState> nullSampler = nil;

    // Deferred destruction / discard recycling (kept a few frames for in-flight safety; ARC frees).
    std::vector<MGG_Buffer*> discarded[MAX_FRAMES_IN_FLIGHT];

    // Occlusion queries.
    id<MTLBuffer> visibilityBuffer = nil;
    int visibilityCount = 0;

    uint64_t nextObjectId = 1;

    // Diagnostics (opt-in via MG_METAL_PERF=1). Used to catch the transient countdown-start magenta
    // flash: log pipeline-compile stalls, and scan each presented backbuffer for magenta / no-draw.
    bool perf = false;
    bool drewThisFrame = false;
    uint64_t perfFrame = 0;
    uint32_t lastWindowFlags = 0; // detect fullscreen toggles for geometry logging
    double lastCompileMs = 0.0;
    uint64_t lastCompileFrame = 0;
    id<MTLBuffer> perfStaging = nil;

    uint64_t shaderCreationCount = 0;
    uint64_t pipelineCacheHits = 0;
    uint64_t pipelineCacheMisses = 0;
    uint64_t pipelineCreationCount = 0;
    uint64_t pipelineCacheImports = 0;
    uint64_t pipelineCacheRejections = 0;
    MGPipelineCacheStatus lastPipelineCacheStatus = MGPipelineCacheStatus::None;
    uint64_t runtimeTranslationCount = 0;
    double shaderCreationMilliseconds = 0.0;
    double pipelineCreationMilliseconds = 0.0;
    double runtimeTranslationMilliseconds = 0.0;
};

// ---------------------------------------------------------------------------------------------
// Forward declarations of internal helpers
// ---------------------------------------------------------------------------------------------

static void MGMTL_PrepareNextFrame(MGG_GraphicsDevice* device);
static bool MGMTL_EnsureEncoder(MGG_GraphicsDevice* device);
static void MGMTL_EndEncoder(MGG_GraphicsDevice* device);
static void MGMTL_ApplyState(MGG_GraphicsDevice* device, MGPrimitiveType primitiveType);

// ===========================================================================================
// System / Adapter
// ===========================================================================================

MGG_GraphicsSystem* MGG_GraphicsSystem_Create()
{
    auto system = new MGG_GraphicsSystem();

    @autoreleasepool
    {
        NSArray<id<MTLDevice>>* devices = nil;
#if TARGET_OS_OSX
        devices = MTLCopyAllDevices();
#endif
        if (devices == nil || devices.count == 0)
        {
            id<MTLDevice> def = MTLCreateSystemDefaultDevice();
            if (def != nil)
                devices = @[ def ];
        }

        for (id<MTLDevice> dev in devices)
        {
            auto adapter = new MGG_GraphicsAdapter();
            adapter->device = dev;
            adapter->name = std::string([dev.name UTF8String] ? [dev.name UTF8String] : "Metal Device");
            adapter->current = { MGSurfaceFormat::Color, 0, 0 };
            system->adapters.push_back(adapter);
        }
    }

    if (system->adapters.empty())
        MGMTL_Log("MGG_GraphicsSystem_Create: no Metal devices found!");

    return system;
}

void MGG_GraphicsSystem_Destroy(MGG_GraphicsSystem* system)
{
    if (!system) return;
    for (auto a : system->adapters)
    {
        a->device = nil;
        delete a;
    }
    delete system;
}

MGG_GraphicsAdapter* MGG_GraphicsAdapter_Get(MGG_GraphicsSystem* system, mgint index)
{
    assert(system);
    // The managed side enumerates adapters by calling Get(0), Get(1), ... until it returns null,
    // so out-of-range MUST return nullptr (not a fallback) or enumeration never terminates.
    if (index < 0 || index >= (mgint)system->adapters.size())
        return nullptr;
    return system->adapters[index];
}

void MGG_GraphicsAdapter_GetInfo(MGG_GraphicsAdapter* adapter, MGG_GraphicsAdaptor_Info& info)
{
    assert(adapter);
    memset(&info, 0, sizeof(info));
    info.DeviceName = (void*)adapter->name.c_str();
    info.Description = (void*)adapter->name.c_str();
    info.SubSystemId = 0;
    info.MonitorHandle = 0;

    // Populate display modes + the current desktop mode from SDL (same source the Vulkan backend uses).
    // The managed side reads CurrentDisplayMode to build the resolution list; leaving it 0x0 makes the
    // game pick a 0x0 resolution and crash creating render targets.
#if defined(MG_SDL3)
    SDL_DisplayID displayID = SDL_GetPrimaryDisplay();
    if (adapter->modes.empty())
    {
        int numModes = 0;
        SDL_DisplayMode** modes = SDL_GetFullscreenDisplayModes(displayID, &numModes);
        if (modes)
        {
            for (int i = 0; i < numModes; i++)
            {
                MGG_DisplayMode dm { MGSurfaceFormat::Color, modes[i]->w, modes[i]->h };
                bool found = false;
                for (auto& m : adapter->modes) if (m.width == dm.width && m.height == dm.height) { found = true; break; }
                if (!found) adapter->modes.push_back(dm);
            }
            SDL_free(modes);
        }
    }
    const SDL_DisplayMode* cur = SDL_GetCurrentDisplayMode(displayID);
    if (!cur) cur = SDL_GetDesktopDisplayMode(displayID);
    adapter->current = cur ? MGG_DisplayMode{ MGSurfaceFormat::Color, cur->w, cur->h }
                           : MGG_DisplayMode{ MGSurfaceFormat::Color, 1920, 1080 };
#else
    int displayIndex = 0;
    int numModes = SDL_GetNumDisplayModes(displayIndex);
    if (adapter->modes.empty() && numModes > 0)
    {
        for (int i = 0; i < numModes; i++)
        {
            SDL_DisplayMode mode;
            if (SDL_GetDisplayMode(displayIndex, i, &mode) == 0)
            {
                MGG_DisplayMode dm { MGSurfaceFormat::Color, mode.w, mode.h };
                bool found = false;
                for (auto& m : adapter->modes) if (m.width == dm.width && m.height == dm.height) { found = true; break; }
                if (!found) adapter->modes.push_back(dm);
            }
        }
    }
    SDL_DisplayMode cur;
    if (SDL_GetCurrentDisplayMode(displayIndex, &cur) == 0)
        adapter->current = MGG_DisplayMode{ MGSurfaceFormat::Color, cur.w, cur.h };
    else if (SDL_GetDesktopDisplayMode(displayIndex, &cur) == 0)
        adapter->current = MGG_DisplayMode{ MGSurfaceFormat::Color, cur.w, cur.h };
    else
        adapter->current = MGG_DisplayMode{ MGSurfaceFormat::Color, 1920, 1080 };
#endif

    info.CurrentDisplayMode = adapter->current;
    info.DisplayModes = adapter->modes.empty() ? nullptr : adapter->modes.data();
    info.DisplayModeCount = (mgint)adapter->modes.size();
}

// ===========================================================================================
// Device create / destroy / caps
// ===========================================================================================

static void MGMTL_CreateNullResources(MGG_GraphicsDevice* device)
{
    MTLTextureDescriptor* td = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm
                                                                                  width:1 height:1 mipmapped:NO];
    td.usage = MTLTextureUsageShaderRead;
    td.storageMode = device->mtlDevice.hasUnifiedMemory ? MTLStorageModeShared : MTLStorageModeManaged;
    device->nullTexture = [device->mtlDevice newTextureWithDescriptor:td];
    uint8_t white[4] = { 255, 255, 255, 255 };
    [device->nullTexture replaceRegion:MTLRegionMake2D(0, 0, 1, 1) mipmapLevel:0 withBytes:white bytesPerRow:4];

    MTLSamplerDescriptor* sd = [[MTLSamplerDescriptor alloc] init];
    device->nullSampler = [device->mtlDevice newSamplerStateWithDescriptor:sd];
}

MGG_GraphicsDevice* MGG_GraphicsDevice_Create(MGG_GraphicsSystem* system, MGG_GraphicsAdapter* adapter)
{
    assert(system);
    auto device = new MGG_GraphicsDevice();

    device->mtlDevice = (adapter && adapter->device) ? adapter->device : MTLCreateSystemDefaultDevice();
    if (device->mtlDevice == nil)
    {
        MGMTL_Log("MGG_GraphicsDevice_Create: failed to obtain an MTLDevice.");
        delete device;
        return nullptr;
    }

    device->queue = [device->mtlDevice newCommandQueue];
    device->inFlight = dispatch_semaphore_create(MAX_FRAMES_IN_FLIGHT);
    device->perf = getenv("MG_METAL_PERF") != nullptr;
    device->validateDrawableSize = getenv("MG_METAL_VALIDATE_DRAWABLE_SIZE") != nullptr;
    if (device->perf)
        MGMTL_Log("[metal-perf] diagnostics ON (MG_METAL_PERF): logging pipeline compiles + scanning each present for magenta");

    MGMTL_CreateNullResources(device);

    return device;
}

void MGG_GraphicsDevice_Destroy(MGG_GraphicsDevice* device)
{
    if (!device) return;

    // Drain in-flight work.
    if (device->commandBuffer != nil)
    {
        MGMTL_EndEncoder(device);
    }
    // Let the queue finish.
    @autoreleasepool
    {
        id<MTLCommandBuffer> cb = [device->queue commandBuffer];
        [cb commit];
        [cb waitUntilCompleted];
    }

    device->pipelines.clear();
    device->layer = nil;
    if (device->metalView) { SDL_Metal_DestroyView(device->metalView); device->metalView = nullptr; }
    delete device;
}

void MGG_GraphicsDevice_GetCaps(MGG_GraphicsDevice* device, MGG_GraphicsDevice_Caps& caps)
{
    caps.MaxTextureSlots = MAX_TEXTURE_SLOTS;
    caps.MaxVertexTextureSlots = MAX_TEXTURE_SLOTS;
    caps.MaxVertexBufferSlots = MAX_VERTEX_BUFFERS;
    // The Metal backend consumes the Vulkan shader profile (FormatId 80). The managed Effect loader
    // checks each MGFX blob's profile byte against this, so it must match the Vulkan content.
    caps.ShaderProfile = 80;
}

void MGG_GraphicsDevice_GetShaderPipelineDiagnostics(MGG_GraphicsDevice* device, MGG_ShaderPipelineDiagnostics& diagnostics)
{
    assert(device != nullptr);
    diagnostics = {};
    diagnostics.ShaderCreationCount = device->shaderCreationCount;
    diagnostics.PipelineCacheHits = device->pipelineCacheHits;
    diagnostics.PipelineCacheMisses = device->pipelineCacheMisses;
    diagnostics.PipelineCreationCount = device->pipelineCreationCount;
    diagnostics.PipelineCacheImports = device->pipelineCacheImports;
    diagnostics.PipelineCacheRejections = device->pipelineCacheRejections;
    diagnostics.LastPipelineCacheStatus = device->lastPipelineCacheStatus;
    diagnostics.RuntimeTranslationCount = device->runtimeTranslationCount;
    diagnostics.ShaderCreationMilliseconds = device->shaderCreationMilliseconds;
    diagnostics.PipelineCreationMilliseconds = device->pipelineCreationMilliseconds;
    diagnostics.RuntimeTranslationMilliseconds = device->runtimeTranslationMilliseconds;
}

void MGG_GraphicsDevice_ResetShaderPipelineDiagnostics(MGG_GraphicsDevice* device)
{
    assert(device != nullptr);
    device->shaderCreationCount = 0;
    device->pipelineCacheHits = 0;
    device->pipelineCacheMisses = 0;
    device->pipelineCreationCount = 0;
    device->pipelineCacheImports = 0;
    device->pipelineCacheRejections = 0;
    device->lastPipelineCacheStatus = MGPipelineCacheStatus::None;
    device->runtimeTranslationCount = 0;
    device->shaderCreationMilliseconds = 0.0;
    device->pipelineCreationMilliseconds = 0.0;
    device->runtimeTranslationMilliseconds = 0.0;
}

void MGG_GraphicsDevice_GetTitleSafeArea(mgint& x, mgint& y, mgint& width, mgint& height)
{
    // Desktop: no title-safe inset.
}

// ===========================================================================================
// Swapchain / frame lifecycle
// ===========================================================================================

static void MGMTL_CreateBackbufferAuxTargets(MGG_GraphicsDevice* device)
{
    device->backbufferDepth = nil;
    device->backbufferMsaa = nil;
    device->backbufferColorInitialized = false;
    device->backbufferDepthInitialized = false;
    device->backbufferStencilInitialized = false;

    const int w = device->backbufferWidth;
    const int h = device->backbufferHeight;
    if (w <= 0 || h <= 0) return;

    if (device->backbufferDepthFormat != MTLPixelFormatInvalid)
    {
        MTLTextureDescriptor* dd = [[MTLTextureDescriptor alloc] init];
        dd.textureType = device->multiSampleCount > 1 ? MTLTextureType2DMultisample : MTLTextureType2D;
        dd.pixelFormat = device->backbufferDepthFormat;
        dd.width = w; dd.height = h;
        dd.sampleCount = device->multiSampleCount > 1 ? device->multiSampleCount : 1;
        dd.usage = MTLTextureUsageRenderTarget;
        dd.storageMode = MTLStorageModePrivate;
        device->backbufferDepth = [device->mtlDevice newTextureWithDescriptor:dd];
    }

    if (device->multiSampleCount > 1)
    {
        MTLTextureDescriptor* md = [[MTLTextureDescriptor alloc] init];
        md.textureType = MTLTextureType2DMultisample;
        md.pixelFormat = device->backbufferFormat;
        md.width = w; md.height = h;
        md.sampleCount = device->multiSampleCount;
        md.usage = MTLTextureUsageRenderTarget;
        md.storageMode = MTLStorageModePrivate;
        device->backbufferMsaa = [device->mtlDevice newTextureWithDescriptor:md];
    }
}

static CGSize MGMTL_GetLiveBackingSize(MGG_GraphicsDevice* device)
{
    NSView* view = (__bridge NSView*)device->metalView;
    return [view convertSizeToBacking:view.bounds.size];
}

// Track the window's real physical pixel size as the back buffer size (the "swapchain sizes itself to
// the surface" model — mirrors the Vulkan backend). SDL auto-resizes the CAMetalLayer with the view, so
// on a fullscreen/resize the drawable grows; without pulling that size into the managed back buffer the
// viewport would stay windowed-size and render into only part of the drawable (the classic quarter-screen
// bug). Only tracks once the window is SHOWN: while hidden (e.g. --screenshot) SDL reports the point size
// (backing scale not applied yet), which would shrink the deterministic offscreen capture — so we keep
// the size the managed side set via ResizeSwapchain until the window is realized. Recreates the depth/MSAA
// aux targets on a real change so all attachments stay the same size.
static void MGMTL_SyncBackbufferSize(MGG_GraphicsDevice* device)
{
    if (device->window == nullptr || device->layer == nil)
        return;
    if ((SDL_GetWindowFlags(device->window) & SDL_WINDOW_HIDDEN) != 0)
        return;
    // SDL updates drawableSize from a window pixel-size event. During a fullscreen transition the
    // view bounds can change one callback before that event, briefly allowing nextDrawable to return
    // an old-size surface for the resized layer. Synchronize from the view's live backing bounds at
    // the point of acquisition so a stale drawable is never presented through the new geometry.
    NSView* view = (__bridge NSView*)device->metalView;
    CGSize backingSize = MGMTL_GetLiveBackingSize(device);
    if (backingSize.width > 0 && backingSize.height > 0 &&
        !CGSizeEqualToSize(device->layer.drawableSize, backingSize))
    {
        device->layer.contentsScale = backingSize.height / view.bounds.size.height;
        device->layer.drawableSize = backingSize;
    }

    CGSize ds = device->layer.drawableSize;
    int w = (int)ds.width, h = (int)ds.height;

    // Geometry diagnostics (MG_METAL_PERF): dump window/view/layer/screen geometry when the fullscreen
    // flags or the drawable size change, to pin down the fullscreen placement offset.
    if (device->perf)
    {
        uint32_t flags = (uint32_t)SDL_GetWindowFlags(device->window);
        if (flags != device->lastWindowFlags || w != device->backbufferWidth || h != device->backbufferHeight)
        {
            device->lastWindowFlags = flags;
            int pw = 0, ph = 0; SDL_GetWindowSize(device->window, &pw, &ph);
            int xw = 0, xh = 0; SDL_GetWindowSizeInPixels(device->window, &xw, &xh);
            int px = 0, py = 0; SDL_GetWindowPosition(device->window, &px, &py);
            CGRect lf = device->layer.frame, vf = view.frame, vb = view.bounds;
            NSWindow* nw = view.window;
            CGRect wf = nw ? nw.frame : CGRectZero;
            CGRect sf = (nw && nw.screen) ? nw.screen.frame : CGRectZero;
            MGMTL_Log("[metal-geom] flags=0x%x winPts=%dx%d winPx=%dx%d pos=%d,%d | drawable=%.0fx%.0f layerFrame=(%.0f,%.0f %.0fx%.0f) scale=%.2f | viewFrame=(%.0f,%.0f %.0fx%.0f) viewBounds=%.0fx%.0f | nswinFrame=(%.0f,%.0f %.0fx%.0f) screen=(%.0f,%.0f %.0fx%.0f)",
                      flags, pw, ph, xw, xh, px, py,
                      ds.width, ds.height, lf.origin.x, lf.origin.y, lf.size.width, lf.size.height, device->layer.contentsScale,
                      vf.origin.x, vf.origin.y, vf.size.width, vf.size.height, vb.size.width, vb.size.height,
                      wf.origin.x, wf.origin.y, wf.size.width, wf.size.height, sf.origin.x, sf.origin.y, sf.size.width, sf.size.height);
        }
    }

    if (w <= 0 || h <= 0)
        return;
    if (w == device->backbufferWidth && h == device->backbufferHeight)
        return;
    device->backbufferWidth = w;
    device->backbufferHeight = h;
    MGMTL_CreateBackbufferAuxTargets(device); // keep depth/MSAA the same size as the color drawable
}

void MGG_GraphicsDevice_ResizeSwapchain(MGG_GraphicsDevice* device, void* nativeWindowHandle,
                                        mgint width, mgint height, MGSurfaceFormat color,
                                        MGDepthFormat depth, mgint multiSampleCount, mgint syncInterval)
{
    assert(device);
    if (multiSampleCount <= 0) multiSampleCount = 1;

    SDL_Window* sdlWindow = (SDL_Window*)nativeWindowHandle;

    if (device->layer == nil || sdlWindow != device->window)
    {
        device->window = sdlWindow;
        if (device->metalView) { SDL_Metal_DestroyView(device->metalView); device->metalView = nullptr; }
        device->metalView = SDL_Metal_CreateView(sdlWindow);
        device->layer = (__bridge CAMetalLayer*)SDL_Metal_GetLayer(device->metalView);
        device->layer.device = device->mtlDevice;
        device->layer.pixelFormat = MTLPixelFormatBGRA8Unorm;
        device->layer.framebufferOnly = NO; // allow GetBackBufferData readback
        device->layer.maximumDrawableCount = MAX_FRAMES_IN_FLIGHT;
    }

    device->backbufferFormat = MTLPixelFormatBGRA8Unorm;
    device->backbufferDepthFormat = ToMTLDepthFormat(depth);
    device->backbufferWidth = width;
    device->backbufferHeight = height;
    device->multiSampleCount = multiSampleCount;
    device->syncInterval = syncInterval;

    // Do NOT set layer.drawableSize here. SDL's Cocoa metal view owns it: the view autoresizes with the
    // window (NSViewWidth|HeightSizable) and its updateDrawableSize sets drawableSize = view backing size.
    // Overriding it with the managed-passed size desynced the drawable from the view in fullscreen (the
    // passed size is stale/wrong there), which offset the rendered image within the screen. We read the
    // SDL-managed size back in MGMTL_SyncBackbufferSize instead.
    device->layer.displaySyncEnabled = syncInterval > 0 ? YES : NO;

    MGMTL_CreateBackbufferAuxTargets(device);

    MGMTL_PrepareNextFrame(device);
}

void MGG_GraphicsDevice_GetBackBufferSize(MGG_GraphicsDevice* device, mgint& width, mgint& height)
{
    // Called each frame via the managed SyncBackBufferToSwapchain (BeforeDraw), before the frame's
    // draws — so pulling the live window size here keeps the managed viewport matching the drawable
    // (fixes fullscreen rendering into only a corner of the screen).
    MGMTL_SyncBackbufferSize(device);
    width = device->backbufferWidth;
    height = device->backbufferHeight;
}

mgint MGG_GraphicsDevice_GetDrawableSizeMismatchCount(MGG_GraphicsDevice* device)
{
    return device->validateDrawableSize ? device->drawableSizeMismatchCount : -1;
}

static void MGMTL_MarkAllDirty(MGG_GraphicsDevice* device)
{
    device->pipelineDirty = true;
    device->viewportDirty = true;
    device->scissorDirty = true;
    device->rasterDirty = true;
    device->blendFactorDirty = true;
    device->depthStencilDirty = true;
    device->indexDirty = true;
    device->vertexDirty = 0xFFFFFFFF;
    device->bindingsDirty = true;
}

static void MGMTL_PrepareNextFrame(MGG_GraphicsDevice* device)
{
    // NOTE: the in-flight gate is NOT taken here. PrepareNextFrame is called from BOTH Present and
    // ResizeSwapchain, and a resize (OnPresentationChanged) does Present-then-ResizeSwapchain — so
    // waiting here would decrement the semaphore twice but only signal once (Present commits once),
    // leaking a slot per resize until the game froze. The gate is now taken/released 1:1 around the
    // Present commit instead (see MGG_GraphicsDevice_Present).
    device->commandBuffer = [device->queue commandBuffer];
    device->drawable = nil;
    device->encoder = nil;
    device->backbufferColorInitialized = false;
    device->backbufferDepthInitialized = false;
    device->backbufferStencilInitialized = false;

    // Default target = backbuffer.
    device->usingBackbuffer = true;
    device->colorTargetCount = 0;
    device->depthTarget = nullptr;

    device->clearColor = device->clearDepth = device->clearStencil = false;

    // Recycle buffers discarded MAX_FRAMES_IN_FLIGHT frames ago. std::vector::clear() only drops
    // the raw pointers — it does not run MGG_Buffer's destructor, so the id<MTLBuffer> field's ARC
    // reference (and the GPU memory behind it) would never be released, leaking every discarded
    // dynamic vertex/index buffer for the life of the process. Delete each one explicitly first.
    int slot = (int)(device->frame % MAX_FRAMES_IN_FLIGHT);
    for (MGG_Buffer* discardedBuffer : device->discarded[slot])
    {
        discardedBuffer->buffer = nil;
        delete discardedBuffer;
    }
    device->discarded[slot].clear();

    device->drewThisFrame = false;
    device->perfFrame++;

    MGMTL_MarkAllDirty(device);
}

mgint MGG_GraphicsDevice_BeginFrame(MGG_GraphicsDevice* device)
{
    // Real per-frame setup happens in Present -> PrepareNextFrame (mirrors the DX12 backend).
    return device->begin_frame_index;
}

void MGG_GraphicsDevice_Clear(MGG_GraphicsDevice* device, MGClearOptions options, Vector4& color, mgfloat depth, mgint stencil)
{
    // A clear must reset the whole (sub)buffer. End any active pass (storing its contents) and stage
    // the clear as a load-action for the next render-pass begin. A partial clear (e.g. depth only)
    // leaves the other attachments on Load so their contents are preserved.
    MGMTL_EndEncoder(device);

    if ((mgint)options & (mgint)MGClearOptions::Target)
    {
        device->clearColor = true;
        device->clearColorValue = MTLClearColorMake(color.X, color.Y, color.Z, color.W);
    }
    if ((mgint)options & (mgint)MGClearOptions::DepthBuffer)
    {
        device->clearDepth = true;
        device->clearDepthValue = depth;
    }
    if ((mgint)options & (mgint)MGClearOptions::Stencil)
    {
        device->clearStencil = true;
        device->clearStencilValue = (uint32_t)stencil;
    }
}

static id<MTLTexture> MGMTL_AcquireBackbufferColor(MGG_GraphicsDevice* device, __strong id<MTLTexture>& resolveOut)
{
    resolveOut = nil;
    // Make sure the drawable + depth/MSAA aux match the current window size before we render (handles a
    // fullscreen/resize that happened since this frame began).
    MGMTL_SyncBackbufferSize(device);
    if (device->drawable == nil)
    {
        device->drawable = [device->layer nextDrawable];
        device->backbufferColorInitialized = false;
        device->backbufferDepthInitialized = false;
        device->backbufferStencilInitialized = false;
    }
    if (device->drawable == nil)
        return nil;

    if (device->validateDrawableSize)
    {
        CGSize backingSize = MGMTL_GetLiveBackingSize(device);
        if ((int)device->drawable.texture.width != (int)backingSize.width ||
            (int)device->drawable.texture.height != (int)backingSize.height)
        {
            device->drawableSizeMismatchCount++;
        }
    }

    if (device->multiSampleCount > 1)
    {
        resolveOut = device->drawable.texture;
        return device->backbufferMsaa;
    }
    return device->drawable.texture;
}

static bool MGMTL_EnsureEncoder(MGG_GraphicsDevice* device)
{
    if (device->encoder != nil)
        return true;
    if (device->commandBuffer == nil)
        return false;

    MTLRenderPassDescriptor* rp = [MTLRenderPassDescriptor renderPassDescriptor];

    id<MTLTexture> depthTex = nil;
    MTLPixelFormat depthFmt = MTLPixelFormatInvalid;

    if (device->usingBackbuffer)
    {
        id<MTLTexture> resolveTex = nil;
        id<MTLTexture> colorTex = MGMTL_AcquireBackbufferColor(device, resolveTex);
        if (colorTex == nil)
            return false; // drawable not available (occluded); skip this pass.

        MTLRenderPassColorAttachmentDescriptor* ca = rp.colorAttachments[0];
        ca.texture = colorTex;
        // Initialize a new drawable once; encoder breaks must preserve earlier draws.
        ca.loadAction = device->clearColor || !device->backbufferColorInitialized
            ? MTLLoadActionClear : MTLLoadActionLoad;
        ca.clearColor = device->clearColor
            ? device->clearColorValue
            : MTLClearColorMake(0.0, 0.0, 0.0, 1.0);
        if (resolveTex != nil)
        {
            ca.resolveTexture = resolveTex;
            ca.storeAction = MTLStoreActionStoreAndMultisampleResolve;
        }
        else
        {
            ca.storeAction = MTLStoreActionStore;
        }

        depthTex = device->backbufferDepth;
        depthFmt = device->backbufferDepthFormat;
    }
    else
    {
        for (int i = 0; i < device->colorTargetCount; i++)
        {
            MGG_Texture* rt = device->colorTargets[i];
            if (rt == nullptr) continue;
            MTLRenderPassColorAttachmentDescriptor* ca = rp.colorAttachments[i];
            if (rt->msaaTexture != nil)
            {
                ca.texture = rt->msaaTexture;
                ca.resolveTexture = rt->texture;
                ca.storeAction = MTLStoreActionMultisampleResolve;
            }
            else
            {
                ca.texture = rt->texture;
                ca.storeAction = MTLStoreActionStore;
            }
            ca.level = 0;
            ca.slice = device->colorTargetSlices[i];
            ca.loadAction = device->clearColor ? MTLLoadActionClear : MTLLoadActionLoad;
            ca.clearColor = device->clearColorValue;
        }

        if (device->depthTarget != nullptr)
        {
            depthTex = device->depthTarget->depthTexture;
            depthFmt = ToMTLDepthFormat(device->depthTarget->depthFormat);
        }
    }

    bool hasStencil = (depthFmt == MTLPixelFormatDepth32Float_Stencil8);
    if (depthTex != nil)
    {
        MTLRenderPassDepthAttachmentDescriptor* da = rp.depthAttachment;
        da.texture = depthTex;
        da.loadAction = device->clearDepth || (device->usingBackbuffer && !device->backbufferDepthInitialized)
            ? MTLLoadActionClear : MTLLoadActionLoad;
        da.clearDepth = device->clearDepth ? device->clearDepthValue : 1.0;
        da.storeAction = MTLStoreActionStore;

        if (hasStencil)
        {
            MTLRenderPassStencilAttachmentDescriptor* sa = rp.stencilAttachment;
            sa.texture = depthTex;
            sa.loadAction = device->clearStencil || (device->usingBackbuffer && !device->backbufferStencilInitialized)
                ? MTLLoadActionClear : MTLLoadActionLoad;
            sa.clearStencil = device->clearStencil ? device->clearStencilValue : 0;
            sa.storeAction = MTLStoreActionStore;
        }
    }

    if (device->visibilityBuffer != nil)
        rp.visibilityResultBuffer = device->visibilityBuffer;

    device->encoder = [device->commandBuffer renderCommandEncoderWithDescriptor:rp];
    if (device->encoder == nil)
        return false;
    if (device->usingBackbuffer)
    {
        device->backbufferColorInitialized = true;
        device->backbufferDepthInitialized = depthTex != nil;
        device->backbufferStencilInitialized = depthTex != nil && hasStencil;
    }
    [device->encoder setFrontFacingWinding:MTLWindingClockwise];

    // Consume pending clears; everything must be re-applied to the fresh encoder.
    device->clearColor = device->clearDepth = device->clearStencil = false;
    MGMTL_MarkAllDirty(device);
    return true;
}

static void MGMTL_EndEncoder(MGG_GraphicsDevice* device)
{
    if (device->encoder != nil)
    {
        [device->encoder endEncoding];
        device->encoder = nil;
    }
}

// Commit the current frame's command buffer and wait for the GPU, then start a fresh one so recording
// continues. Readback paths (GetData / GetBackBufferData) need this so they observe the results of
// draws already recorded this frame — screenshot mode renders to an offscreen target and reads it back
// WITHOUT ever calling Present, so the recorded draws must be flushed here or they never execute.
static void MGMTL_FlushAndContinue(MGG_GraphicsDevice* device)
{
    MGMTL_EndEncoder(device);
    if (device->commandBuffer != nil)
    {
        [device->commandBuffer commit];
        [device->commandBuffer waitUntilCompleted];
    }
    device->commandBuffer = [device->queue commandBuffer];
    // A readback flush is not a presentation; subsequent draws still target this drawable.
    MGMTL_MarkAllDirty(device);
}

void MGG_GraphicsDevice_Present(MGG_GraphicsDevice* device, mgint currentFrame, mgint syncInterval)
{
    MGMTL_EndEncoder(device);

    // Realize a clear-only backbuffer frame (Clear then Present with no draws).
    if (device->usingBackbuffer && (device->clearColor || device->clearDepth || device->clearStencil))
    {
        if (MGMTL_EnsureEncoder(device))
            MGMTL_EndEncoder(device);
    }

    // Diagnostics: copy the about-to-be-presented backbuffer into a staging buffer and scan it for
    // magenta / no-draw in the command buffer's completion handler (async — no CPU stall, so timing
    // isn't perturbed). Catches the transient countdown-start magenta flash with full context.
    if (device->perf && device->drawable != nil)
    {
        const int w = device->backbufferWidth, h = device->backbufferHeight;
        const size_t rowBytes = (size_t)w * 4, total = rowBytes * h;
        if (w > 0 && h > 0)
        {
            if (device->perfStaging == nil || device->perfStaging.length < total)
                device->perfStaging = [device->mtlDevice newBufferWithLength:total options:MTLResourceStorageModeShared];

            id<MTLBlitCommandEncoder> pblit = [device->commandBuffer blitCommandEncoder];
            [pblit copyFromTexture:device->drawable.texture sourceSlice:0 sourceLevel:0
                      sourceOrigin:MTLOriginMake(0, 0, 0) sourceSize:MTLSizeMake(w, h, 1)
                          toBuffer:device->perfStaging destinationOffset:0 destinationBytesPerRow:rowBytes
              destinationBytesPerImage:total];
            [pblit endEncoding];

            uint64_t f = device->perfFrame; bool drew = device->drewThisFrame;
            double cm = device->lastCompileMs; uint64_t cf = device->lastCompileFrame;
            id<MTLBuffer> st = device->perfStaging; size_t px = (size_t)w * h;
            [device->commandBuffer addCompletedHandler:^(id<MTLCommandBuffer> cb) {
                const uint8_t* p = (const uint8_t*)st.contents; // BGRA8
                size_t mag = 0;
                for (size_t i = 0; i < px; i++)
                    if (p[i*4+0] > 220 && p[i*4+1] < 60 && p[i*4+2] > 220) mag++; // B high, G low, R high
                double pct = 100.0 * mag / (double)px;
                if (pct > 20.0 || !drew)
                    MGMTL_Log("[metal-perf] PRESENT frame %llu: magenta=%.1f%% drew=%d lastPipelineCompile=%.2fms@frame%llu",
                              (unsigned long long)f, pct, drew ? 1 : 0, cm, (unsigned long long)cf);
            }];
        }
    }

    if (device->drawable != nil)
        [device->commandBuffer presentDrawable:device->drawable];

    // In-flight gate: exactly one wait per committed+presented frame, released by the completion
    // handler. Bounds the CPU to MAX_FRAMES_IN_FLIGHT frames ahead of the GPU. Paired 1:1 with this
    // commit (not with PrepareNextFrame) so resizes, which prepare more than once per Present, can't
    // leak the semaphore.
    dispatch_semaphore_wait(device->inFlight, DISPATCH_TIME_FOREVER);
    __block dispatch_semaphore_t sem = device->inFlight;
    [device->commandBuffer addCompletedHandler:^(id<MTLCommandBuffer> cb) {
        dispatch_semaphore_signal(sem);
    }];
    [device->commandBuffer commit];

    device->commandBuffer = nil;
    device->frame++;

    MGMTL_PrepareNextFrame(device);
}

// ===========================================================================================
// Render targets
// ===========================================================================================

void MGG_GraphicsDevice_SetRenderTargets(MGG_GraphicsDevice* device, MGG_Texture** targets, mgint* arraySlices, mgint count)
{
    // Ending the encoder flushes any MSAA resolve for the outgoing target.
    MGMTL_EndEncoder(device);

    // A clear-only target has no draw to start its deferred render pass. Realize the pending clear
    // before unbinding it so later sampling does not expose uninitialized texture contents.
    if (device->clearColor || device->clearDepth || device->clearStencil)
    {
        if (MGMTL_EnsureEncoder(device))
            MGMTL_EndEncoder(device);
    }

    // Switching targets starts fresh: no pending clears carry over, and the pipeline formats change.
    device->clearColor = device->clearDepth = device->clearStencil = false;
    device->pipelineDirty = true;

    if (targets == nullptr || count == 0)
    {
        device->usingBackbuffer = true;
        device->colorTargetCount = 0;
        device->depthTarget = nullptr;
        return;
    }

    device->usingBackbuffer = false;
    device->colorTargetCount = (int)count;
    for (int i = 0; i < (int)count && i < 8; i++)
    {
        device->colorTargets[i] = targets[i];
        device->colorTargetSlices[i] = arraySlices ? (int)arraySlices[i] : 0;
    }
    // Only the first target's depth is used (matches DX12).
    device->depthTarget = targets[0];
}

void MGG_GraphicsDevice_ResolveRenderTargets(MGG_GraphicsDevice* device)
{
    // MSAA resolve is handled by the color attachment storeAction when the encoder ends
    // (in SetRenderTargets/Present). Here we only generate mipmaps for bound RTs (matches DX12).
    if (device->usingBackbuffer || device->colorTargetCount == 0)
        return;

    MGMTL_EndEncoder(device);

    id<MTLBlitCommandEncoder> blit = nil;
    for (int i = 0; i < device->colorTargetCount; i++)
    {
        MGG_Texture* rt = device->colorTargets[i];
        if (rt && rt->texture != nil && rt->mipmaps > 1)
        {
            if (blit == nil) blit = [device->commandBuffer blitCommandEncoder];
            [blit generateMipmapsForTexture:rt->texture];
        }
    }
    if (blit != nil) [blit endEncoding];
}

void MGG_GraphicsDevice_GetBackBufferData(MGG_GraphicsDevice* device, mgint x, mgint y, mgint width, mgint height,
                                          void* data, mgint count, mgint dataBytes)
{
    // Ensure the backbuffer has actually been drawn before reading it back.
    if (device->drawable == nil || device->clearColor || device->clearDepth || device->clearStencil)
    {
        if (!MGMTL_EnsureEncoder(device))
            return;
    }
    MGMTL_EndEncoder(device);
    id<MTLTexture> src = device->drawable ? device->drawable.texture : nil;
    if (src == nil) return;

    const int bpp = 4;
    const size_t rowBytes = (size_t)width * bpp;
    id<MTLBuffer> staging = [device->mtlDevice newBufferWithLength:rowBytes * height options:MTLResourceStorageModeShared];

    // Blit on the frame's command buffer (after the draws), then flush and wait.
    id<MTLBlitCommandEncoder> blit = [device->commandBuffer blitCommandEncoder];
    [blit copyFromTexture:src sourceSlice:0 sourceLevel:0
             sourceOrigin:MTLOriginMake(x, y, 0) sourceSize:MTLSizeMake(width, height, 1)
                 toBuffer:staging destinationOffset:0 destinationBytesPerRow:rowBytes
      destinationBytesPerImage:rowBytes * height];
    [blit endEncoding];
    MGMTL_FlushAndContinue(device);

    // Drawable is BGRA8; MonoGame's Color expects RGBA8 — swizzle B<->R.
    const uint8_t* srcPtr = (const uint8_t*)staging.contents;
    uint8_t* dstPtr = (uint8_t*)data;
    size_t pixels = (size_t)width * height;
    size_t maxPixels = (size_t)count * dataBytes / bpp;
    if (pixels > maxPixels) pixels = maxPixels;
    for (size_t i = 0; i < pixels; i++)
    {
        dstPtr[i * 4 + 0] = srcPtr[i * 4 + 2];
        dstPtr[i * 4 + 1] = srcPtr[i * 4 + 1];
        dstPtr[i * 4 + 2] = srcPtr[i * 4 + 0];
        dstPtr[i * 4 + 3] = srcPtr[i * 4 + 3];
    }
}

// ===========================================================================================
// Dynamic state setters
// ===========================================================================================

void MGG_GraphicsDevice_SetBlendState(MGG_GraphicsDevice* device, MGG_BlendState* state, mgfloat factorR, mgfloat factorG, mgfloat factorB, mgfloat factorA)
{
    if (device->blendState != state) { device->blendState = state; device->pipelineDirty = true; }
    device->blendFactor[0] = factorR; device->blendFactor[1] = factorG;
    device->blendFactor[2] = factorB; device->blendFactor[3] = factorA;
    device->blendFactorDirty = true;
}

void MGG_GraphicsDevice_SetDepthStencilState(MGG_GraphicsDevice* device, MGG_DepthStencilState* state)
{
    if (device->depthStencilState != state) { device->depthStencilState = state; device->depthStencilDirty = true; }
}

void MGG_GraphicsDevice_SetRasterizerState(MGG_GraphicsDevice* device, MGG_RasterizerState* state)
{
    if (device->rasterizerState != state) { device->rasterizerState = state; device->rasterDirty = true; device->scissorDirty = true; }
}

void MGG_GraphicsDevice_SetViewport(MGG_GraphicsDevice* device, mgint x, mgint y, mgint width, mgint height, mgfloat minDepth, mgfloat maxDepth)
{
    device->viewport.originX = x;
    device->viewport.originY = y;
    device->viewport.width = width;
    device->viewport.height = height;
    device->viewport.znear = minDepth;
    device->viewport.zfar = maxDepth;
    device->viewportDirty = true;
    if (!(device->rasterizerState && device->rasterizerState->scissorTestEnable))
        device->scissorDirty = true;
}

void MGG_GraphicsDevice_SetScissorRectangle(MGG_GraphicsDevice* device, mgint x, mgint y, mgint width, mgint height)
{
    device->scissor.x = x < 0 ? 0 : x;
    device->scissor.y = y < 0 ? 0 : y;
    device->scissor.width = width < 0 ? 0 : width;
    device->scissor.height = height < 0 ? 0 : height;
    device->scissorSet = true;
    device->scissorDirty = true;
}

// ===========================================================================================
// State objects
// ===========================================================================================

MGG_BlendState* MGG_BlendState_Create(MGG_GraphicsDevice* device, MGG_BlendState_Info* infos)
{
    auto state = new MGG_BlendState();
    state->id = device->nextObjectId++;
    for (int i = 0; i < 4; i++)
    {
        state->infos[i] = infos[i];
        bool opaque = infos[i].colorSourceBlend == MGBlend::One && infos[i].colorDestBlend == MGBlend::Zero &&
                      infos[i].alphaSourceBlend == MGBlend::One && infos[i].alphaDestBlend == MGBlend::Zero;
        if (!opaque) state->blending = true;
    }
    return state;
}

void MGG_BlendState_Destroy(MGG_GraphicsDevice* device, MGG_BlendState* state) { delete state; }

MGG_DepthStencilState* MGG_DepthStencilState_Create(MGG_GraphicsDevice* device, MGG_DepthStencilState_Info* info)
{
    auto state = new MGG_DepthStencilState();
    state->id = device->nextObjectId++;
    state->referenceStencil = info->referenceStencil;

    MTLDepthStencilDescriptor* dd = [[MTLDepthStencilDescriptor alloc] init];
    dd.depthCompareFunction = info->depthBufferEnable ? ToMTLCompareFunction(info->depthBufferFunction) : MTLCompareFunctionAlways;
    dd.depthWriteEnabled = info->depthBufferWriteEnable ? YES : NO;

    if (info->stencilEnable)
    {
        MTLStencilDescriptor* sd = [[MTLStencilDescriptor alloc] init];
        sd.stencilCompareFunction = ToMTLCompareFunction(info->stencilFunction);
        sd.stencilFailureOperation = ToMTLStencilOperation(info->stencilFail);
        sd.depthFailureOperation = ToMTLStencilOperation(info->stencilDepthBufferFail);
        sd.depthStencilPassOperation = ToMTLStencilOperation(info->stencilPass);
        sd.readMask = (uint32_t)info->stencilMask;
        sd.writeMask = (uint32_t)info->stencilWriteMask;
        dd.frontFaceStencil = sd;
        dd.backFaceStencil = sd;
    }

    state->state = [device->mtlDevice newDepthStencilStateWithDescriptor:dd];
    return state;
}

void MGG_DepthStencilState_Destroy(MGG_GraphicsDevice* device, MGG_DepthStencilState* state)
{
    if (state) { state->state = nil; delete state; }
}

MGG_RasterizerState* MGG_RasterizerState_Create(MGG_GraphicsDevice* device, MGG_RasterizerState_Info* info)
{
    auto state = new MGG_RasterizerState();
    state->id = device->nextObjectId++;
    state->cullMode = info->cullMode;
    state->fillMode = info->fillMode;
    state->scissorTestEnable = info->scissorTestEnable;
    state->depthBias = info->depthBias;
    state->slopeScaleDepthBias = info->slopeScaleDepthBias;
    state->multiSampleAntiAlias = info->multiSampleAntiAlias;
    return state;
}

void MGG_RasterizerState_Destroy(MGG_GraphicsDevice* device, MGG_RasterizerState* state) { delete state; }

MGG_SamplerState* MGG_SamplerState_Create(MGG_GraphicsDevice* device, MGG_SamplerState_Info* info)
{
    auto state = new MGG_SamplerState();
    state->id = device->nextObjectId++;
    state->info = *info;

    MTLSamplerDescriptor* sd = [[MTLSamplerDescriptor alloc] init];
    sd.sAddressMode = ToMTLAddressMode(info->AddressU);
    sd.tAddressMode = ToMTLAddressMode(info->AddressV);
    sd.rAddressMode = ToMTLAddressMode(info->AddressW);

    MTLSamplerMinMagFilter minF, magF; MTLSamplerMipFilter mipF; bool aniso;
    ToMTLSamplerFilters(info->Filter, minF, magF, mipF, aniso);
    sd.minFilter = minF;
    sd.magFilter = magF;
    sd.mipFilter = mipF;
    sd.maxAnisotropy = aniso ? (info->MaximumAnisotropy > 0 ? info->MaximumAnisotropy : 16) : 1;
    sd.lodMinClamp = 0.0f;
    sd.lodMaxClamp = FLT_MAX;
    if (info->FilterMode == MGTextureFilterMode::Comparison)
        sd.compareFunction = ToMTLCompareFunction(info->ComparisonFunction);
#if TARGET_OS_OSX
    sd.borderColor = MTLSamplerBorderColorOpaqueBlack;
#endif

    state->state = [device->mtlDevice newSamplerStateWithDescriptor:sd];
    return state;
}

void MGG_SamplerState_Destroy(MGG_GraphicsDevice* device, MGG_SamplerState* state)
{
    if (state) { state->state = nil; delete state; }
}

// ===========================================================================================
// Buffers
// ===========================================================================================

MGG_Buffer* MGG_Buffer_Create(MGG_GraphicsDevice* device, MGBufferType type, mgbool dynamic, mgint sizeInBytes)
{
    auto buffer = new MGG_Buffer();
    buffer->type = type;
    buffer->size = sizeInBytes;

    if (type == MGBufferType::Constant)
    {
        // Constant buffers are CPU-side; uploaded per-draw via setVertexBytes/setFragmentBytes.
        buffer->isConstant = true;
        buffer->push.resize(sizeInBytes, 0);
    }
    else
    {
        buffer->buffer = [device->mtlDevice newBufferWithLength:(sizeInBytes > 0 ? sizeInBytes : 4)
                                                        options:MTLResourceStorageModeShared];
    }
    return buffer;
}

void MGG_Buffer_Destroy(MGG_GraphicsDevice* device, MGG_Buffer* buffer)
{
    if (!buffer) return;
    // Draw state borrows CPU wrappers; Metal retaining an encoded buffer cannot keep these alive.
    for (int s = 0; s < NUM_STAGES; s++)
        if (device->uniforms[s] == buffer)
        {
            device->uniforms[s] = nullptr;
            device->bindingsDirty = true;
        }
    for (int i = 0; i < MAX_VERTEX_BUFFERS; i++)
        if (device->vertexBuffers[i] == buffer)
        {
            device->vertexBuffers[i] = nullptr;
            device->vertexOffsets[i] = 0;
            device->vertexDirty |= (1u << i);
        }
    if (device->indexBuffer == buffer)
    {
        device->indexBuffer = nullptr;
        device->indexDirty = true;
    }
    buffer->buffer = nil;
    delete buffer;
}

static void MGMTL_BufferCopy(MGG_Buffer* buffer, int offset, mgbyte* data, mgint elementCount, mgint vertexStride, mgint elementSizeInBytes)
{
    uint8_t* dst = buffer->isConstant ? buffer->push.data() : (uint8_t*)buffer->buffer.contents;
    dst += offset;
    if (vertexStride == elementSizeInBytes)
    {
        memcpy(dst, data, (size_t)elementCount * elementSizeInBytes);
    }
    else
    {
        for (mgint i = 0; i < elementCount; i++)
            memcpy(dst + i * vertexStride, data + i * elementSizeInBytes, elementSizeInBytes);
    }
}

void MGG_Buffer_SetData(MGG_GraphicsDevice* device, MGG_Buffer*& buffer, mgint offset, mgbyte* data,
                        mgint elementCount, mgint vertexStride, mgint elementSizeInBytes, mgbool discard)
{
    assert(buffer && data);

    // Constant/push buffers: just copy; discard is irrelevant (per-draw upload).
    if (buffer->isConstant)
    {
        MGMTL_BufferCopy(buffer, offset, data, elementCount, vertexStride, elementSizeInBytes);
        return;
    }

    // Orphan on discard: allocate a fresh MTLBuffer so we never overwrite data an in-flight command
    // buffer may still be reading. The old MGG_Buffer is retired for a few frames (ARC + Metal's
    // command-buffer retention keep the underlying MTLBuffer alive until the GPU is done).
    if (discard)
    {
        MGG_Buffer* last = buffer;
        auto fresh = new MGG_Buffer();
        fresh->type = last->type;
        fresh->size = last->size;
        fresh->buffer = [device->mtlDevice newBufferWithLength:(last->size > 0 ? last->size : 4)
                                                       options:MTLResourceStorageModeShared];
        buffer = fresh;

        int slot = (int)(device->frame % MAX_FRAMES_IN_FLIGHT);
        device->discarded[slot].push_back(last);

        // Fix up any active binding that referenced the discarded buffer.
        switch (fresh->type)
        {
        case MGBufferType::Vertex:
            for (int i = 0; i < MAX_VERTEX_BUFFERS; i++)
                if (device->vertexBuffers[i] == last) { device->vertexBuffers[i] = fresh; device->vertexDirty |= (1u << i); }
            break;
        case MGBufferType::Index:
            if (device->indexBuffer == last) { device->indexBuffer = fresh; device->indexDirty = true; }
            break;
        default: break;
        }
    }

    MGMTL_BufferCopy(buffer, offset, data, elementCount, vertexStride, elementSizeInBytes);
}

void MGG_Buffer_GetData(MGG_GraphicsDevice* device, MGG_Buffer* buffer, mgint offset, mgbyte* data,
                        mgint dataCount, mgint dataBytes, mgint dataStride)
{
    assert(buffer && data);
    const uint8_t* src = buffer->isConstant ? buffer->push.data() : (const uint8_t*)buffer->buffer.contents;
    src += offset;
    if (dataStride == dataBytes)
    {
        memcpy(data, src, (size_t)dataCount * dataBytes);
    }
    else
    {
        int copy = dataBytes < dataStride ? dataBytes : dataStride;
        for (mgint i = 0; i < dataCount; i++)
        {
            memcpy(data + i * dataBytes, src + i * dataStride, copy);
        }
    }
}

// ===========================================================================================
// Textures / render targets
// ===========================================================================================

static MTLTextureDescriptor* MGMTL_MakeTextureDescriptor(MGTextureType type, MTLPixelFormat pf,
                                                         int width, int height, int depth, int mipmaps, int slices)
{
    MTLTextureDescriptor* td = [[MTLTextureDescriptor alloc] init];
    td.pixelFormat = pf;
    td.width = width;
    td.height = height;
    td.mipmapLevelCount = mipmaps < 1 ? 1 : mipmaps;

    switch (type)
    {
    case MGTextureType::_3D:
        td.textureType = MTLTextureType3D;
        td.depth = depth < 1 ? 1 : depth;
        break;
    case MGTextureType::Cube:
        td.textureType = slices > 6 ? MTLTextureTypeCubeArray : MTLTextureTypeCube;
        if (slices > 6) td.arrayLength = slices / 6;
        break;
    default:
        if (slices > 1) { td.textureType = MTLTextureType2DArray; td.arrayLength = slices; }
        else td.textureType = MTLTextureType2D;
        break;
    }
    return td;
}

MGG_Texture* MGG_Texture_Create(MGG_GraphicsDevice* device, MGTextureType type, MGSurfaceFormat format,
                                mgint width, mgint height, mgint depth, mgint mipmaps, mgint slices)
{
    auto tex = new MGG_Texture();
    tex->type = type; tex->format = format;
    tex->width = width; tex->height = height; tex->depth = depth; tex->mipmaps = mipmaps; tex->slices = slices;

    MTLTextureDescriptor* td = MGMTL_MakeTextureDescriptor(type, ToMTLPixelFormat(format), width, height, depth, mipmaps, slices);
    td.usage = MTLTextureUsageShaderRead;
    // On unified-memory GPUs (Apple silicon) use Shared so CPU replaceRegion writes are immediately
    // coherent for the GPU without an explicit sync. Managed is only needed on discrete/Intel Macs.
    td.storageMode = device->mtlDevice.hasUnifiedMemory ? MTLStorageModeShared : MTLStorageModeManaged;
    tex->texture = [device->mtlDevice newTextureWithDescriptor:td];
    return tex;
}

MGG_Texture* MGG_RenderTarget_Create(MGG_GraphicsDevice* device, MGTextureType type, MGSurfaceFormat format,
                                     mgint width, mgint height, mgint depth, mgint mipmaps, mgint slices,
                                     MGDepthFormat depthFormat, mgint multiSampleCount, MGRenderTargetUsage usage)
{
    auto tex = new MGG_Texture();
    tex->type = type; tex->format = format;
    tex->width = width; tex->height = height; tex->depth = depth; tex->mipmaps = mipmaps; tex->slices = slices;
    tex->isTarget = true;
    tex->multiSampleCount = multiSampleCount < 1 ? 1 : multiSampleCount;
    tex->depthFormat = depthFormat;

    if (width <= 0 || height <= 0)
        MGMTL_Log("[metal] WARNING MGG_RenderTarget_Create with %dx%d (fmt %d depth %d msaa %d)", width, height, (int)format, (int)depthFormat, multiSampleCount);

    MTLPixelFormat pf = ToMTLPixelFormat(format);

    // Resolvable / sampleable single-sample texture.
    MTLTextureDescriptor* td = MGMTL_MakeTextureDescriptor(type, pf, width, height, depth, mipmaps, slices);
    td.usage = MTLTextureUsageShaderRead | MTLTextureUsageRenderTarget;
    td.storageMode = MTLStorageModePrivate;
    tex->texture = [device->mtlDevice newTextureWithDescriptor:td];

    if (tex->multiSampleCount > 1)
    {
        MTLTextureDescriptor* md = [[MTLTextureDescriptor alloc] init];
        md.textureType = MTLTextureType2DMultisample;
        md.pixelFormat = pf;
        md.width = width; md.height = height;
        md.sampleCount = tex->multiSampleCount;
        md.usage = MTLTextureUsageRenderTarget;
        md.storageMode = MTLStorageModePrivate;
        tex->msaaTexture = [device->mtlDevice newTextureWithDescriptor:md];
    }

    if (depthFormat != MGDepthFormat::None)
    {
        MTLTextureDescriptor* dd = [[MTLTextureDescriptor alloc] init];
        dd.textureType = tex->multiSampleCount > 1 ? MTLTextureType2DMultisample : MTLTextureType2D;
        dd.pixelFormat = ToMTLDepthFormat(depthFormat);
        dd.width = width; dd.height = height;
        dd.sampleCount = tex->multiSampleCount > 1 ? tex->multiSampleCount : 1;
        dd.usage = MTLTextureUsageRenderTarget;
        dd.storageMode = MTLStorageModePrivate;
        tex->depthTexture = [device->mtlDevice newTextureWithDescriptor:dd];
    }

    return tex;
}

void MGG_Texture_Destroy(MGG_GraphicsDevice* device, MGG_Texture* texture)
{
    if (!texture) return;
    texture->texture = nil;
    texture->msaaTexture = nil;
    texture->depthTexture = nil;
    delete texture;
}

void MGG_Texture_SetData(MGG_GraphicsDevice* device, MGG_Texture* texture, mgint level, mgint slice,
                         mgint x, mgint y, mgint z, mgint width, mgint height, mgint depth, mgbyte* data, mgint dataBytes)
{
    assert(texture && texture->texture);

    // A zero width/height/depth means "the whole level" — the managed full-texture SetData overload
    // passes 0s (Texture2D.Native.cs PlatformSetData). Substitute the level's real dimensions; Metal
    // asserts on a truly-zero replaceRegion, and skipping would leave the texture uninitialized.
    if (width <= 0)  width  = texture->width  >> level; if (width  < 1) width  = 1;
    if (height <= 0) height = texture->height >> level; if (height < 1) height = 1;
    if (depth <= 0)  depth  = texture->depth  >> level; if (depth  < 1) depth  = 1;

    if (SurfaceFormatIsCompressed(texture->format))
    {
        // Block-compressed upload: 4x4 blocks.
        int blocksWide = (width + 3) / 4;
        int blockBytes = (texture->format == MGSurfaceFormat::Dxt1 || texture->format == MGSurfaceFormat::Dxt1a ||
                          texture->format == MGSurfaceFormat::Dxt1SRgb) ? 8 : 16;
        NSUInteger bytesPerRow = (NSUInteger)blocksWide * blockBytes;
        [texture->texture replaceRegion:MTLRegionMake2D(x, y, width, height)
                            mipmapLevel:level slice:slice
                              withBytes:data bytesPerRow:bytesPerRow bytesPerImage:0];
        return;
    }

    NSUInteger bytesPerPixel = SurfaceFormatBytesPerPixel(texture->format);
    NSUInteger bytesPerRow = (NSUInteger)width * bytesPerPixel;

    if (texture->type == MGTextureType::_3D)
    {
        [texture->texture replaceRegion:MTLRegionMake3D(x, y, z, width, height, depth)
                            mipmapLevel:level slice:0
                              withBytes:data bytesPerRow:bytesPerRow bytesPerImage:bytesPerRow * height];
    }
    else
    {
        [texture->texture replaceRegion:MTLRegionMake2D(x, y, width, height)
                            mipmapLevel:level slice:slice
                              withBytes:data bytesPerRow:bytesPerRow bytesPerImage:0];
    }
}

void MGG_Texture_GetData(MGG_GraphicsDevice* device, MGG_Texture* texture, mgint level, mgint slice,
                         mgint x, mgint y, mgint z, mgint width, mgint height, mgint depth, mgbyte* data, mgint dataBytes)
{
    assert(texture && texture->texture);

    // Zero width/height means "the whole level" (see MGG_Texture_SetData).
    if (width <= 0)  width  = texture->width  >> level; if (width  < 1) width  = 1;
    if (height <= 0) height = texture->height >> level; if (height < 1) height = 1;

    NSUInteger bytesPerPixel = SurfaceFormatBytesPerPixel(texture->format);
    NSUInteger bytesPerRow = (NSUInteger)width * bytesPerPixel;

    // A render encoder may still be open (GetData can be called mid-frame); Metal allows only one
    // encoder per command buffer, so end it before opening the blit.
    MGMTL_EndEncoder(device);

    // Blit into a shared staging buffer, appended to THIS frame's command buffer so it runs after the
    // draws that produced the texture, then flush and wait. Works for Private and Managed alike.
    id<MTLBuffer> staging = [device->mtlDevice newBufferWithLength:bytesPerRow * height options:MTLResourceStorageModeShared];
    id<MTLBlitCommandEncoder> blit = [device->commandBuffer blitCommandEncoder];
    [blit copyFromTexture:texture->texture sourceSlice:slice sourceLevel:level
             sourceOrigin:MTLOriginMake(x, y, z) sourceSize:MTLSizeMake(width, height, depth < 1 ? 1 : depth)
                 toBuffer:staging destinationOffset:0 destinationBytesPerRow:bytesPerRow
      destinationBytesPerImage:bytesPerRow * height];
    [blit endEncoding];
    MGMTL_FlushAndContinue(device);

    size_t copyBytes = (size_t)bytesPerRow * height;
    if (copyBytes > (size_t)dataBytes) copyBytes = (size_t)dataBytes;
    memcpy(data, staging.contents, copyBytes);
}

// ===========================================================================================
// Input layout
// ===========================================================================================

MGG_InputLayout* MGG_InputLayout_Create(MGG_GraphicsDevice* device, MGG_Shader* vertexShader,
                                        mgint* strides, mgint streamCount, MGG_InputElement* elements, mgint elementCount)
{
    auto layout = new MGG_InputLayout();
    layout->id = device->nextObjectId++;

    MTLVertexDescriptor* vd = [MTLVertexDescriptor vertexDescriptor];

    for (int i = 0; i < elementCount; i++)
    {
        const MGG_InputElement& e = elements[i];
        // Attribute index == element index (matches location=i assigned in the compiled shader).
        vd.attributes[i].format = ToMTLVertexFormat(e.Format);
        vd.attributes[i].offset = e.AlignedByteOffset;
        vd.attributes[i].bufferIndex = MG_MTL_VBO_BASE + e.VertexBufferSlot;

        int bi = MG_MTL_VBO_BASE + e.VertexBufferSlot;
        if (e.InstanceDataStepRate > 0)
        {
            vd.layouts[bi].stepFunction = MTLVertexStepFunctionPerInstance;
            vd.layouts[bi].stepRate = e.InstanceDataStepRate;
        }
    }

    for (int s = 0; s < streamCount; s++)
    {
        int bi = MG_MTL_VBO_BASE + s;
        vd.layouts[bi].stride = strides[s];
        if (vd.layouts[bi].stepFunction != MTLVertexStepFunctionPerInstance)
        {
            vd.layouts[bi].stepFunction = MTLVertexStepFunctionPerVertex;
            vd.layouts[bi].stepRate = 1;
        }
    }

    layout->descriptor = vd;
    return layout;
}

void MGG_InputLayout_Destroy(MGG_GraphicsDevice* device, MGG_InputLayout* layout)
{
    if (layout) { layout->descriptor = nil; delete layout; }
}

// ===========================================================================================
// Shaders (SPIR-V -> MSL via SPIRV-Cross)
// ===========================================================================================

static std::string MGMTL_TranslationCacheKey(const mgbyte* spirv, size_t sizeInBytes)
{
    std::string key = "spirv-cross:";
    key += MG_MTL_SPIRV_CROSS_REVISION;
    key += ";platform:macos;msl:2.0;flip-vert-y:";
    key += MG_MTL_FLIP_VERT_Y ? "1" : "0";
    key += ";deployment-target:" MGMTL_STRINGIFY(__ENVIRONMENT_MAC_OS_X_VERSION_MIN_REQUIRED__) ";spirv:";
    key.append((const char*)spirv, sizeInBytes);
    return key;
}

MGG_Shader* MGG_Shader_Create(MGG_GraphicsDevice* device, MGShaderStage stage, mgbyte* bytecode, mgint sizeInBytes)
{
    assert(device && bytecode && sizeInBytes > 0);

    double shaderStarted = CACurrentMediaTime();

    auto shader = new MGG_Shader();
    shader->stage = stage;
    shader->id = device->nextObjectId++;

    // Parse the reflection header (same layout the Vulkan backend reads).
    mgbyte* p = bytecode;
    mgint remaining = sizeInBytes;

    /*int uniformCount =*/ (void)(*(mgint*)p); p += sizeof(mgint); remaining -= sizeof(mgint);
    shader->uniformSlots = *(uint32_t*)p; p += sizeof(uint32_t); remaining -= sizeof(uint32_t);
    shader->textureSlots = *(uint32_t*)p; p += sizeof(uint32_t); remaining -= sizeof(uint32_t);
    shader->samplerSlots = *(uint32_t*)p; p += sizeof(uint32_t); remaining -= sizeof(uint32_t);

    memcpy(shader->textureTypes, p, sizeof(MGTextureType) * MAX_TEXTURE_SLOTS);
    p += sizeof(MGTextureType) * MAX_TEXTURE_SLOTS;
    remaining -= sizeof(MGTextureType) * MAX_TEXTURE_SLOTS;

    // The Vulkan descriptor-binding table follows; Metal doesn't need it, so skip it.
    // Each VkDescriptorSetLayoutBinding is 24 bytes (see ShaderProfile.Vulkan.cs).
    int bindingCount = *(mgint*)p; p += sizeof(mgint); remaining -= sizeof(mgint);
    const int kVkBindingSize = 24;
    p += bindingCount * kVkBindingSize;
    remaining -= bindingCount * kVkBindingSize;

    // Remaining bytes are the SPIR-V module.
    for (int i = 0; i < MAX_TEXTURE_SLOTS; i++)
    {
        if (shader->textureSlots & (1u << i)) shader->maxTextureSlot = i;
        if (shader->samplerSlots & (1u << i)) shader->maxSamplerSlot = i;
    }

    MGMetalShaderPayload preparedPayload;
    bool hasPreparedLibrary = MGG_TryParseMetalShaderPayload(p, remaining, preparedPayload);
    mgbyte* spirv = p;
    mgint spirvSize = hasPreparedLibrary ? static_cast<mgint>(preparedPayload.spirvSize) : remaining;
    if (spirvSize <= 0 || (spirvSize % 4) != 0)
    {
        MGMTL_Log("MGG_Shader_Create: invalid SPIR-V payload size %d", spirvSize);
        delete shader;
        return nullptr;
    }

    std::string translationKey = MGMTL_TranslationCacheKey(spirv, spirvSize);
    auto cachedTranslation = device->translations.find(translationKey);
    std::string mslSource;
    std::string entryName;
    bool translationCacheHit = cachedTranslation != device->translations.end();
    id<MTLLibrary> cachedLibrary = translationCacheHit ? cachedTranslation->second.library : nil;
    double translationMilliseconds = 0.0;
    if (translationCacheHit)
    {
        mslSource = cachedTranslation->second.source;
        entryName = cachedTranslation->second.entryPoint;
    }

    double libraryStarted = CACurrentMediaTime();
    bool preparedLibraryLoaded = false;
    @autoreleasepool
    {
        NSError* error = nil;
        id<MTLLibrary> lib = cachedLibrary;
        if (lib == nil && hasPreparedLibrary)
        {
            void* libraryBytes = malloc(preparedPayload.librarySize);
            if (libraryBytes == nullptr)
            {
                MGMTL_Log("MGG_Shader_Create: failed to allocate prepared Metal library");
                delete shader;
                return nullptr;
            }
            memcpy(libraryBytes, preparedPayload.library, preparedPayload.librarySize);
            dispatch_data_t libraryData = dispatch_data_create(
                libraryBytes,
                preparedPayload.librarySize,
                dispatch_get_global_queue(0, 0),
                DISPATCH_DATA_DESTRUCTOR_DEFAULT);
            lib = [device->mtlDevice newLibraryWithData:libraryData error:&error];
            if (lib != nil)
            {
                entryName.assign(preparedPayload.entryPoint, preparedPayload.entryPointSize);
                preparedLibraryLoaded = true;
            }
            else if (!preparedPayload.allowRuntimeFallback)
            {
                MGMTL_Log("MGG_Shader_Create: prepared Metal library rejected in strict mode: %s",
                    error ? [[error localizedDescription] UTF8String] : "unknown");
                delete shader;
                return nullptr;
            }
            else
            {
                MGMTL_Log("MGG_Shader_Create: prepared Metal library rejected; using diagnostics SPIR-V fallback: %s",
                    error ? [[error localizedDescription] UTF8String] : "unknown");
                error = nil;
            }
        }
        if (lib == nil)
        {
            if (mslSource.empty())
            {
                double translationStarted = CACurrentMediaTime();
                std::string translationError;
                if (!MGMTL_SpirvToMsl(
                    reinterpret_cast<const uint32_t*>(spirv),
                    spirvSize / 4,
                    mslSource,
                    entryName,
                    &translationError))
                {
                    MGMTL_Log("SPIRV-Cross SPIR-V->MSL failed: %s", translationError.c_str());
                    delete shader;
                    return nullptr;
                }
                translationMilliseconds = (CACurrentMediaTime() - translationStarted) * 1000.0;
                device->runtimeTranslationCount++;
                device->runtimeTranslationMilliseconds += translationMilliseconds;
            }
            MTLCompileOptions* opts = [[MTLCompileOptions alloc] init];
            lib = [device->mtlDevice newLibraryWithSource:@(mslSource.c_str()) options:opts error:&error];
        }
        if (lib == nil)
        {
            MGMTL_Log("MGG_Shader_Create: MSL compile failed: %s", error ? [[error localizedDescription] UTF8String] : "unknown");
            MGMTL_Log("---- MSL ----\n%s\n-------------", mslSource.c_str());
            delete shader;
            return nullptr;
        }
        device->translations[translationKey] = MGMTL_TranslationCacheEntry { mslSource, entryName, lib };
        shader->library = lib;
        shader->function = [lib newFunctionWithName:@(entryName.c_str())];
        if (shader->function == nil)
        {
            MGMTL_Log("MGG_Shader_Create: entry point '%s' not found", entryName.c_str());
            delete shader;
            return nullptr;
        }
    }
    double libraryMilliseconds = (CACurrentMediaTime() - libraryStarted) * 1000.0;

    if (device->perf)
    {
        MGMTL_Log("[metal-perf] shader=%llu prepared-library=%s translation-cache=%s library-cache=%s translation=%.3fms library=%.3fms",
            shader->id, preparedLibraryLoaded ? "loaded" : (hasPreparedLibrary ? (cachedLibrary != nil ? "cached" : "fallback") : "none"),
            translationCacheHit ? "hit" : "miss", cachedLibrary != nil ? "hit" : "miss",
            translationMilliseconds, libraryMilliseconds);
    }

    device->shaderCreationCount++;
    device->shaderCreationMilliseconds += (CACurrentMediaTime() - shaderStarted) * 1000.0;
    return shader;
}

void MGG_Shader_Destroy(MGG_GraphicsDevice* device, MGG_Shader* shader)
{
    if (shader) { shader->function = nil; shader->library = nil; delete shader; }
}

// ===========================================================================================
// Bind setters
// ===========================================================================================

void MGG_GraphicsDevice_SetConstantBuffer(MGG_GraphicsDevice* device, MGShaderStage stage, mgint slot, MGG_Buffer* buffer)
{
    // Only slot 0 is supported (single cbuffer per stage), matching the other backends.
    device->uniforms[(int)stage] = buffer;
    device->bindingsDirty = true;
}

void MGG_GraphicsDevice_SetTexture(MGG_GraphicsDevice* device, MGShaderStage stage, mgint slot, MGG_Texture* texture)
{
    if (slot < 0 || slot >= MAX_TEXTURE_SLOTS) return;
    device->textures[(int)stage][slot] = texture;
    device->bindingsDirty = true;
}

void MGG_GraphicsDevice_SetSamplerState(MGG_GraphicsDevice* device, MGShaderStage stage, mgint slot, MGG_SamplerState* state)
{
    if (slot < 0 || slot >= MAX_TEXTURE_SLOTS) return;
    device->samplers[(int)stage][slot] = state;
    device->bindingsDirty = true;
}

void MGG_GraphicsDevice_SetIndexBuffer(MGG_GraphicsDevice* device, MGIndexElementSize size, MGG_Buffer* buffer)
{
    device->indexBuffer = buffer;
    device->indexBufferSize = size;
    device->indexDirty = true;
}

void MGG_GraphicsDevice_SetVertexBuffer(MGG_GraphicsDevice* device, mgint slot, MGG_Buffer* buffer, mgint vertexOffset)
{
    if (slot < 0 || slot >= MAX_VERTEX_BUFFERS) return;
    device->vertexBuffers[slot] = buffer;
    device->vertexOffsets[slot] = (uint32_t)vertexOffset;
    device->vertexDirty |= (1u << slot);
}

void MGG_GraphicsDevice_SetShader(MGG_GraphicsDevice* device, MGShaderStage stage, MGG_Shader* shader)
{
    if (device->shaders[(int)stage] != shader)
    {
        device->shaders[(int)stage] = shader;
        device->pipelineDirty = true;
        device->bindingsDirty = true;
    }
}

void MGG_GraphicsDevice_SetInputLayout(MGG_GraphicsDevice* device, MGG_InputLayout* layout)
{
    if (device->inputLayout != layout)
    {
        device->inputLayout = layout;
        device->pipelineDirty = true;
    }
}

// ===========================================================================================
// Pipeline cache + apply state + draw
// ===========================================================================================

static id<MTLRenderPipelineState> MGMTL_GetPipeline(MGG_GraphicsDevice* device)
{
    MGG_Shader* vs = device->shaders[(int)MGShaderStage::Vertex];
    MGG_Shader* ps = device->shaders[(int)MGShaderStage::Pixel];
    MGG_InputLayout* layout = device->inputLayout;
    if (vs == nullptr) return nil;

    // Determine attachment formats + sample count.
    MTLPixelFormat colorFormats[8] = {};
    int colorCount = 0;
    MTLPixelFormat depthFmt = MTLPixelFormatInvalid;
    int sampleCount = 1;

    if (device->usingBackbuffer)
    {
        colorFormats[0] = device->backbufferFormat;
        colorCount = 1;
        depthFmt = device->backbufferDepthFormat;
        sampleCount = device->multiSampleCount;
    }
    else
    {
        colorCount = device->colorTargetCount;
        for (int i = 0; i < colorCount && i < 8; i++)
            colorFormats[i] = ToMTLPixelFormat(device->colorTargets[i]->format);
        if (device->depthTarget) depthFmt = ToMTLDepthFormat(device->depthTarget->depthFormat);
        sampleCount = (colorCount > 0 && device->colorTargets[0]) ? device->colorTargets[0]->multiSampleCount : 1;
    }
    if (sampleCount < 1) sampleCount = 1;

    // Cache key.
    uint64_t h = 1469598103934665603ULL;
    uint64_t vsId = vs->id; uint64_t psId = ps ? ps->id : 0;
    uint64_t layId = layout ? layout->id : 0;
    uint64_t blendId = device->blendState ? device->blendState->id : 0;
    h = MGMTL_HashValue(vsId, h);
    h = MGMTL_HashValue(psId, h);
    h = MGMTL_HashValue(layId, h);
    h = MGMTL_HashValue(blendId, h);
    h = MGMTL_HashValue(depthFmt, h);
    h = MGMTL_HashValue(sampleCount, h);
    h = MGMTL_HashValue(colorCount, h);
    for (int i = 0; i < colorCount; i++) h = MGMTL_HashValue(colorFormats[i], h);

    auto it = device->pipelines.find(h);
    if (it != device->pipelines.end())
    {
        device->pipelineCacheHits++;
        return it->second.pipeline;
    }

    device->pipelineCacheMisses++;

    MTLRenderPipelineDescriptor* pd = [[MTLRenderPipelineDescriptor alloc] init];
    pd.vertexFunction = vs->function;
    pd.fragmentFunction = ps ? ps->function : nil;
    if (layout && layout->descriptor) pd.vertexDescriptor = layout->descriptor;
    pd.rasterSampleCount = sampleCount;

    for (int i = 0; i < colorCount; i++)
    {
        MTLRenderPipelineColorAttachmentDescriptor* ca = pd.colorAttachments[i];
        ca.pixelFormat = colorFormats[i];

        MGG_BlendState_Info bi = device->blendState ? device->blendState->infos[i] : MGG_BlendState_Info{};
        bool blendState_ok = device->blendState != nullptr;
        bool opaque = !blendState_ok ||
            (bi.colorSourceBlend == MGBlend::One && bi.colorDestBlend == MGBlend::Zero &&
             bi.alphaSourceBlend == MGBlend::One && bi.alphaDestBlend == MGBlend::Zero);

        ca.blendingEnabled = !opaque;
        if (!opaque)
        {
            ca.sourceRGBBlendFactor = ToMTLBlendFactor(bi.colorSourceBlend);
            ca.destinationRGBBlendFactor = ToMTLBlendFactor(bi.colorDestBlend);
            ca.rgbBlendOperation = ToMTLBlendOperation(bi.colorBlendFunc);
            ca.sourceAlphaBlendFactor = ToMTLBlendFactor(bi.alphaSourceBlend);
            ca.destinationAlphaBlendFactor = ToMTLBlendFactor(bi.alphaDestBlend);
            ca.alphaBlendOperation = ToMTLBlendOperation(bi.alphaBlendFunc);
        }
        ca.writeMask = blendState_ok ? ToMTLColorWriteMask(bi.colorWriteChannels) : MTLColorWriteMaskAll;
    }

    if (depthFmt != MTLPixelFormatInvalid)
    {
        pd.depthAttachmentPixelFormat = depthFmt;
        if (depthFmt == MTLPixelFormatDepth32Float_Stencil8)
            pd.stencilAttachmentPixelFormat = depthFmt;
    }

    NSError* error = nil;
    double t0 = CACurrentMediaTime();
    id<MTLRenderPipelineState> pso = [device->mtlDevice newRenderPipelineStateWithDescriptor:pd error:&error];
    if (pso == nil)
    {
        MGMTL_Log("MGG_GetPipeline: pipeline creation failed: %s", error ? [[error localizedDescription] UTF8String] : "unknown");
        return nil;
    }

    double compileMilliseconds = (CACurrentMediaTime() - t0) * 1000.0;
    device->pipelineCreationCount++;
    device->pipelineCreationMilliseconds += compileMilliseconds;

    if (device->perf)
    {
        device->lastCompileMs = compileMilliseconds;
        device->lastCompileFrame = device->perfFrame;
        MGMTL_Log("[metal-perf] frame %llu: compiled render pipeline #%zu in %.2f ms (target=%s)",
                  (unsigned long long)device->perfFrame, device->pipelines.size() + 1, compileMilliseconds,
                  device->usingBackbuffer ? "backbuffer" : "RT");
    }

    device->pipelines[h] = MGMTL_PipelineCacheEntry { pso };
    return pso;
}

static void MGMTL_ApplyState(MGG_GraphicsDevice* device, MGPrimitiveType primitiveType)
{
    device->drewThisFrame = true;
    id<MTLRenderCommandEncoder> enc = device->encoder;

    // Pipeline.
    id<MTLRenderPipelineState> pso = MGMTL_GetPipeline(device);
    if (pso != nil)
        [enc setRenderPipelineState:pso];
    device->pipelineDirty = false;

    // Depth-stencil state.
    if (device->depthStencilState && device->depthStencilState->state)
    {
        [enc setDepthStencilState:device->depthStencilState->state];
        [enc setStencilReferenceValue:(uint32_t)device->depthStencilState->referenceStencil];
    }
    device->depthStencilDirty = false;

    // Rasterizer.
    if (device->rasterizerState)
    {
        MGG_RasterizerState* rs = device->rasterizerState;
        [enc setCullMode:ToMTLCullMode(rs->cullMode)];
        [enc setTriangleFillMode:ToMTLFillMode(rs->fillMode)];
        [enc setDepthBias:rs->depthBias slopeScale:rs->slopeScaleDepthBias clamp:0.0f];
    }
    device->rasterDirty = false;

    // Blend factor.
    [enc setBlendColorRed:device->blendFactor[0] green:device->blendFactor[1]
                     blue:device->blendFactor[2] alpha:device->blendFactor[3]];
    device->blendFactorDirty = false;

    // Viewport.
    [enc setViewport:device->viewport];
    device->viewportDirty = false;

    // Scissor.
    {
        bool scissorTest = device->rasterizerState && device->rasterizerState->scissorTestEnable;
        MTLScissorRect r;
        int fbW = device->usingBackbuffer ? device->backbufferWidth :
                  (device->colorTargetCount > 0 ? device->colorTargets[0]->width : device->backbufferWidth);
        int fbH = device->usingBackbuffer ? device->backbufferHeight :
                  (device->colorTargetCount > 0 ? device->colorTargets[0]->height : device->backbufferHeight);
        if (scissorTest && device->scissorSet)
            r = device->scissor;
        else { r.x = 0; r.y = 0; r.width = fbW; r.height = fbH; }
        // Clamp to the attachment (Metal validates scissor within render target).
        if ((int)(r.x + r.width) > fbW)  r.width  = (r.x < (NSUInteger)fbW) ? (fbW - r.x) : 0;
        if ((int)(r.y + r.height) > fbH) r.height = (r.y < (NSUInteger)fbH) ? (fbH - r.y) : 0;
        [enc setScissorRect:r];
    }
    device->scissorDirty = false;

    // Vertex stream buffers (bound at MG_MTL_VBO_BASE + slot).
    for (int i = 0; i < MAX_VERTEX_BUFFERS; i++)
    {
        MGG_Buffer* vb = device->vertexBuffers[i];
        if (vb && vb->buffer)
            [enc setVertexBuffer:vb->buffer offset:device->vertexOffsets[i] atIndex:(MG_MTL_VBO_BASE + i)];
    }
    device->vertexDirty = 0;

    // Constant buffers (per-draw upload).
    for (int s = 0; s < NUM_STAGES; s++)
    {
        MGG_Buffer* cb = device->uniforms[s];
        if (cb == nullptr || cb->push.empty()) continue;
        if (s == (int)MGShaderStage::Vertex)
            [enc setVertexBytes:cb->push.data() length:cb->push.size() atIndex:MG_MTL_CBUFFER_INDEX];
        else
            [enc setFragmentBytes:cb->push.data() length:cb->push.size() atIndex:MG_MTL_CBUFFER_INDEX];
    }

    // Textures + samplers, for the slots each stage's shader actually uses.
    for (int s = 0; s < NUM_STAGES; s++)
    {
        MGG_Shader* sh = device->shaders[s];
        if (sh == nullptr) continue;
        for (int slot = 0; slot <= sh->maxTextureSlot; slot++)
        {
            if (!(sh->textureSlots & (1u << slot))) continue;
            MGG_Texture* t = device->textures[s][slot];
            id<MTLTexture> mt = (t && t->texture) ? t->texture : device->nullTexture;
            if (s == (int)MGShaderStage::Vertex) [enc setVertexTexture:mt atIndex:slot];
            else                                  [enc setFragmentTexture:mt atIndex:slot];
        }
        for (int slot = 0; slot <= sh->maxSamplerSlot; slot++)
        {
            if (!(sh->samplerSlots & (1u << slot))) continue;
            MGG_SamplerState* ss = device->samplers[s][slot];
            id<MTLSamplerState> ms = (ss && ss->state) ? ss->state : device->nullSampler;
            if (s == (int)MGShaderStage::Vertex) [enc setVertexSamplerState:ms atIndex:slot];
            else                                  [enc setFragmentSamplerState:ms atIndex:slot];
        }
    }
    device->bindingsDirty = false;
}

static int MGMTL_GetIndexCount(MGPrimitiveType type, int primitiveCount)
{
    switch (type)
    {
    case MGPrimitiveType::TriangleList:  return primitiveCount * 3;
    case MGPrimitiveType::TriangleStrip: return primitiveCount + 2;
    case MGPrimitiveType::LineList:      return primitiveCount * 2;
    case MGPrimitiveType::LineStrip:     return primitiveCount + 1;
    case MGPrimitiveType::PointList:     return primitiveCount;
    default:                             return primitiveCount * 3;
    }
}

mgbool MGG_GraphicsDevice_PrewarmCurrentPipeline(MGG_GraphicsDevice* device, MGPrimitiveType primitiveType)
{
    return MGMTL_GetPipeline(device) != nil;
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
    assert(device != nullptr);
    device->lastPipelineCacheStatus = dataBytes <= 0 ? MGPipelineCacheStatus::Empty : MGPipelineCacheStatus::Unsupported;
    device->pipelineCacheRejections++;
    return device->lastPipelineCacheStatus;
}

void MGG_GraphicsDevice_Draw(MGG_GraphicsDevice* device, MGPrimitiveType primitiveType, mgint vertexStart, mgint vertexCount)
{
    if (vertexCount <= 0) return;
    if (!MGMTL_EnsureEncoder(device)) return;
    MGMTL_ApplyState(device, primitiveType);
    [device->encoder drawPrimitives:ToMTLPrimitiveType(primitiveType) vertexStart:vertexStart vertexCount:vertexCount];
}

void MGG_GraphicsDevice_DrawIndexed(MGG_GraphicsDevice* device, MGPrimitiveType primitiveType, mgint primitiveCount, mgint indexStart, mgint vertexStart)
{
    if (primitiveCount <= 0) return;
    if (!MGMTL_EnsureEncoder(device)) return;
    MGG_Buffer* ib = device->indexBuffer;
    if (ib == nullptr || ib->buffer == nil) return;
    MGMTL_ApplyState(device, primitiveType);

    int indexCount = MGMTL_GetIndexCount(primitiveType, primitiveCount);
    int indexBytes = device->indexBufferSize == MGIndexElementSize::ThirtyTwoBits ? 4 : 2;
    [device->encoder drawIndexedPrimitives:ToMTLPrimitiveType(primitiveType)
                                indexCount:indexCount
                                 indexType:ToMTLIndexType(device->indexBufferSize)
                               indexBuffer:ib->buffer
                                                 indexBufferOffset:(NSUInteger)indexStart * indexBytes
                                                         instanceCount:1
                                                                baseVertex:vertexStart
                                                            baseInstance:0];
}

void MGG_GraphicsDevice_DrawIndexedInstanced(MGG_GraphicsDevice* device, MGPrimitiveType primitiveType, mgint primitiveCount, mgint indexStart, mgint vertexStart, mgint instanceCount)
{
    if (primitiveCount <= 0 || instanceCount <= 0) return;
    if (!MGMTL_EnsureEncoder(device)) return;
    MGG_Buffer* ib = device->indexBuffer;
    if (ib == nullptr || ib->buffer == nil) return;
    MGMTL_ApplyState(device, primitiveType);

    int indexCount = MGMTL_GetIndexCount(primitiveType, primitiveCount);
    int indexBytes = device->indexBufferSize == MGIndexElementSize::ThirtyTwoBits ? 4 : 2;
    [device->encoder drawIndexedPrimitives:ToMTLPrimitiveType(primitiveType)
                                indexCount:indexCount
                                 indexType:ToMTLIndexType(device->indexBufferSize)
                               indexBuffer:ib->buffer
                         indexBufferOffset:(NSUInteger)indexStart * indexBytes
                                                         instanceCount:instanceCount
                                                                baseVertex:vertexStart
                                                            baseInstance:0];
}

// ===========================================================================================
// Occlusion queries (visibility result buffer)
// ===========================================================================================

MGG_OcclusionQuery* MGG_OcclusionQuery_Create(MGG_GraphicsDevice* device)
{
    auto query = new MGG_OcclusionQuery();
    const int kMaxQueries = 256;
    if (device->visibilityBuffer == nil)
    {
        device->visibilityBuffer = [device->mtlDevice newBufferWithLength:sizeof(uint64_t) * kMaxQueries
                                                                  options:MTLResourceStorageModeShared];
    }
    query->index = device->visibilityCount++ % kMaxQueries;
    return query;
}

void MGG_OcclusionQuery_Destroy(MGG_GraphicsDevice* device, MGG_OcclusionQuery* query) { delete query; }

void MGG_OcclusionQuery_Begin(MGG_GraphicsDevice* device, MGG_OcclusionQuery* query)
{
    query->inBeginEnd = true;
    if (MGMTL_EnsureEncoder(device))
        [device->encoder setVisibilityResultMode:MTLVisibilityResultModeCounting offset:(NSUInteger)query->index * sizeof(uint64_t)];
}

void MGG_OcclusionQuery_End(MGG_GraphicsDevice* device, MGG_OcclusionQuery* query)
{
    query->inBeginEnd = false;
    if (device->encoder != nil)
        [device->encoder setVisibilityResultMode:MTLVisibilityResultModeDisabled offset:0];
}

mgbyte MGG_OcclusionQuery_GetResult(MGG_GraphicsDevice* device, MGG_OcclusionQuery* query, mgint& pixelCount)
{
    if (device->visibilityBuffer != nil && query->index >= 0)
    {
        uint64_t* counters = (uint64_t*)device->visibilityBuffer.contents;
        pixelCount = (mgint)counters[query->index];
    }
    else pixelCount = 0;
    return 1; // result available
}
