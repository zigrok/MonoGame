#include "api_MGG.h"
#include <SDL3/SDL.h>
#include <GLES3/gl3.h>
#include <emscripten/html5.h>
#include <algorithm>
#include <array>
#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <string>
#include <vector>
#include "AlphaTestEffect.gl.mgfxo.h"
#include "BasicEffect.gl.mgfxo.h"
#include "DualTextureEffect.gl.mgfxo.h"
#include "EnvironmentMapEffect.gl.mgfxo.h"
#include "SkinnedEffect.gl.mgfxo.h"
#include "SpriteEffect.gl.mgfxo.h"
#include "mg_effect.h"

namespace
{
bool contextLost=false;
EM_BOOL lostContext(int,const void*,void*) { contextLost=true;return EM_TRUE; }
[[noreturn]] void fail(const char* message)
{
    std::fprintf(stderr, "MonoGame BrowserGL: %s\n", message);
    std::abort();
}
void require(bool condition, const char* message) { if (!condition) fail(message); }
GLenum primitive(MGPrimitiveType type)
{
    switch (type) {
    case MGPrimitiveType::TriangleList: return GL_TRIANGLES;
    case MGPrimitiveType::TriangleStrip: return GL_TRIANGLE_STRIP;
    case MGPrimitiveType::LineList: return GL_LINES;
    case MGPrimitiveType::LineStrip: return GL_LINE_STRIP;
    case MGPrimitiveType::PointList: return GL_POINTS;
    default: fail("Unsupported primitive type");
    }
}
int vertices(MGPrimitiveType type, int count)
{
    switch (type) {
    case MGPrimitiveType::TriangleList: return count * 3;
    case MGPrimitiveType::TriangleStrip: return count + 2;
    case MGPrimitiveType::LineList: return count * 2;
    case MGPrimitiveType::LineStrip: return count + 1;
    case MGPrimitiveType::PointList: return count;
    default: fail("Unsupported primitive type");
    }
}
GLenum comparison(MGCompareFunction value)
{
    static const GLenum values[] = {GL_ALWAYS,GL_NEVER,GL_LESS,GL_LEQUAL,GL_EQUAL,GL_GEQUAL,GL_GREATER,GL_NOTEQUAL};
    require(static_cast<unsigned>(value) < 8, "Invalid comparison");
    return values[static_cast<int>(value)];
}
GLenum blend(MGBlend value)
{
    static const GLenum values[] = {GL_ONE,GL_ZERO,GL_SRC_COLOR,GL_ONE_MINUS_SRC_COLOR,GL_SRC_ALPHA,GL_ONE_MINUS_SRC_ALPHA,
        GL_DST_COLOR,GL_ONE_MINUS_DST_COLOR,GL_DST_ALPHA,GL_ONE_MINUS_DST_ALPHA,GL_CONSTANT_COLOR,GL_ONE_MINUS_CONSTANT_COLOR,GL_SRC_ALPHA_SATURATE};
    require(static_cast<unsigned>(value) < 13, "Invalid blend factor");
    return values[static_cast<int>(value)];
}
GLenum equation(MGBlendFunction value)
{
    static const GLenum values[] = {GL_FUNC_ADD,GL_FUNC_SUBTRACT,GL_FUNC_REVERSE_SUBTRACT,GL_MIN,GL_MAX};
    require(static_cast<unsigned>(value) < 5, "Invalid blend equation");
    return values[static_cast<int>(value)];
}
GLenum stencilOp(MGStencilOperation value)
{
    static const GLenum values[] = {GL_KEEP,GL_ZERO,GL_REPLACE,GL_INCR_WRAP,GL_DECR_WRAP,GL_INCR,GL_DECR,GL_INVERT};
    require(static_cast<unsigned>(value) < 8, "Invalid stencil operation");
    return values[static_cast<int>(value)];
}
GLenum address(MGTextureAddressMode value)
{
    switch (value) {
    case MGTextureAddressMode::Wrap: return GL_REPEAT;
    case MGTextureAddressMode::Clamp: return GL_CLAMP_TO_EDGE;
    case MGTextureAddressMode::Mirror: return GL_MIRRORED_REPEAT;
    default: fail("WebGL2 does not support border texture addressing");
    }
}
void enable(GLenum capability, bool enabled) { if (enabled) glEnable(capability); else glDisable(capability); }
}

