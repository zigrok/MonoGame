-- MonoGame - Copyright (C) MonoGame Foundation, Inc
-- This file is subject to the terms and conditions defined in
-- file 'LICENSE.txt', which is part of this source code package.

local vulkan_sdk = os.getenv("VULKAN_SDK")

newoption {
    trigger = "artifacts-root",
    value = "PATH",
    description = "Isolated absolute directory for generated projects, objects and binaries"
}
newoption {
    trigger = "sdl3-build",
    value = "PATH",
    description = "Directory containing the matching source-built SDL3 static library"
}
newoption {
    trigger = "faudio-build",
    value = "PATH",
    description = "Directory containing the matching source-built FAudio static library"
}
newoption {
    trigger = "metal-shaders",
    value = "PATH",
    description = "Directory containing freshly generated Vulkan-profile stock effect headers"
}

newoption {
    trigger = "arch",
    value = "ARCH",
    description = "Target architecture (x64 or arm64)",
    default = "x64",
    allowed = {
        { "x64", "64-bit x86" },
        { "arm64", "64-bit ARM" }
    }
}

newoption {
    trigger = "metal-only",
    description = "Generate only the native Metal project without requiring the Vulkan SDK"
}

newoption {
    trigger = "headless-only",
    description = "Generate only the headless project, requiring neither the Vulkan SDK nor Xcode"
}

-- Which SDL major version the platform layer (MGP) is built against. SDL3 is the default;
-- SDL2 remains available as an explicit fallback. The MGP sources are shared and guarded.
newoption {
    trigger = "sdl",
    value = "VERSION",
    description = "SDL major version for the platform layer (2 or 3)",
    default = "3",
    allowed = {
        { "2", "SDL2 (fallback)" },
        { "3", "SDL3 (default)" }
    }
}

function common(project_name)
    -- The default SDL3 variant uses the canonical project path consumed by packages and tests.
    -- The explicit SDL2 fallback uses a distinct path so both variants can coexist.
    local variant = project_name
    if _OPTIONS["sdl"] == "2" then
        variant = project_name .. "-sdl2"
    end
    if os.target() == "windows" then
        filter "platforms:x64"
        architecture "x86_64"
        filter "platforms:arm64"
        architecture "ARM64"
        filter {}
        platform_target_path = "../../Artifacts/native/mgruntime/" .. variant .. "/%{cfg.system}/%{cfg.platform}/%{cfg.buildcfg}"
    else
        local target_arch = _OPTIONS["arch"] or "x64"
        architecture(target_arch == "arm64" and "ARM64" or "x64")
        if os.target() == "macosx" then
            platform_target_path = "../../Artifacts/native/mgruntime/" .. variant .. "/%{cfg.system}/%{cfg.buildcfg}"
        else
            platform_target_path = "../../Artifacts/native/mgruntime/" .. variant .. "/%{cfg.system}/" .. target_arch .. "/%{cfg.buildcfg}"
        end
    end
    if _OPTIONS["artifacts-root"] then
        platform_target_path = path.join(_OPTIONS["artifacts-root"], "bin", variant, "%{cfg.buildcfg}")
    end
    kind "SharedLib"
    language "C++"
    filter "system:linux"
    pic "On"
    filter {}
    defines {"DLL_EXPORT"}
    targetdir(platform_target_path)
    -- Per-variant object dir so the SDL2 and SDL3 builds don't share stale objects.
    objdir("obj/" .. variant)
    if _OPTIONS["artifacts-root"] then
        objdir(path.join(_OPTIONS["artifacts-root"], "obj", variant, "%{cfg.buildcfg}"))
    end
    targetname "mgruntime"
    cppdialect "C++17"

    files {"include/**.h", "common/**.h", "common/**.cpp"}
    includedirs {"include", "common", "../../external/stb"}
end

-- SDL is supported on all desktop platforms.
function sdl2()
    defines {"MG_SDL2"}

    files {"sdl/**.h", "sdl/**.cpp"}

    includedirs {"external/sdl2/sdl/include"}

    filter {"system:windows"}
    links {"external/sdl2/sdl/build/%{cfg.platform}/Release/SDL2-static.lib", "winmm", "imm32", "user32", "gdi32", "advapi32",
           "setupapi", "ole32", "oleaut32", "version", "shell32"}
    filter {"system:macosx"}
    libdirs {"external/sdl2/sdl/build"}
    linkoptions {"-Wl,-force_load,external/sdl2/sdl/build/libSDL2.a"}
    links {"SDL2"}
    links {"Cocoa.framework", "IOKit.framework", "ForceFeedback.framework", "CoreAudio.framework",
        "AudioToolbox.framework", "CoreGraphics.framework", "CoreFoundation.framework", "Metal.framework",
        "CoreVideo.framework", "GameController.framework", "CoreHaptics.framework", "Carbon.framework", "iconv"}

    filter {"system:linux"}
    linkoptions {"external/sdl2/sdl/build/libSDL2.a"}
    links {"dl", "pthread", "m", "rt"}
    filter {}
end

