// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

#pragma once
#include <string.h>

#if defined(MG_DIRECTX12)
#define MG_BUILTIN_EFFECT_SYMBOL(name) name##_dx12_mgfxo
#elif defined(MG_GLES)
#define MG_BUILTIN_EFFECT_SYMBOL(name) name##_gl_mgfxo
#elif defined(MG_VULKAN) || defined(MG_METAL) || defined(MG_HEADLESS)
// The Metal backend reuses the Vulkan-compiled effect blobs (SPIR-V + reflection header) and
// translates SPIR-V -> MSL at runtime, so it consumes the same *_vk_mgfxo symbols. The headless
// backend reports the same shader profile (80) and never executes a shader, so it reuses them too
// — that way effect loading behaves identically whether a test runs headless or on a real device.
#define MG_BUILTIN_EFFECT_SYMBOL(name) name##_vk_mgfxo
#else
#error "Unsupported graphics backend, this header is intended for native builtin effects embedding only."
#endif

#define MG_BUILTIN_EFFECT_BYTES(name) ((mgbyte*)MG_BUILTIN_EFFECT_SYMBOL(name))
#define MG_BUILTIN_EFFECT_SIZE(name) (sizeof(MG_BUILTIN_EFFECT_SYMBOL(name)))

#define MG_HANDLE_BUILTIN_EFFECT(effectName, bytecode, size) \
    if (strcmp(name, #effectName) == 0) \
    { \
        (bytecode) = MG_BUILTIN_EFFECT_BYTES(effectName); \
        (size) = MG_BUILTIN_EFFECT_SIZE(effectName); \
    }

void MGG_EffectResource_GetBytecode(const char* name, mgbyte * &bytecode, mgint & size)
{
	MG_HANDLE_BUILTIN_EFFECT(AlphaTestEffect, bytecode, size)
	else MG_HANDLE_BUILTIN_EFFECT(BasicEffect, bytecode, size)
	else MG_HANDLE_BUILTIN_EFFECT(DualTextureEffect, bytecode, size)
	else MG_HANDLE_BUILTIN_EFFECT(EnvironmentMapEffect, bytecode, size)
	else MG_HANDLE_BUILTIN_EFFECT(SkinnedEffect, bytecode, size)
	else MG_HANDLE_BUILTIN_EFFECT(SpriteEffect, bytecode, size)
}