struct MGG_GraphicsAdapter { MGG_DisplayMode mode{MGSurfaceFormat::Color, 800, 480}; };
struct MGG_GraphicsSystem { MGG_GraphicsAdapter adapter; };
struct MGG_Buffer { GLuint id=0; GLenum target; std::vector<mgbyte> bytes; };
struct MGG_Texture { GLuint id=0, framebuffer=0, depthBuffer=0; int width, height, levels; MGSurfaceFormat format; };
struct MGG_BlendState { MGG_BlendState_Info info; };
struct MGG_DepthStencilState { MGG_DepthStencilState_Info info; };
struct MGG_RasterizerState { MGG_RasterizerState_Info info; };
struct MGG_SamplerState { GLuint id=0; GLenum minFilter; };
struct ShaderSampler { int texture, sampler; std::string name; };
struct MGG_Shader { GLuint id=0; std::string block; std::vector<ShaderSampler> samplers; };
struct MGG_InputLayout { std::vector<int> strides; std::vector<MGG_InputElement> elements; };
struct MGG_OcclusionQuery { GLuint id=0; };
struct MGG_GraphicsDevice
{
    SDL_Window* window=nullptr;
    SDL_GLContext context=nullptr;
    int width=0,height=0,targetHeight=0,frames=0,drawableSizeMismatchCount=0;
    GLuint vao=0;
    MGG_Texture* target=nullptr;
    MGG_Shader* shaders[2]{};
    MGG_Buffer* constants[2]{};
    MGG_Buffer* index=nullptr;
    MGIndexElementSize indexSize{};
    std::array<MGG_Buffer*,16> buffers{};
    std::array<int,16> offsets{};
    MGG_Texture* textures[2][16]{};
    MGG_SamplerState* samplers[2][16]{};
    MGG_InputLayout* layout=nullptr;
    std::map<std::pair<GLuint,GLuint>,GLuint> programs;
    MGG_ShaderPipelineDiagnostics diagnostics{};
    MGG_RasterizerState_Info rasterizer{};
    bool depthWrite=true;
    int stencilWrite=255, colorWrite=15;
    int viewport[4]{}, scissor[4]{};
};

static void viewport(MGG_GraphicsDevice* d)
{
    auto* v=d->viewport;
    glViewport(v[0],d->target?v[1]:d->targetHeight-v[1]-v[3],v[2],v[3]);
    auto* s=d->scissor;
    glScissor(s[0],d->target?s[1]:d->targetHeight-s[1]-s[3],s[2],s[3]);
    glFrontFace(d->target ? GL_CW : GL_CCW);
}
static GLuint program(MGG_GraphicsDevice* d)
{
    require(d->shaders[0] && d->shaders[1], "Both shader stages must be bound");
    auto key=std::make_pair(d->shaders[0]->id,d->shaders[1]->id);
    auto found=d->programs.find(key);
    if(found!=d->programs.end()) { ++d->diagnostics.PipelineCacheHits; return found->second; }
    ++d->diagnostics.PipelineCacheMisses;
    GLuint result=glCreateProgram();
    glAttachShader(result,key.first);
    glAttachShader(result,key.second);
    glLinkProgram(result);
    GLint ok=0; glGetProgramiv(result,GL_LINK_STATUS,&ok);
    if(!ok) { char log[4096]; glGetProgramInfoLog(result,sizeof(log),nullptr,log); fail(log); }
    for(int stage=0;stage<2;++stage)
    {
        auto& block=d->shaders[stage]->block;
        if(!block.empty())
        {
            GLuint index=glGetUniformBlockIndex(result,block.c_str());
            if(index!=GL_INVALID_INDEX) glUniformBlockBinding(result,index,stage);
        }
    }
    d->programs[key]=result;
    ++d->diagnostics.PipelineCreationCount;
    return result;
}
static void prepare(MGG_GraphicsDevice* d, int baseVertex)
{
    require(d->context && d->layout, "Missing graphics context or input layout");
    GLuint p=program(d); glUseProgram(p);
    glUniform1f(glGetUniformLocation(p,"mg_targetFlip"),d->target?-1.0f:1.0f);
    int unit=0;
    for(int stage=0;stage<2;++stage)
    {
        if(d->constants[stage]) glBindBufferBase(GL_UNIFORM_BUFFER,stage,d->constants[stage]->id);
        for(const auto& binding:d->shaders[stage]->samplers)
        {
            require(unit<16,"BrowserGL supports at most 16 combined samplers");
            auto* texture=d->textures[stage][binding.texture];
            auto* sampler=d->samplers[stage][binding.sampler];
            require(texture && sampler,"Shader texture and sampler must be bound");
            require(texture!=d->target,"Cannot sample the active render target");
            glActiveTexture(GL_TEXTURE0+unit);
            glBindTexture(GL_TEXTURE_2D,texture->id);
            glBindSampler(unit,sampler->id);
            glUniform1i(glGetUniformLocation(p,binding.name.c_str()),unit++);
        }
    }
    glBindVertexArray(d->vao);
    for(int slot=0;slot<16;++slot) glDisableVertexAttribArray(slot);
    for(const auto& element:d->layout->elements)
    {
        int slot=element.VertexBufferSlot;
        require(slot<static_cast<int>(d->layout->strides.size()) && d->buffers[slot],"Missing vertex buffer");
        GLint count=0; GLenum type=GL_FLOAT; GLboolean normalized=GL_FALSE;
        switch(element.Format) {
        case MGVertexElementFormat::Single: count=1; break;
        case MGVertexElementFormat::Vector2: count=2; break;
        case MGVertexElementFormat::Vector3: count=3; break;
        case MGVertexElementFormat::Vector4: count=4; break;
        case MGVertexElementFormat::Color: count=4; type=GL_UNSIGNED_BYTE; normalized=GL_TRUE; break;
        case MGVertexElementFormat::Byte4: count=4; type=GL_UNSIGNED_BYTE; break;
        case MGVertexElementFormat::Short2: count=2; type=GL_SHORT; break;
        case MGVertexElementFormat::Short4: count=4; type=GL_SHORT; break;
        case MGVertexElementFormat::NormalizedShort2: count=2; type=GL_SHORT; normalized=GL_TRUE; break;
        case MGVertexElementFormat::NormalizedShort4: count=4; type=GL_SHORT; normalized=GL_TRUE; break;
        case MGVertexElementFormat::HalfVector2: count=2; type=GL_HALF_FLOAT; break;
        case MGVertexElementFormat::HalfVector4: count=4; type=GL_HALF_FLOAT; break;
        default: fail("Unsupported vertex element format");
        }
        int stride=d->layout->strides[slot];
        int vertex=d->offsets[slot]+(element.InstanceDataStepRate?0:baseVertex);
        intptr_t offset=static_cast<intptr_t>(vertex)*stride+element.AlignedByteOffset;
        require(offset>=0,"Negative vertex buffer offset");
        GLuint location=element.SemanticIndex;
        glBindBuffer(GL_ARRAY_BUFFER,d->buffers[slot]->id);
        glVertexAttribPointer(location,count,type,normalized,stride,reinterpret_cast<void*>(offset));
        glVertexAttribDivisor(location,element.InstanceDataStepRate);
        glEnableVertexAttribArray(location);
    }
    if(d->index) glBindBuffer(GL_ELEMENT_ARRAY_BUFFER,d->index->id);
}

