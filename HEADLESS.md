# Headless backend

A fourth graphics backend alongside Metal, Vulkan and Direct3D 12, for running a game with **no
window, no GPU and no display server**. It implements the whole `api_MGG.h` contract as no-ops, so
`Game.Run()`'s `Initialize` → `Update`/`Draw` → `Present` loop executes normally and every graphics
call succeeds — but nothing is rasterized.

It exists so automated tests can execute the real draw path instead of bypassing it. It is not a
software renderer.

## What does and does not work

| Works | Does not |
| --- | --- |
| The full `Game` lifecycle, update and draw ticks, `Present` | Anything visual |
| Creating and binding render targets, textures, buffers, shaders, state objects | Rasterization of any kind |
| `SpriteBatch` passes, `DrawUserPrimitives`, `BasicEffect` and the other stock effects | Pixel or visual-regression assertions |
| `Texture2D.SetData`/`GetData` round trips | Sampling — a shader never executes |
| `GetBackBufferData` | — it returns a uniformly zero surface |

`GetBackBufferData` returning blank is deliberate rather than incidental: handing back
uninitialized memory would let a screenshot-style test appear to capture something. Assertions in a
headless run belong on model and scene state, not on pixels.

## Selecting it

Headless is **not** a separate `MonoGamePlatform`. It uses the same `MonoGame.Framework.Native`
managed assembly as Metal and Vulkan — those backends are already distinguished only by which
`libmgruntime` is loaded — so it is selected exactly the way Metal is, by pointing at a different
native runtime and asking SDL for its dummy drivers:

```sh
SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy dotnet run \
  -p:MonoGamePlatform=Native \
  -p:MonoGameProjectPath=<repo>/MonoGame.Framework/MonoGame.Framework.Native.csproj \
  -p:NativeRuntimePath=<repo>/Artifacts/native/mgruntime/desktopheadless/macosx/Release/libmgruntime.dylib
```

`PlatformInfo.GraphicsBackend` reports `GraphicsBackend.Headless` at runtime. Consumers that switch
on the backend to pick shader content should treat it like Vulkan and Metal: it reports **shader
profile 80**, the Vulkan profile, and loads the same compiled effect blobs, so effect loading
behaves identically to a real desktop backend.

## Building

No Vulkan SDK and no Xcode Metal toolchain are required — that is the point, since the machines this
targets have neither. The static SDL3 and FAudio dependencies are shared with the other native
targets; build them first if `external/` is empty (see the Metal instructions in [README.md](README.md)).

### macOS

```sh
cd native/monogame
premake5 --arch=arm64 --headless-only gmake && make config=release desktopheadless
```

`--headless-only` generates just this target, skipping the projects that need the Vulkan SDK or
Xcode.

### Linux

- SDL3 must be built with CMake as a **static** library with `-DSDL_UNIX_CONSOLE_BUILD=ON`, so it
  does not require X11/Wayland development packages.
- FAudio must be built and available to the link step.
- Generate the effect headers the runtime needs:
  `dotnet run --project build/Build.csproj -- --target="Build Vulkan Shaders"`.
- Then generate and build with premake as on macOS (`--headless-only gmake`,
  `make config=release desktopheadless`).

### Environment

`SDL_VIDEODRIVER=dummy` and `SDL_AUDIODRIVER=dummy` must be in the **process environment before the
host starts** (shell `export`, CI `env:`, or the test runner's launch environment).
`Environment.SetEnvironmentVariable` does not reach native SDL on Unix, so setting them from managed
code at startup has no effect.

## Deterministic frame stepping

```csharp
public void StepFrame(TimeSpan? elapsed = null)
```

Runs exactly one `Update` and one `Draw` (through `BeginDraw`/`EndDraw`/`Present`) with a synthetic
`GameTime`: `ElapsedGameTime` is `elapsed ?? TargetElapsedTime` and `TotalGameTime` advances by
exactly that. It never sleeps, never reads the wall clock and never performs catch-up updates, no
matter how large `elapsed` is. The first call initializes the game like `RunOneFrame` does
(`Platform.BeforeRun`, `Initialize`, `BeginRun`). `Exit()` is honored at the end of the step.
Existing drivers (`Run`, `Tick`, `RunOneFrame`) are unchanged. Use it to drive a headless game from
tests; do not mix it with `Run()` on the same instance.

## How it works

The fork's native runtime is already split into three C ABIs swapped per target —
**MGP** (windowing/input), **MGG** (graphics) and **MGA** (audio) — and each backend is composed in
`premake5.lua` from those parts. Headless changes only the graphics module:

```lua
project "desktopheadless"
common("desktopheadless")
sdl()        -- MGP: the same SDL3 layer every other target uses
headless()   -- MGG: no-op
faudio()     -- MGA: unchanged
```

**The windowing half needed almost nothing.** SDL3 ships a dummy video driver built for exactly this
case, and the window was already created `SDL_WINDOW_HIDDEN`. The only obstacle was that the SDL
window is requested with a compile-time graphics flag — `SDL_WINDOW_METAL`, `SDL_WINDOW_VULKAN` — and
the dummy driver supports none of them. `MG_HEADLESS` adds no flag, exactly as the DirectX 12 case
already does, and the window is then created successfully.

Two things that were expected to be problems were not: `MGP_Window_GetDrawableSize` already uses the
generic `SDL_GetWindowSizeInPixels` under SDL3 rather than a graphics-API call, and the macOS
live-resize watcher null-checks and simply stays dormant when no events arrive.

**Where state is cheap to keep, it is kept.** A stub that returns a plausible-looking wrong value
fails far away from the mistake, so buffers and textures retain what `SetData` wrote and round-trip
it back, the swapchain remembers its size so `GetBackBufferSize` reports what was requested, and the
adapter advertises a real non-zero display mode — a `0x0` mode makes the managed layer pick a `0x0`
resolution and crash creating render targets.