-- SDL3 (default, or explicit via --sdl=3). Shares the same MGP sources as sdl2(); the sources are
-- #if MG_SDL2 / MG_SDL3 guarded. SDL3 headers live under external/sdl3/include (SDL3/*.h).
function sdl3()
    local sdl_build = _OPTIONS["sdl3-build"] or "external/sdl3/build"
    -- SDL_ENABLE_OLD_NAMES turns on SDL3's official compat aliases for renamed-but-unchanged
    -- SDL2 symbols, so the shared MGP sources only need #if MG_SDL3 branches at genuine
    -- structural/semantic divergences (events, keysym, return types, surface/display/handle APIs).
    defines {"MG_SDL3", "SDL_ENABLE_OLD_NAMES"}

    files {"sdl/**.h", "sdl/**.cpp"}

    includedirs {"external/sdl3/include"}

    filter {"system:windows"}
    links {"external/sdl3/build/%{cfg.platform}/Release/SDL3-static.lib", "winmm", "imm32", "user32", "gdi32", "advapi32",
           "setupapi", "ole32", "oleaut32", "version", "shell32"}
    filter {"system:macosx"}
    libdirs {sdl_build}
    linkoptions {"-Wl,-force_load," .. sdl_build .. "/libSDL3.a"}
    links {"SDL3"}
    -- QuartzCore is SDL's own dependency, not the Metal backend's: SDL_cocoavulkan.m references
    -- CAMetalLayer unconditionally, whatever graphics backend is linked alongside it. It went
    -- unnoticed while every macOS target also linked metal(), which pulls QuartzCore in itself —
    -- the headless target is the first one here that doesn't, and it failed to link without this.
    links {"Cocoa.framework", "IOKit.framework", "ForceFeedback.framework", "CoreAudio.framework",
        "AudioToolbox.framework", "CoreGraphics.framework", "CoreFoundation.framework", "Metal.framework",
        "QuartzCore.framework",
        "CoreVideo.framework", "GameController.framework", "CoreHaptics.framework", "Carbon.framework",
        "UniformTypeIdentifiers.framework", "AVFoundation.framework", "CoreMedia.framework", "iconv"}

    filter {"system:linux"}
    linkoptions {"external/sdl3/build/libSDL3.a"}
    links {"dl", "pthread", "m", "rt"}
    filter {}
end

-- Selects the platform (MGP) implementation per the --sdl option.
function sdl()
    if _OPTIONS["sdl"] == "3" then
        sdl3()
    else
        sdl2()
    end
end

-- Vulkan is supported for all desktop platforms.
function vulkan()
    if vulkan_sdk == nil and os.target() == "macosx" then
        error("Error: VULKAN_SDK environment variable is not set. Please set it to your Vulkan SDK installation path.")
    end

    defines {"MG_VULKAN"}

    files {"vulkan/**.h", "vulkan/**.cpp"}

    includedirs {"external/vulkan-headers/include", "external/volk", "external/vma/include",
        path.join(vulkan_sdk, "include")}

    filter {"system:macosx"}
    libdirs {path.join(vulkan_sdk, "lib/MoltenVK.xcframework/macos-arm64_x86_64")}
    links {"MoltenVK", "IOSurface.framework", "Foundation.framework", "QuartzCore.framework", "AppKit.framework"}
    filter {}
end

-- DirectX12 is supported on Xbox and Windows.
function directx12()
    defines {"MG_DIRECTX12"}

    files {"directx12/**.h", "directx12/**.cpp"}

    filter {"system:windows"}
    links {"dxguid", "dxgi", "d3d12"}
    filter {}
end

-- Metal is the native macOS/iOS graphics backend. It renders straight to a CAMetalLayer (no MoltenVK),
-- reusing the Vulkan backend's compiled effect headers (vulkan/*.vk.mgfxo.h: SPIR-V + reflection header)
-- and translating SPIR-V -> MSL at runtime via the pinned SPIRV-Cross submodule. Obj-C++ (.mm).
function headless()
    defines {"MG_HEADLESS"}

    files {"headless/**.h", "headless/**.cpp"}

    -- "vulkan" is on the include path for the shared *.vk.mgfxo.h effect blobs, exactly as metal()
    -- does: the headless backend reports the same shader profile (80) so it consumes the same
    -- compiled effect content as the real desktop backends.
    includedirs {_OPTIONS["metal-shaders"] or "vulkan"}
end