MGG_GraphicsSystem* MGG_GraphicsSystem_Create() { return new MGG_GraphicsSystem(); }
void MGG_GraphicsSystem_Destroy(MGG_GraphicsSystem* s) { delete s; }
MGG_GraphicsAdapter* MGG_GraphicsAdapter_Get(MGG_GraphicsSystem* s,mgint i) { return i==0?&s->adapter:nullptr; }
void MGG_GraphicsAdapter_GetInfo(MGG_GraphicsAdapter* a,MGG_GraphicsAdaptor_Info& info)
{
    info={}; info.DeviceName=(void*)"WebGL2"; info.Description=(void*)"SDL3 OpenGL ES 3.0";
    info.DisplayModes=&a->mode; info.DisplayModeCount=1; info.CurrentDisplayMode=a->mode;
}
MGG_GraphicsDevice* MGG_GraphicsDevice_Create(MGG_GraphicsSystem*,MGG_GraphicsAdapter*) { return new MGG_GraphicsDevice(); }
void MGG_GraphicsDevice_Destroy(MGG_GraphicsDevice* d)
{
    for(auto& p:d->programs) glDeleteProgram(p.second);
    if(d->vao) glDeleteVertexArrays(1,&d->vao);
    if(d->context) SDL_GL_DestroyContext(d->context);
    delete d;
}
void MGG_GraphicsDevice_GetCaps(MGG_GraphicsDevice*,MGG_GraphicsDevice_Caps& caps) { caps={16,16,16,81}; }
void MGG_GraphicsDevice_GetShaderPipelineDiagnostics(MGG_GraphicsDevice* d,MGG_ShaderPipelineDiagnostics& v) { v=d->diagnostics; }
void MGG_GraphicsDevice_ResetShaderPipelineDiagnostics(MGG_GraphicsDevice* d) { d->diagnostics={}; }
mgbool MGG_GraphicsDevice_PrewarmCurrentPipeline(MGG_GraphicsDevice* d,MGPrimitiveType) { return program(d)!=0; }
mgint MGG_GraphicsDevice_GetPipelineCacheDataSize(MGG_GraphicsDevice*) { return 0; }
mgbool MGG_GraphicsDevice_GetPipelineCacheData(MGG_GraphicsDevice*,mgbyte*,mgint) { return false; }
MGPipelineCacheStatus MGG_GraphicsDevice_ImportPipelineCache(MGG_GraphicsDevice*,mgbyte*,mgint) { return MGPipelineCacheStatus::Unsupported; }
void MGG_GraphicsDevice_ResizeSwapchain(MGG_GraphicsDevice* d,void* window,mgint w,mgint h,MGSurfaceFormat color,MGDepthFormat,mgint samples,mgint)
{
    require(color==MGSurfaceFormat::Color && samples<=1,"Browser backbuffer supports Color without explicit multisampling");
    d->window=static_cast<SDL_Window*>(window);
    if(!d->context) {
        d->context=SDL_GL_CreateContext(d->window);
        require(d->context,SDL_GetError());
        contextLost=false;
        emscripten_set_webglcontextlost_callback("#canvas",nullptr,EM_TRUE,lostContext);
        glGenVertexArrays(1,&d->vao); glBindVertexArray(d->vao);
    }
    SDL_SetWindowSize(d->window,w,h);
    SDL_GetWindowSizeInPixels(d->window,&d->width,&d->height);
    if(d->width!=w || d->height!=h)++d->drawableSizeMismatchCount;
    if(!d->target) d->targetHeight=d->height;
}
MG_EXPORT mgbool MGG_Browser_IsContextLost() { return contextLost; }
void MGG_GraphicsDevice_GetBackBufferSize(MGG_GraphicsDevice* d,mgint& w,mgint& h) { w=d->width;h=d->height; }
mgint MGG_GraphicsDevice_GetDrawableSizeMismatchCount(MGG_GraphicsDevice* d) { return d->drawableSizeMismatchCount; }
mgint MGG_GraphicsDevice_BeginFrame(MGG_GraphicsDevice* d)
{
    require(SDL_GL_MakeCurrent(d->window,d->context),SDL_GetError());
    return ++d->frames;
}
void MGG_GraphicsDevice_Clear(MGG_GraphicsDevice* d,MGClearOptions options,Vector4& color,mgfloat depth,mgint stencil)
{
    int mask=static_cast<int>(options); GLbitfield bits=0;
    if(mask&1) { glColorMask(1,1,1,1);glClearColor(color.X,color.Y,color.Z,color.W);bits|=GL_COLOR_BUFFER_BIT; }
    if(mask&2) { glDepthMask(1);glClearDepthf(depth);bits|=GL_DEPTH_BUFFER_BIT; }
    if(mask&4) { glStencilMask(255);glClearStencil(stencil);bits|=GL_STENCIL_BUFFER_BIT; }
    glDisable(GL_SCISSOR_TEST);glClear(bits);
    enable(GL_SCISSOR_TEST,d->rasterizer.scissorTestEnable);
    glDepthMask(d->depthWrite);glStencilMask(d->stencilWrite);
    glColorMask(d->colorWrite&1,d->colorWrite&2,d->colorWrite&4,d->colorWrite&8);
}
void MGG_GraphicsDevice_Present(MGG_GraphicsDevice* d,mgint,mgint) { SDL_GL_SwapWindow(d->window); }
void MGG_GraphicsDevice_SetBlendState(MGG_GraphicsDevice* d,MGG_BlendState* state,mgfloat r,mgfloat g,mgfloat b,mgfloat a)
{
    auto& s=state->info; glEnable(GL_BLEND); glBlendColor(r,g,b,a);
    glBlendFuncSeparate(blend(s.colorSourceBlend),blend(s.colorDestBlend),blend(s.alphaSourceBlend),blend(s.alphaDestBlend));
    glBlendEquationSeparate(equation(s.colorBlendFunc),equation(s.alphaBlendFunc));
    d->colorWrite=static_cast<int>(s.colorWriteChannels);
    glColorMask(d->colorWrite&1,d->colorWrite&2,d->colorWrite&4,d->colorWrite&8);
}
void MGG_GraphicsDevice_SetDepthStencilState(MGG_GraphicsDevice* d,MGG_DepthStencilState* state)
{
    auto& s=state->info; enable(GL_DEPTH_TEST,s.depthBufferEnable);glDepthFunc(comparison(s.depthBufferFunction));
    d->depthWrite=s.depthBufferWriteEnable;glDepthMask(d->depthWrite);enable(GL_STENCIL_TEST,s.stencilEnable);
    glStencilFunc(comparison(s.stencilFunction),s.referenceStencil,s.stencilMask);
    glStencilOp(stencilOp(s.stencilFail),stencilOp(s.stencilDepthBufferFail),stencilOp(s.stencilPass));
    d->stencilWrite=s.stencilWriteMask;glStencilMask(d->stencilWrite);
}
void MGG_GraphicsDevice_SetRasterizerState(MGG_GraphicsDevice* d,MGG_RasterizerState* state)
{
    auto& s=state->info; require(s.fillMode==MGFillMode::Solid && s.depthClipEnable,"WebGL2 requires solid fill and depth clipping");
    d->rasterizer=s; enable(GL_SCISSOR_TEST,s.scissorTestEnable); enable(GL_CULL_FACE,s.cullMode!=MGCullMode::None);
    glCullFace(s.cullMode==MGCullMode::CullClockwiseFace?GL_BACK:GL_FRONT);
    enable(GL_POLYGON_OFFSET_FILL,s.depthBias!=0 || s.slopeScaleDepthBias!=0);
    glPolygonOffset(s.slopeScaleDepthBias,s.depthBias); viewport(d);
}
// Browsers have no television overscan; the caller's viewport is already the safe area.
void MGG_GraphicsDevice_GetTitleSafeArea(mgint&,mgint&,mgint&,mgint&) {}
void MGG_GraphicsDevice_SetViewport(MGG_GraphicsDevice* d,mgint x,mgint y,mgint w,mgint h,mgfloat min,mgfloat max)
{ d->viewport[0]=x;d->viewport[1]=y;d->viewport[2]=w;d->viewport[3]=h;glDepthRangef(min,max);viewport(d); }
void MGG_GraphicsDevice_SetScissorRectangle(MGG_GraphicsDevice* d,mgint x,mgint y,mgint w,mgint h)
{ d->scissor[0]=x;d->scissor[1]=y;d->scissor[2]=w;d->scissor[3]=h;viewport(d); }
void MGG_GraphicsDevice_SetRenderTargets(MGG_GraphicsDevice* d,MGG_Texture** targets,mgint* slices,mgint count)
{
    require(count<=1 && (!count || slices[0]==0),"BrowserGL currently supports one 2D render target");
    d->target=count?targets[0]:nullptr; require(!d->target || d->target->framebuffer,"Texture is not a render target");
    glBindFramebuffer(GL_FRAMEBUFFER,d->target?d->target->framebuffer:0);
    d->targetHeight=d->target?d->target->height:d->height;viewport(d);
}
void MGG_GraphicsDevice_SetConstantBuffer(MGG_GraphicsDevice* d,MGShaderStage s,mgint slot,MGG_Buffer* b)
{ require(slot==0,"BrowserGL supports one constant buffer per stage");d->constants[static_cast<int>(s)]=b; }
void MGG_GraphicsDevice_SetTexture(MGG_GraphicsDevice* d,MGShaderStage s,mgint slot,MGG_Texture* t) { require(slot>=0&&slot<16,"Invalid texture slot");d->textures[static_cast<int>(s)][slot]=t; }
void MGG_GraphicsDevice_SetSamplerState(MGG_GraphicsDevice* d,MGShaderStage s,mgint slot,MGG_SamplerState* t) { require(slot>=0&&slot<16,"Invalid sampler slot");d->samplers[static_cast<int>(s)][slot]=t; }
void MGG_GraphicsDevice_SetIndexBuffer(MGG_GraphicsDevice* d,MGIndexElementSize s,MGG_Buffer* b) { d->index=b;d->indexSize=s; }
void MGG_GraphicsDevice_SetVertexBuffer(MGG_GraphicsDevice* d,mgint slot,MGG_Buffer* b,mgint offset) { require(slot>=0&&slot<16,"Invalid vertex buffer slot");d->buffers[slot]=b;d->offsets[slot]=offset; }
void MGG_GraphicsDevice_SetShader(MGG_GraphicsDevice* d,MGShaderStage s,MGG_Shader* shader) { d->shaders[static_cast<int>(s)]=shader; }
void MGG_GraphicsDevice_SetInputLayout(MGG_GraphicsDevice* d,MGG_InputLayout* layout) { d->layout=layout; }
void MGG_GraphicsDevice_Draw(MGG_GraphicsDevice* d,MGPrimitiveType type,mgint start,mgint count) { prepare(d,0);glDrawArrays(primitive(type),start,count); }
void MGG_GraphicsDevice_DrawIndexed(MGG_GraphicsDevice* d,MGPrimitiveType type,mgint count,mgint start,mgint baseVertex)
{
    prepare(d,baseVertex);require(d->index,"Missing index buffer");
    bool wide=d->indexSize==MGIndexElementSize::ThirtyTwoBits;
    glDrawElements(primitive(type),vertices(type,count),wide?GL_UNSIGNED_INT:GL_UNSIGNED_SHORT,reinterpret_cast<void*>(static_cast<intptr_t>(start)*(wide?4:2)));
}
void MGG_GraphicsDevice_DrawIndexedInstanced(MGG_GraphicsDevice* d,MGPrimitiveType type,mgint count,mgint start,mgint baseVertex,mgint instances)
{
    prepare(d,baseVertex);require(d->index,"Missing index buffer");
    bool wide=d->indexSize==MGIndexElementSize::ThirtyTwoBits;
    glDrawElementsInstanced(primitive(type),vertices(type,count),wide?GL_UNSIGNED_INT:GL_UNSIGNED_SHORT,reinterpret_cast<void*>(static_cast<intptr_t>(start)*(wide?4:2)),instances);
}
void MGG_GraphicsDevice_ResolveRenderTargets(MGG_GraphicsDevice* d)
{
    if(d->target && d->target->levels>1) { glBindTexture(GL_TEXTURE_2D,d->target->id);glGenerateMipmap(GL_TEXTURE_2D); }
}
void MGG_GraphicsDevice_GetBackBufferData(MGG_GraphicsDevice* d,mgint x,mgint y,mgint w,mgint h,void* data,mgint count,mgint bytes)
{
    require(static_cast<int64_t>(count)*bytes>=static_cast<int64_t>(w)*h*4,"Backbuffer read buffer too small");std::vector<mgbyte> pixels(w*h*4);
    glBindFramebuffer(GL_FRAMEBUFFER,0);glReadPixels(x,d->height-y-h,w,h,GL_RGBA,GL_UNSIGNED_BYTE,pixels.data());
    for(int row=0;row<h;++row) std::memcpy(static_cast<mgbyte*>(data)+row*w*4,pixels.data()+(h-row-1)*w*4,w*4);
    glBindFramebuffer(GL_FRAMEBUFFER,d->target?d->target->framebuffer:0);
}
MGG_BlendState* MGG_BlendState_Create(MGG_GraphicsDevice*,MGG_BlendState_Info* i) { return new MGG_BlendState{*i}; }
void MGG_BlendState_Destroy(MGG_GraphicsDevice*,MGG_BlendState* s) { delete s; }
MGG_DepthStencilState* MGG_DepthStencilState_Create(MGG_GraphicsDevice*,MGG_DepthStencilState_Info* i) { return new MGG_DepthStencilState{*i}; }
void MGG_DepthStencilState_Destroy(MGG_GraphicsDevice*,MGG_DepthStencilState* s) { delete s; }
MGG_RasterizerState* MGG_RasterizerState_Create(MGG_GraphicsDevice*,MGG_RasterizerState_Info* i) { return new MGG_RasterizerState{*i}; }
void MGG_RasterizerState_Destroy(MGG_GraphicsDevice*,MGG_RasterizerState* s) { delete s; }
MGG_SamplerState* MGG_SamplerState_Create(MGG_GraphicsDevice*,MGG_SamplerState_Info* s)
{
    require(s->FilterMode==MGTextureFilterMode::Default && s->MipMapLevelOfDetailBias==0,"WebGL2 sampler comparison/LOD bias unsupported by BrowserGL profile");
    auto* result=new MGG_SamplerState();glGenSamplers(1,&result->id);
    glSamplerParameteri(result->id,GL_TEXTURE_WRAP_S,address(s->AddressU));glSamplerParameteri(result->id,GL_TEXTURE_WRAP_T,address(s->AddressV));
    static const GLenum mins[]={GL_LINEAR_MIPMAP_LINEAR,GL_NEAREST_MIPMAP_NEAREST,GL_LINEAR_MIPMAP_LINEAR,GL_LINEAR_MIPMAP_NEAREST,GL_NEAREST_MIPMAP_LINEAR,
        GL_LINEAR_MIPMAP_LINEAR,GL_LINEAR_MIPMAP_NEAREST,GL_NEAREST_MIPMAP_LINEAR,GL_NEAREST_MIPMAP_NEAREST};
    static const GLenum mags[]={GL_LINEAR,GL_NEAREST,GL_LINEAR,GL_LINEAR,GL_NEAREST,GL_NEAREST,GL_NEAREST,GL_LINEAR,GL_LINEAR};
    int filter=static_cast<int>(s->Filter);require(filter>=0&&filter<9,"Invalid sampler filter");
    require(s->Filter!=MGTextureFilter::Anisotropic,"BrowserGL anisotropic filtering requires an optional extension and is not supported");
    result->minFilter=mins[filter];glSamplerParameteri(result->id,GL_TEXTURE_MIN_FILTER,result->minFilter);
    glSamplerParameteri(result->id,GL_TEXTURE_MAG_FILTER,mags[filter]);glSamplerParameterf(result->id,GL_TEXTURE_MIN_LOD,s->MaxMipLevel);
    return result;
}
void MGG_SamplerState_Destroy(MGG_GraphicsDevice* d,MGG_SamplerState* s)
{
    for(auto& stage:d->samplers)for(auto& bound:stage)if(bound==s)bound=nullptr;
    glDeleteSamplers(1,&s->id);delete s;
}
MGG_Buffer* MGG_Buffer_Create(MGG_GraphicsDevice*,MGBufferType type,mgbool dynamic,mgint bytes)
{
    require(bytes>0,"Invalid buffer length");auto* b=new MGG_Buffer();b->bytes.resize(bytes);
    b->target=type==MGBufferType::Constant?GL_UNIFORM_BUFFER:type==MGBufferType::Index?GL_ELEMENT_ARRAY_BUFFER:GL_ARRAY_BUFFER;
    glGenBuffers(1,&b->id);glBindBuffer(b->target,b->id);glBufferData(b->target,bytes,nullptr,dynamic?GL_DYNAMIC_DRAW:GL_STATIC_DRAW);return b;
}
void MGG_Buffer_Destroy(MGG_GraphicsDevice* d,MGG_Buffer* b)
{
    for(auto& bound:d->buffers)if(bound==b)bound=nullptr;
    for(auto& bound:d->constants)if(bound==b)bound=nullptr;
    if(d->index==b)d->index=nullptr;
    glDeleteBuffers(1,&b->id);delete b;
}
void MGG_Buffer_SetData(MGG_GraphicsDevice*,MGG_Buffer*& b,mgint offset,mgbyte* data,mgint count,mgint stride,mgint elementSize,mgbool discard)
{
    require(offset>=0&&count>=0&&stride>=elementSize&&elementSize>0,"Invalid buffer upload range");
    size_t end=static_cast<size_t>(offset)+(count?static_cast<size_t>(count-1)*stride+elementSize:0);
    require(end<=b->bytes.size(),"Buffer upload exceeds allocation");
    for(int i=0;i<count;++i) std::memcpy(b->bytes.data()+offset+i*stride,data+i*elementSize,elementSize);
    glBindBuffer(b->target,b->id);if(discard) glBufferData(b->target,b->bytes.size(),nullptr,GL_DYNAMIC_DRAW);
    if(count) glBufferSubData(b->target,offset,end-offset,b->bytes.data()+offset);
}
void MGG_Buffer_GetData(MGG_GraphicsDevice*,MGG_Buffer* b,mgint offset,mgbyte* data,mgint count,mgint bytes,mgint stride)
{
    require(count>0&&bytes>0&&offset>=0,"Invalid buffer read range");int element=bytes;
    require(static_cast<size_t>(offset)+static_cast<size_t>(count-1)*stride+element<=b->bytes.size(),"Buffer read exceeds allocation");
    for(int i=0;i<count;++i) std::memcpy(data+i*element,b->bytes.data()+offset+i*stride,element);
}
MGG_Texture* MGG_Texture_Create(MGG_GraphicsDevice*,MGTextureType type,MGSurfaceFormat format,mgint w,mgint h,mgint depth,mgint levels,mgint slices)
{
    require(type==MGTextureType::_2D && depth<=1 && slices<=1,"BrowserGL supports 2D textures, not volumes/cubes/arrays");
    require(format==MGSurfaceFormat::Color || format==MGSurfaceFormat::Alpha8 || format==MGSurfaceFormat::ColorSRgb,"Unsupported BrowserGL texture format");
    require(w>0&&h>0&&levels>0,"Invalid texture dimensions");auto* t=new MGG_Texture();t->width=w;t->height=h;t->levels=levels;t->format=format;
    glGenTextures(1,&t->id);glBindTexture(GL_TEXTURE_2D,t->id);
    glTexStorage2D(GL_TEXTURE_2D,levels,format==MGSurfaceFormat::ColorSRgb?GL_SRGB8_ALPHA8:GL_RGBA8,w,h);
    glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAX_LEVEL,levels-1);
    glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_LINEAR);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_LINEAR);
    return t;
}
MGG_Texture* MGG_RenderTarget_Create(MGG_GraphicsDevice* d,MGTextureType type,MGSurfaceFormat format,mgint w,mgint h,mgint depth,mgint levels,mgint slices,MGDepthFormat depthFormat,mgint samples,MGRenderTargetUsage)
{
    require(samples<=1&&format!=MGSurfaceFormat::Alpha8,"BrowserGL render targets do not support explicit MSAA or Alpha8");
    auto* t=MGG_Texture_Create(d,type,format,w,h,depth,levels,slices);
    glGenFramebuffers(1,&t->framebuffer);glBindFramebuffer(GL_FRAMEBUFFER,t->framebuffer);
    glFramebufferTexture2D(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,t->id,0);
    if(depthFormat!=MGDepthFormat::None) {
        glGenRenderbuffers(1,&t->depthBuffer);glBindRenderbuffer(GL_RENDERBUFFER,t->depthBuffer);
        GLenum internal=depthFormat==MGDepthFormat::Depth16?GL_DEPTH_COMPONENT16:depthFormat==MGDepthFormat::Depth24?GL_DEPTH_COMPONENT24:GL_DEPTH24_STENCIL8;
        glRenderbufferStorage(GL_RENDERBUFFER,internal,w,h);
        glFramebufferRenderbuffer(GL_FRAMEBUFFER,depthFormat==MGDepthFormat::Depth24Stencil8?GL_DEPTH_STENCIL_ATTACHMENT:GL_DEPTH_ATTACHMENT,GL_RENDERBUFFER,t->depthBuffer);
    }
    require(glCheckFramebufferStatus(GL_FRAMEBUFFER)==GL_FRAMEBUFFER_COMPLETE,"Incomplete WebGL2 render target");
    glBindFramebuffer(GL_FRAMEBUFFER,d->target?d->target->framebuffer:0);return t;
}
void MGG_Texture_Destroy(MGG_GraphicsDevice* d,MGG_Texture* t)
{
    if(d->target==t) {
        d->target=nullptr;d->targetHeight=d->height;
        glBindFramebuffer(GL_FRAMEBUFFER,0);viewport(d);
    }
    for(auto& stage:d->textures)for(auto& bound:stage)if(bound==t)bound=nullptr;
    if(t->framebuffer)glDeleteFramebuffers(1,&t->framebuffer);if(t->depthBuffer)glDeleteRenderbuffers(1,&t->depthBuffer);glDeleteTextures(1,&t->id);delete t;
}
void MGG_Texture_SetData(MGG_GraphicsDevice*,MGG_Texture* t,mgint level,mgint slice,mgint x,mgint y,mgint z,mgint w,mgint h,mgint depth,mgbyte* data,mgint bytes)
{
    require(slice==0&&z==0&&depth<=1&&level>=0&&level<t->levels,"Invalid 2D texture subresource");
    if(w==0) w=std::max(1,t->width>>level);
    if(h==0) h=std::max(1,t->height>>level);
    require(x>=0&&y>=0&&w>0&&h>0&&x+w<=std::max(1,t->width>>level)&&y+h<=std::max(1,t->height>>level),"Invalid texture upload rectangle");
    std::vector<mgbyte> expanded;bool alpha=t->format==MGSurfaceFormat::Alpha8;
    require(bytes>=w*h*(alpha?1:4),"Texture upload buffer too small");
    if(alpha) { expanded.resize(w*h*4,255);for(int i=0;i<w*h;++i)expanded[i*4+3]=data[i];data=expanded.data(); }
    glBindTexture(GL_TEXTURE_2D,t->id);glPixelStorei(GL_UNPACK_ALIGNMENT,1);glTexSubImage2D(GL_TEXTURE_2D,level,x,y,w,h,GL_RGBA,GL_UNSIGNED_BYTE,data);
}
void MGG_Texture_GetData(MGG_GraphicsDevice* d,MGG_Texture* t,mgint level,mgint slice,mgint x,mgint y,mgint z,mgint w,mgint h,mgint depth,mgbyte* data,mgint bytes)
{
    require(slice==0&&z==0&&depth<=1&&level>=0&&level<t->levels,"Invalid texture read subresource");
    bool alpha=t->format==MGSurfaceFormat::Alpha8;require(bytes>=w*h*(alpha?1:4),"Texture read buffer too small");
    GLuint f;glGenFramebuffers(1,&f);glBindFramebuffer(GL_FRAMEBUFFER,f);glFramebufferTexture2D(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,t->id,level);
    require(glCheckFramebufferStatus(GL_FRAMEBUFFER)==GL_FRAMEBUFFER_COMPLETE,"Texture format is not readable");
    std::vector<mgbyte> pixels(alpha?w*h*4:0);glReadPixels(x,y,w,h,GL_RGBA,GL_UNSIGNED_BYTE,alpha?pixels.data():data);
    if(alpha)for(int i=0;i<w*h;++i)data[i]=pixels[i*4+3];
    glBindFramebuffer(GL_FRAMEBUFFER,d->target?d->target->framebuffer:0);glDeleteFramebuffers(1,&f);
}
MGG_InputLayout* MGG_InputLayout_Create(MGG_GraphicsDevice*,MGG_Shader*,mgint* strides,mgint count,MGG_InputElement* elements,mgint elementCount)
{ auto* l=new MGG_InputLayout();l->strides.assign(strides,strides+count);l->elements.assign(elements,elements+elementCount);return l; }
void MGG_InputLayout_Destroy(MGG_GraphicsDevice* d,MGG_InputLayout* l) { if(d->layout==l)d->layout=nullptr;delete l; }

