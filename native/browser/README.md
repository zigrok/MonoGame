# Native SDL3 / WebGL2 browser target

This experimental source target preserves the `MonoGame.Framework` assembly identity and
XNA APIs. Rendering runs through the native `MGG_*` ABI and GLES3/WebGL2, not a JavaScript
renderer. SDL3, FAudio and MGG are statically linked into the .NET interpreter's single
WebAssembly module. There are no pthreads or SDL2 ports.

## Reproducible build

The verified toolchain is .NET SDK **10.0.103**, **wasm-tools 10.0.110**, and its
Emscripten **3.1.56 / pack 10.0.10**. Do not substitute a global emsdk. SDL3 is pinned
to stable **3.4.16**, `fa2c02bb6e21974a89ea9824bc53c9932abe5f9c`.
The script supports macOS arm64 and Linux x64 workload layouts; only macOS arm64 has
been validated. Override `DOTNET_ROOT` when the installation is not `/usr/local/share/dotnet`.
Initialize the SDL3, FAudio, stb and SPIRV-Cross submodules before building.

From the MonoGame repository:

```sh
bash native/browser/build.sh
dotnet build MonoGame.Framework/MonoGame.Framework.Browser.csproj -c Release
```

The build uses the existing MGFXC/DXC shader compiler, and builds the pinned SPIRV-Cross
as a **host-only** GLSL translator. It compiles all built-in effect permutations to
GLSL ES 3.00 **offline**, with std140 reflection. Browser shader format **81** is
intentionally incompatible with Vulkan format 80 and legacy OpenGL format 0.
`PlatformInfo.GraphicsBackend` returns `GraphicsBackend.WebGL` (value 5), so consumers
can select browser resources without confusing this target with legacy desktop OpenGL.
No DXC/SPIRV-Cross or runtime shader transpilation ships in the browser.

For custom effects (including Alpha8 coverage effects):

```sh
export MONOGAME_BROWSER_SHADER_TRANSLATOR="$PWD/Artifacts/browser/shader-tool/mggl-shader"
dotnet Artifacts/MonoGame.Effect.Compiler/Release/mgfxc.dll input.fx output.mgfxo /Profile:BrowserGL
```

`BrowserGL` uses the same SM6/HLSL macros as the native Vulkan compiler, but distinct
constant-buffer layout and prepared GLSL payloads.

## Host integration

Reference `MonoGame.Framework/MonoGame.Framework.Browser.csproj`, and import
`native/browser/MonoGame.Browser.Native.targets` into the **executable** browser project.
The targets retain interpretation and disable trimming, and statically link:

- `Artifacts/browser/native/mgruntime.a`
- `Artifacts/browser/native/sdl3/libSDL3.a`
- `Artifacts/browser/native/faudio/libFAudio.a`

The archive is deliberately named **mgruntime.a**, not libmgruntime.a: the .NET static
P/Invoke table keys libraries by their archive basename, which must match `DllImport`.
Use the same rule for other native dependencies. WebGL2 linker options are included.

Supply `<canvas id="canvas">` and `.withModuleConfig({ canvas })` to the .NET loader.
Call `game.Run()` once. It starts asynchronously without registering any scheduler.
The host calls `MonoGame.Framework.BrowserGameLoop.Tick(game)` from its **one**
`requestAnimationFrame` callback; false means exit. Fixed-step games yield rather than
sleeping/spinning before their next update. The host remains responsible for `Dispose`.
`Tick` owns SDL event polling, cooperative audio pumping, `Game.Tick()` (update and draw),
and the framework's queued main-thread work. A module adapter should expose one
`Frame() -> bool`, not call separate update/draw methods around it. Set
`game.IsFixedTimeStep = false` before `Run(GameRunBehavior.Asynchronous)` when each
animation callback should perform one variable-time update and draw. MonoGame measures
elapsed time; the host does not pass or apply its own delta a second time.

`BrowserGameLoop.Resize(game, pixelWidth, pixelHeight)` updates the drawable pixel size.
The host owns CSS sizing and device-pixel-ratio policy. Game coordinates, scissor rectangles
and SDL mouse coordinates use drawable pixels; do not scale the graphics manager a second
time. Native resize notifications do not reapply stale window sizes.
Hosts using resizable SDL windows should apply their pixel/DPR resize after SDL's browser
resize callback; SDL's CSS-based sizing must not overwrite the host's requested drawable.

On context loss, the next tick stops and throws an explicit restart-required exception.
Catch it in the host and offer a full-page restart. In-place GPU resource resurrection is
not implemented; continuing with old handles is never treated as recovery.

Stage assets asynchronously into the runtime filesystem **before** calling synchronous
MonoGame content/image/song APIs. There is no synchronous network loader in this target.

## Supported graphics profile

- SpriteBatch, BasicEffect vertex colors, native vertex/index/constant buffers and indexed
  instanced draws; all declared XNA vertex element formats.