function metal()
    defines {"MG_METAL"}

    files {
        "metal/**.h", "metal/**.cpp", "metal/**.mm",
        "external/spirv-cross/spirv_cross.cpp",
        "external/spirv-cross/spirv_parser.cpp",
        "external/spirv-cross/spirv_cross_parsed_ir.cpp",
        "external/spirv-cross/spirv_cfg.cpp",
        "external/spirv-cross/spirv_glsl.cpp",
        "external/spirv-cross/spirv_msl.cpp",
        "external/spirv-cross/spirv_cross_util.cpp"
    }

    -- "vulkan" is on the include path so metal/ can #include the shared *.vk.mgfxo.h effect blobs;
    -- SPIRV-Cross headers include each other by bare name, so its root is also an include directory.
    includedirs {_OPTIONS["metal-shaders"] or "vulkan", "external/spirv-cross"}

    filter {"system:macosx"}
    links {"Metal.framework", "MetalKit.framework", "QuartzCore.framework", "Foundation.framework",
        "IOSurface.framework", "AppKit.framework"}
    filter {}

    -- Compile the Obj-C++ backend with ARC so MTL* object lifetime is automatic. Scoped to the
    -- metal .mm files only; the shared C++ TUs (sdl/faudio/common) don't touch Obj-C objects.
    filter {"files:metal/**.mm"}
    buildoptions {"-fobjc-arc"}
    filter {}
end

-- FAudio is supported for all desktop platforms.
function faudio()
    defines {"MG_FAUDIO"}

    files {"faudio/**.h", "faudio/**.cpp"}

    includedirs {"external/faudio/include"}

    -- FAudio uses SDL as its platform layer (threads/audio-device/IO), so it must be built against
    -- the SAME SDL major version we link. SDL3 uses build-sdl3; the SDL2 fallback uses build.
    local faudio_build = _OPTIONS["faudio-build"] or
        ((_OPTIONS["sdl"] == "3") and "external/faudio/build-sdl3" or "external/faudio/build")

    filter {"system:windows"}
    libdirs {faudio_build .. "/%{cfg.platform}/Release"}
    links {"FAudio.lib"}

    filter {"system:macosx"}
    libdirs {faudio_build}
    linkoptions {
        "-Wl,-force_load," .. faudio_build .. "/libFAudio.a"
    }

    filter {"system:linux"}
    linkoptions {faudio_build .. "/libFAudio.a"}
    filter {}
end

-- Xaudio is supported on Windows and Xbox.
function xaudio()
    defines {"MG_XAUDIO"}

    files {"xaudio/**.h", "xaudio/**.cpp"}
end

function configs()
    filter "configurations:Debug"
    defines {"DEBUG"}
    symbols "On"

    filter "configurations:Release"
    defines {"NDEBUG"}
    optimize "On"

    filter {"system:windows"}
    staticruntime "On"
    filter {"system:windows", "configurations:Debug"}
    runtime "Debug"
    filter {"system:windows", "configurations:Release"}
    runtime "Release"

    filter "system:macosx"
    buildoptions {"-arch x86_64", "-arch arm64"}
    linkoptions {"-arch x86_64", "-arch arm64"}
    filter {}
end

workspace "monogame"
if _OPTIONS["artifacts-root"] then
    location(path.join(_OPTIONS["artifacts-root"], "projects"))
end
configurations {"Debug", "Release"}
if os.target() == "windows" then
    platforms { "x64", "arm64" }
end

if not _OPTIONS["metal-only"] and not _OPTIONS["headless-only"] then
    project "desktopvk"
    common("desktopvk")
    sdl()
    vulkan()
    faudio()
    configs()
end

-- Ungated deliberately: headless has no external SDK dependency of any kind, so it must generate on
-- a machine with neither the Vulkan SDK nor Xcode. That is exactly the machine it exists for.
project "desktopheadless"
common("desktopheadless")
sdl()
headless()
faudio()
configs()

if os.target() == "macosx" and not _OPTIONS["headless-only"] then
    project "mgmetalcompiler"
    kind "ConsoleApp"
    language "C++"
    cppdialect "C++17"
    targetname "mgmetalcompiler"
    targetdir "../../Artifacts/native/mgmetalcompiler/%{cfg.system}/%{cfg.buildcfg}"
    objdir "obj/mgmetalcompiler"
    if _OPTIONS["artifacts-root"] then
        targetdir(path.join(_OPTIONS["artifacts-root"], "bin", "mgmetalcompiler", "%{cfg.buildcfg}"))
        objdir(path.join(_OPTIONS["artifacts-root"], "obj", "mgmetalcompiler", "%{cfg.buildcfg}"))
    end
    files {
        "tools/metalcompiler/main.cpp",
        "metal/MGMetalShaderTranspiler.h",
        "metal/MGMetalShaderTranspiler.cpp",
        "external/spirv-cross/spirv_cross.cpp",
        "external/spirv-cross/spirv_parser.cpp",
        "external/spirv-cross/spirv_cross_parsed_ir.cpp",
        "external/spirv-cross/spirv_cfg.cpp",
        "external/spirv-cross/spirv_glsl.cpp",
        "external/spirv-cross/spirv_msl.cpp",
        "external/spirv-cross/spirv_cross_util.cpp"
    }
    includedirs {"metal", "external/spirv-cross"}
    configs()

    -- Native Metal head (macOS). Coexists on disk with desktopvk via common()'s per-project artifacts.
    project "desktopmetal"
    common("desktopmetal")
    sdl()
    metal()
    faudio()
    configs()
end

if os.target() == "windows" then
    project "windowsdx"
    common("windowsdx")
    sdl()
    directx12()
    xaudio()
    configs()
end
