<div align="center">
 <a href="https://monogame.net/">
   <img height="128" alt="MonoGame" src="https://raw.githubusercontent.com/MonoGame/MonoGame.Logo/refs/heads/master/FullColorOnLight/LogoOnly_128px.png">
 </a>
 <h1>MonoGame — <code>zigrok</code> fork</h1>

 One framework for creating powerful cross-platform games

[What this fork adds](#what-this-fork-adds) •
[Backends](#backends) •
[Building the divergent targets](#building-the-divergent-targets) •
[Relationship to upstream](#relationship-to-upstream) •
[Upstream resources](#upstream-resources) •
[License](#license)
</div>

## About this fork

This is a fork of [MonoGame](https://github.com/MonoGame/MonoGame), the open-source
re-implementation of Microsoft's XNA Framework. It tracks upstream's `develop` branch and adds
platform and backend work that is not in upstream 3.8.x: an **SDL3** platform layer, a direct
**Metal** graphics backend on macOS, a **browser (WebGL2) target** built with Emscripten, plus
high-DPI, live-resize and native text-input support.

Everything here preserves the **`MonoGame.Framework` assembly identity and the XNA-style API**, so
existing game code and third-party libraries (Myra, FontStashSharp, …) bind to it unchanged.

> [!IMPORTANT]
> Published upstream runtime packages such as `MonoGame.Runtime.Mac.Vulkan` are **not** compatible
> with this fork's managed layer — they predate native exports added here and fail at startup with
> errors like `Unable to find an entry point named 'MGP_Window_GetDrawableSize'`. Build the native
> runtime from source (see [Building the divergent targets](#building-the-divergent-targets)).

## What this fork adds

| Addition | Where | Notes |
| --- | --- | --- |
| **SDL3 platform layer** | `native/monogame/sdl`, submodule `external/sdl3` | Upstream 3.8.x ships SDL2. Native builds can carry both; the Metal and DX12 backends prefer SDL3. |
| **Metal graphics backend** | `native/monogame/metal` (`desktopmetal`) | Renders directly to a `CAMetalLayer`. Translates the Vulkan-profile SPIR-V effects to MSL at runtime with **vendored SPIRV-Cross** — **no MoltenVK and no Vulkan SDK**. |
| **Browser / WebGL2 target** | `native/browser` | SDL3, FAudio and MGG statically linked into the .NET interpreter's single WebAssembly module. Rendering goes through the native `MGG_*` ABI on GLES3/WebGL2, *not* a JavaScript renderer. No pthreads, no SDL2 port. |
| **Direct3D 12 backend** | `native/monogame/directx12` (`windowsdx`) | Paired with XAudio. |
| **Vulkan backend** | `native/monogame/vulkan` (`desktopvk`) | Entry points via **volk**, device memory via **VMA**; MoltenVK on macOS. |
| **High-DPI back buffers** | `GraphicsDeviceManager.AllowHighDpi` | Opt-in on DesktopGL/SDL: the window stays in logical points while the back buffer and viewport are physical pixels, so rendering is crisp instead of OS-upscaled. The Native platform derives its scale from `MGP_Window_GetDrawableSize` and needs no opt-in. |
| **Live resize** | `Platform/Native/GameWindow.Native.cs` | Runs the ordinary update/draw tick during Cocoa live-resize notifications, so a macOS resize drag stays live instead of freezing. Native platform only — SDL and browser loops keep their previous behaviour. |
| **Native text input / IME** | `GameWindow.SupportsTextComposition`, `TextCommitted`, `TextEditing` | SDL3 preedit is separated from whole-string commits, with scalar selections converted to UTF-16 and candidate rectangles translated from drawable to window space. Rich events are opt-in with capability fallback for older native libraries. |
| **`GraphicsBackend` values** | `Utilities/GraphicsBackend.cs` | Adds `Vulkan`, `Metal`, `DirectX12` and `WebGL` (`5`) alongside `DirectX` and `OpenGL`, so consumers can select resources per backend. |

## Backends

One managed API, two managed implementations. The Native implementation pairs with one of several
`libmgruntime` graphics modules; all variants produce the same managed assembly name.

| Backend | Assembly / lib | Windowing + input | Graphics | Audio |
| --- | --- | --- | --- | --- |
| **DesktopGL** (mature managed backend) | `MonoGame.Framework.DesktopGL` | SDL2 (managed P/Invoke) | OpenGL | OpenAL |
| **Native — `desktopvk`** | `MonoGame.Framework.Native` + `libmgruntime` | SDL2 or SDL3 (static, MGP) | Vulkan (MGG); MoltenVK on macOS | FAudio (MGA) |
| **Native — `desktopmetal`** (macOS) | `MonoGame.Framework.Native` + `libmgruntime` | SDL2 or SDL3 (static, MGP) | Metal (MGG), direct `CAMetalLayer` | FAudio (MGA) |
| **Native — `windowsdx`** (Windows/Xbox) | `MonoGame.Framework.Native` + `libmgruntime` | SDL2 or SDL3 (static, MGP) | Direct3D 12 (MGG) | XAudio (MGA) |
| **Native — browser** | `MonoGame.Framework.Browser` + static WASM module | SDL3 (static, MGP) | GLES3 / WebGL2 (MGG) | FAudio (MGA) |

`libmgruntime` is modular: **MGP** = platform (windowing/input, `sdl/MGP_sdl.cpp`), **MGG** =
graphics (`vulkan/MGG_Vulkan.cpp`, `metal/MGG_Metal.mm`, `directx12/MGG_DX12.cpp`), **MGA** = audio
(`faudio/MGA_faudio.cpp`, `xaudio/MGA_xaudio2.cpp`).

For the full dependency graph and per-edge notes, see [BACKEND-DEPENDENCIES.md](BACKEND-DEPENDENCIES.md).

## Building the divergent targets

Start with the usual clone and submodule setup:

```sh
git clone --recurse-submodules https://github.com/zigrok/MonoGame.git
git submodule update --init
```

### Browser (WebGL2)

The Emscripten toolchain comes from the pinned .NET `wasm-tools` workload — **never a global
emsdk**. The verified toolchain is .NET SDK **10.0.103**, **wasm-tools 10.0.110** and its Emscripten
**3.1.56 / pack 10.0.10**; SDL3 is pinned to **3.4.16**. Initialise the `sdl3`, `faudio`, `stb` and
`spirv-cross` submodules first.

```sh
bash native/browser/build.sh
dotnet build MonoGame.Framework/MonoGame.Framework.Browser.csproj -c Release
```

Host integration: reference `MonoGame.Framework.Browser.csproj`, import
`native/browser/MonoGame.Browser.Native.targets` into the **executable** project, supply
`<canvas id="canvas">` with `.withModuleConfig({ canvas })`, call `game.Run()` once, then drive
`MonoGame.Framework.BrowserGameLoop.Tick(game)` from a single `requestAnimationFrame` callback
(`false` means exit). Browser shader format **81** is intentionally incompatible with Vulkan format
80 and legacy OpenGL format 0. Details in [`native/browser/README.md`](native/browser/README.md).

### Metal (macOS)

No Vulkan SDK is required — `desktopmetal` compiles the vendored `external/spirv-cross` sources and
links only Apple frameworks. (The Vulkan SDK applies to `desktopvk`, not to Metal; see
[METAL-VULKAN-SDK-DEPENDENCY.md](METAL-VULKAN-SDK-DEPENDENCY.md) for the background.) You need
`premake5`, Xcode's Metal toolchain, and the `external/spirv-cross` submodule.

macOS native builds are **universal binaries**, so SDL3 and FAudio must be built fat or the link
fails with `symbol(s) not found for architecture x86_64`.

```sh
cd native/monogame

# 1. Static, universal dependencies
cmake -S external/sdl3 -B external/sdl3/build -DSDL_STATIC=ON -DSDL_SHARED=OFF \
  -DSDL_TEST_LIBRARY=OFF -DSDL_TESTS=OFF -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES="x86_64;arm64" -DCMAKE_OSX_DEPLOYMENT_TARGET=10.15
cmake --build external/sdl3/build --config Release --parallel 4
cmake -S external/faudio -B external/faudio/build-sdl3 -DBUILD_SHARED_LIBS=OFF -DBUILD_SDL3=ON \
  -DCMAKE_C_STANDARD_INCLUDE_DIRECTORIES="$PWD/external/sdl3/include" -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES="x86_64;arm64" -DCMAKE_OSX_DEPLOYMENT_TARGET=10.15
cmake --build external/faudio/build-sdl3 --config Release --parallel 4

# 2. Stock effects at the Vulkan profile (translated to MSL at runtime). Gitignored, not checked in.
cd vulkan
for fx in ../../../MonoGame.Framework/Platform/Graphics/Effect/Resources/*.fx; do
  dotnet ../../../Artifacts/MonoGame.Effect.Compiler/Release/mgfxc.dll \
    "$fx" "$(basename "$fx" .fx).vk.mgfxo.h" /Profile:Vulkan
done
cd ..

# 3. The runtime
premake5 --arch=arm64 --metal-only gmake
make config=release desktopmetal mgmetalcompiler
```

The result is `Artifacts/native/mgruntime/desktopmetal/macosx/Release/libmgruntime.dylib`.

The equivalent automated path is the repo's own build tasks: `Build Native Dependencies` →
`Build Vulkan Shaders` → `Build Native Metal` (`dotnet run --project build/Build.csproj -- --target="Build Native Metal"`).

## Relationship to upstream

This fork tracks upstream `develop` and rebases fork work on top of it, so its history diverges from
upstream tags. Bugs that reproduce on stock MonoGame belong in the
[upstream issue tracker](https://github.com/MonoGame/MonoGame/issues); anything specific to the
additions listed above belongs in this repository.

Upstream's own preview work covers Vulkan and DirectX 12 for `3.8.5`; the SDL3 layer, the direct
Metal backend, the browser target and the platform fixes listed above are specific to this fork.

## Upstream resources

The upstream documentation applies to the managed API, which is unchanged here.

* [Getting started →](https://docs.monogame.net/articles/tutorials/building_2d_games/)
* ["How To" Guides →](https://docs.monogame.net/articles/getting_to_know/howto/)
* [Documentation Hub →](https://docs.monogame.net/)
* [API Reference →](https://docs.monogame.net/api/index.html)
* [Game samples](https://github.com/MonoGame/MonoGame.Samples) maintained by the MonoGame team

To support the upstream project, see the [MonoGame donation page](https://monogame.net/donate/).

## Source code layout

> [!NOTE]
> For build prerequisites, see [REQUIREMENTS.md](REQUIREMENTS.md).

* The game framework is in [MonoGame.Framework](MonoGame.Framework).
* The native runtime (MGP / MGG / MGA) is in [native/monogame](native/monogame).
* The browser target is in [native/browser](native/browser).
* The content pipeline is in [MonoGame.Framework.Content.Pipeline](MonoGame.Framework.Content.Pipeline).
* Project templates are in [Templates](Templates); framework tests in [Tests](Tests).
* [mgcb](Tools/MonoGame.Content.Builder) is the content processing CLI, [mgfxc](Tools/MonoGame.Effect.Compiler) the effect compiler, and [mgcb-editor](Tools/MonoGame.Content.Builder.Editor) the GUI frontend.

## License

The MonoGame project is under the [Microsoft Public License](https://opensource.org/licenses/MS-PL)
except for a few portions of the code. See the [LICENSE.txt](LICENSE.txt) file for more details.
Third-party libraries used by MonoGame are under their own licenses. Please refer to those libraries
for details on the license they use.