MGG_Shader* MGG_Shader_Create(MGG_GraphicsDevice* d,MGShaderStage stage,mgbyte* bytes,mgint length)
{
    const mgbyte* p=bytes;const mgbyte* end=bytes+length;
    auto number=[&]() { require(end-p>=4,"Truncated BrowserGL shader");uint32_t v;std::memcpy(&v,p,4);p+=4;return v; };
    auto text=[&]() { auto n=number();require(n<=static_cast<size_t>(end-p),"Truncated BrowserGL shader string");std::string s(reinterpret_cast<const char*>(p),n);p+=n;return s; };
    require(length>=84,"Truncated shader reflection");p+=80;auto bindings=number();
    require(bindings<=static_cast<size_t>(end-p)/24,"Invalid shader descriptor header");p+=bindings*24;
    require(number()==0x4c47474d && number()==1,"Expected offline BrowserGL profile shader (81)");
    std::string source=text();auto* shader=new MGG_Shader();shader->block=text();auto count=number();
    require(count<=16,"Too many combined samplers");
    for(uint32_t i=0;i<count;++i) { int texture=number(),sampler=number();require(texture<16&&sampler<16,"Invalid sampler slot");shader->samplers.push_back({texture,sampler,text()}); }
    require(p==end,"Trailing shader payload data");
    if(stage==MGShaderStage::Vertex)
    {
        auto main=source.find("void main()");
        require(main!=std::string::npos,"Missing GLSL main entry point");
        source.replace(main,11,"void mg_main()");
        source+="\nuniform highp float mg_targetFlip;\nvoid main() { mg_main(); gl_Position.y *= mg_targetFlip; }\n";
    }
    shader->id=glCreateShader(stage==MGShaderStage::Vertex?GL_VERTEX_SHADER:GL_FRAGMENT_SHADER);
    auto* sourcePtr=source.c_str();glShaderSource(shader->id,1,&sourcePtr,nullptr);glCompileShader(shader->id);
    GLint ok=0;glGetShaderiv(shader->id,GL_COMPILE_STATUS,&ok);
    if(!ok) { char log[4096];glGetShaderInfoLog(shader->id,sizeof(log),nullptr,log);std::fprintf(stderr,"%s\n",source.c_str());fail(log); }
    ++d->diagnostics.ShaderCreationCount;return shader;
}
void MGG_Shader_Destroy(MGG_GraphicsDevice* d,MGG_Shader* s)
{
    for(auto& bound:d->shaders)if(bound==s)bound=nullptr;
    for(auto it=d->programs.begin();it!=d->programs.end();)
        if(it->first.first==s->id||it->first.second==s->id) { glDeleteProgram(it->second);it=d->programs.erase(it); } else ++it;
    glDeleteShader(s->id);delete s;
}
MGG_OcclusionQuery* MGG_OcclusionQuery_Create(MGG_GraphicsDevice*) { fail("WebGL2 occlusion queries return booleans, not XNA pixel counts"); }
void MGG_OcclusionQuery_Destroy(MGG_GraphicsDevice*,MGG_OcclusionQuery*) { fail("Occlusion queries are unsupported"); }
void MGG_OcclusionQuery_Begin(MGG_GraphicsDevice*,MGG_OcclusionQuery*) { fail("Occlusion queries are unsupported"); }
void MGG_OcclusionQuery_End(MGG_GraphicsDevice*,MGG_OcclusionQuery*) { fail("Occlusion queries are unsupported"); }
mgbyte MGG_OcclusionQuery_GetResult(MGG_GraphicsDevice*,MGG_OcclusionQuery*,mgint&) { fail("Occlusion queries are unsupported"); }