- Color, ColorSRgb and Alpha8 2D textures, updates and readback. Alpha8 stores RGBA with
  white RGB and coverage in alpha, preserving `.a` sampling (including coverage effects).
- One 2D render target, optional Depth16/Depth24/Depth24Stencil8, mipmap generation,
  render-target sampling with consistent top-left orientation.
- Separate RGB/alpha blend functions, depth/stencil state, culling, viewport, scissor,
  point/linear/mip filtering, wrap/clamp/mirror addressing.
- Offline effect reflection and distinct stage uniform blocks, multiple texture/sampler
  pairs, in-memory GL program caching and pipeline diagnostics/prewarming.

Unsupported native operations fail explicitly: compressed/float/volume/cube/array textures,
MRT, explicit MSAA, anisotropic/border/comparison samplers, LOD bias, wireframe/depth clamp,
and XNA pixel-count occlusion queries. WebGL has no portable program-binary cache; persistent
pipeline-cache import returns `Unsupported`. The capability flags do not advertise these
features. This is a supported 2D/UI profile, **not desktop HiDef feature parity**.

## Audio and scheduling

SoundEffect uses FAudio/SDL3. Start/unlock audio from a browser user gesture. Song decodes
one 250-ms chunk per animation tick and holds at most three queued chunks, without decoder
threads, waits or sleeps. PCM16 mono/stereo WAV, Ogg Vorbis and MP3 are accepted. PCM WAV
reads/seeks are bounded and do not load a whole second decoded copy into memory.
Pause/resume/stop/looping continue through the existing MediaPlayer API. Completion is
delivered only after the final native voice buffer drains. Volume remains adjustable.
`BrowserGameLoop.SetAudioSuspended(bool)` stops/starts the FAudio engine for host visibility
changes while preserving individual voice state and volume. It also applies if requested
before the first audio system is created.

The workload's Emscripten SDL audio uses ScriptProcessorNode, which browsers mark deprecated.
Music may underrun while background-tab animation frames are throttled. AudioWorklet
migration and guaranteed background music are not part of this initial single-thread profile.

## Executable proofs and current evidence

```sh
bash native/browser/build-proof.sh
source native/browser/emsdk-env.sh
dotnet publish native/browser/renderer-proof/RendererProof.csproj -c Release \
  -o Artifacts/browser/renderer-proof
```

Serve `Artifacts/browser/proof/wwwroot` and `Artifacts/browser/renderer-proof/wwwroot`
with an ordinary static HTTP server. No COOP/COEP headers or cross-origin isolation are
required. The first proof checks the linked SDL version and creates a real GLES3 context.
The second runs actual MonoGame APIs and pixel assertions for SpriteBatch, BasicEffect,
Alpha8 coverage, scissor, render-target orientation and readback, then repeats after
resizing 160×120 → 320×240 → 160×120. The white sprite texture loads a PNG through
`Texture2D.FromStream`. Its button generates a quiet two-second PCM WAV, loads it through
`SoundEffect.FromStream`, and plays both that effect and a cooperative Song.

Verified in desktop Chrome on macOS: `SDL_STATIC_NATIVE_PROOF=PASS`,
`MONOGAME_BROWSER_GRAPHICS_PROOF=PASS`, `MONOGAME_BROWSER_RESIZE_PROOF=PASS`,
`MONOGAME_BROWSER_AUDIO_ADVANCING=PASS`,
and `MONOGAME_BROWSER_AUDIO_DRAINED=PASS`; injected context loss stops with the documented
restart exception. Desktop Native managed build, syntax checks for shared SDL/Metal
sources, and the existing full `Build Native Metal` task (including universal SDL3/FAudio
libraries and the Metal compiler) also pass. Existing compiler warnings remain.

The repository's NUnitLite main-thread runner passes 46 desktop Game tests, with 13
pre-existing ignored tests. The older general ShaderTest fixture cannot select a Metal
effect directory (`Paths.CompiledEffect` has no `METAL` branch); its eight cases remain
blocked by that existing fixture limitation, not reported as passing. `dotnet test` is
not an equivalent replacement for this repository's Cocoa main-thread runner.

The companion Textus native host now passes separately loaded Luna GPU readback,
DPR-2 resize and actual context-loss shutdown in Chromium, Firefox and WebKit.
Forma's independent compiled-XAML/native-font/Alpha8 proof passes those engines for all
seven supported locales, including font disposal/reload. The audio proof additionally
verifies pre-initialization suspension, frozen playback position and resumed advancement:

```sh
node native/browser/renderer-proof/verify-audio.mjs \
  "<absolute existing @playwright/test/index.mjs path>" http://127.0.0.1:5292/
```

Full input/fullscreen combinations, every effect permutation at draw time, complete
story/save/locale workflows, physical Safari/mobile devices and release validation remain
gates. The Luna startup screenshots also exposed blank menu captions, reported to its
owners. These proofs are **not** a claim that the complete application vertical slice
or every desktop API is ready.
