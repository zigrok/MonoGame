# MonoGame Tests

The MonoGame Tests run against XNA on Windows and MonoGame on Windows, macOS and Linux. They serve as an assurance that MonoGame conforms as closely as possible to XNA.

Simple unit tests make assertions about MonoGame's core class properties, methods and behavior to guarantee compatibility with XNA in those regards. Additionally, visual tests verify via frame capture and comparison that MonoGame renders equivalently to XNA.

Currently, on Windows, the tests can be run using NUnit and target either XNA or MonoGame. On macOS and Linux, the tests target MonoGame and are implemented in an executable assembly that can be run and debugged directly. After execution using the custom test runner, and HTML report of the results will be loaded in your default browser, and a log of stdout can be found in bin\$(Configuration)\stdout.txt.

In Visual Studio 2022 (Windows) or Visual Studio for Mac, use the Test Explorer to run the tests.

To run them from the command-line (Windows/Mac/Linux):

```bash
dotnet test Tests/MonoGame.Tests.DesktopGL.csproj
```

To list all the tests:

**Note** These are quick hints, for extensive help use `dotnet test --help`.

```bash
dotnet test Tests/MonoGame.Tests.DesktopGL.csproj -t
```

To run/filter specific classes/namespaces, an example:

```bash
dotnet test Tests/MonoGame.Tests.DesktopGL.csproj --filter MonoGame.Tests.Visual
```

## Native text-input shortcut regressions

These focused checks create no windows and inject no operating-system input.
Run from the MonoGame directory, with the native/.NET build slot available:

```sh
mkdir -p Artifacts/Tests/NativeInput
c++ -std=c++17 -Wall -Wextra -Werror native/monogame/tests/TextInputStateTest.cpp \
  -o Artifacts/Tests/NativeInput/text-input-state
Artifacts/Tests/NativeInput/text-input-state
dotnet test Tests/MonoGame.Tests.DesktopGL.csproj \
  --filter FullyQualifiedName~TextCompositionIndexTest --disable-build-servers \
  -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false \
  '-p:DefaultItemExcludes=**/obj/**'
dotnet build MonoGame.Framework/MonoGame.Framework.Native.csproj -c Release \
  --disable-build-servers -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false
```

The exclusion avoids compiling generated C# left by the test asset projects
under `Tests/Assets/Projects/obj`; it does not delete or alter those artifacts.

On macOS, SDL3 Cocoa dispatches keys before AppKit's normal `sendEvent:` handling.
Its text responder sends a pending raw key before directly interpreted text, but
clears that key when committing marked text. Forwarding both events unchanged
allowed Control+Option+Space to switch input sources **and** type a space.

The native SDL bridge now uses each key event's own modifiers, not the final
`SDL_GetModState()` after pumping the queue. On macOS, direct Command/Control
shortcut text is discarded before either rich commits or legacy character
dispatch. Modified Space is reserved from raw game/UI polling too, including
repeat and release handling. Other shortcut keys remain available for application
commands and navigation. This reserves Command/Control+Space even when the user
has disabled their usual system bindings; it does not attempt to discover every
custom system shortcut.

Preedit (including the empty event before a commit), key-up, focus loss, text
session changes, and a drained event queue clear shortcut provenance. This avoids
rejecting an IME commit merely because a modifier remains held. Option/Shift
text, dead keys, composed Unicode and Windows AltGr are not shortcut-filtered.
The managed text handlers intentionally do not apply a held-modifier text veto.
Their control-character compatibility path and old-runtime Space polling fallback
use macOS-specific command semantics.

The rich bridge remains opt-in; event layouts and native entry points are
unchanged by this shortcut fix. The native SDL2/browser translation is unchanged.
Old native libraries still use the existing capability fallback, but need a rebuilt
SDL3 runtime for text-event provenance filtering; the managed fallback alone
cannot reliably reconstruct IME provenance from legacy characters.

The pure tests do not establish real keyboard-layout/IME acceptance. Rebuild a
fresh native runtime and disposable host, then have the user test actual plain
spaces, Control+Option+Space, Command/Control shortcuts, Option dead keys and IME
commits. Do not replace or launch an existing user's host as part of these tests.

## Native macOS live resize

SDL3's Cocoa backend runs a 60 Hz timer in tracking mode and synchronously emits
`SDL_EVENT_WINDOW_EXPOSED` with `data1 == 1`. The ordinary native poll cannot
return until the border drag ends; processing queued resize events alone only
fixes the final frame. Native MonoGame now watches that specific notification
while the synchronous game loop is polling and calls its normal Update/Draw
tick on the owning main thread. No sample/host resize adapter is required.

Logical client bounds are refreshed from SDL, then the existing swapchain-size
sync updates the physical backbuffer, viewport, scissor and Metal depth/MSAA
targets **before** layout notifications and Update. The callback never pumps
events or echoes the user resize through `SDL_SetWindowSize`. Queued macOS resize
events read current dimensions instead of reverting to pre-callback geometry.
Fixed-step games retain their timing but do not sleep/spin inside the tracking
callback when the next update is not due. `SuppressDraw`/`BeginDraw` remain in
control; the callback does not directly force presentation.

The watch exists only during `Game.Run`'s synchronous native loop; hidden,
minimized, unfocused and foreign-thread events are ignored. `RunOneFrame`,
manual hidden probes, browser, SDL2 and non-macOS loops are unchanged. Reentrant
frames are rejected. Callback failures are captured and rethrown after the
native poll returns; disposal during a callback stops frames and defers native
window/platform destruction until that same return. Exit and exceptions remove
the watch before releasing its rooted managed delegate. The new optional
registration entry point does not change existing event structs or exports:
older native binaries fall back to their post-drag behavior.

With the native/.NET build slot available, run the windowless dispatcher tests:

```sh
dotnet test Tests/MonoGame.Tests.DesktopGL.csproj \
  --filter FullyQualifiedName~LiveResizeFrameDispatcherTest --disable-build-servers \
  -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false \
  '-p:DefaultItemExcludes=**/obj/**'
```

These tests establish callback thread affinity, polling-only eligibility,
nonreentrancy, disposal and exception containment, not visible Cocoa/Metal
acceptance. Rebuild a **new** runtime and disposable host; the user must verify
continuous layout and animation while dragging (including pausing without
releasing), Retina sizing, no size rollback on release, then focus/selection,
IME and hide/minimize/exit behavior. Do not launch or replace an existing user's
host or remove hidden/offscreen activation guards for this test.

## Rendering Tests

### Desktop Metal backbuffer preservation

On macOS, rebuild the native runtime and run the existing NUnitLite runner on its
main UI thread (no offscreen frame-capture substitute):

```sh
make -C native/monogame -f desktopmetal.make config=release -j4
dotnet build Tests/MonoGame.Tests.DesktopMetal.csproj -c Release
cd Tests
MTL_DEBUG_LAYER=1 dotnet ../Artifacts/Tests/DesktopMetal/Release/MonoGame.Tests.dll \
  --where='test =~ MetalBackbuffer_ResumedPassesPreservePixels' --workers=0 \
  --result=../Artifacts/Tests/DesktopMetal/metal-backbuffer-results.xml
```

These GPU-readback regressions cover single-sample and 4x MSAA drawables over two
presented frames: deterministic first initialization, resumed passes, readback
flushes, render-target switches, explicit color/depth/stencil clears and retained
depth/stencil tests. Backbuffer MSAA passes must both store samples and resolve;
resolving alone cannot preserve samples for a subsequent load.

Most rendering tests do not require a game loop, but just need a GraphicsDevice to be able to render things. These tests can inherit from GraphicsDeviceTestFixtureBase and use the supplied GraphicsDevice 'gd' to render. Tests that require rendering were formerly implemented with the VisualTestFixtureBase (we call these "visual tests"), but this is no longer recommended unless the test requires an actual Game loop or tests functionality of the Game class itself because these tests are slower and harder to implement. When creating a new rendering test, the first run will fail because there is no reference image. Running the test will capture and save the result. Run the test with the XNA test project to get a reference image that checks XNA compatibility, or with MG to make sure no regression occurs. After adding the captured frame as a reference image, the test should pass.

### GraphicsDeviceTestFixtureBase

There are 3 methods in GraphicsDeviceTestFixtureBase that can be used to capture a frame and compare it to a reference image.

- `PrepareFrameCapture`: Call this before rendering, so a RenderTarget can be prepared and set as the target for the GraphicsDevice. Optionally pass in the amount of frames you expect to capture. This is used for naming the captures when saving them (i.e. frame1.png or frame001.png) and the amount of captured frames will also be checked explicitly unless the ExactNumberSubmits flag is set to false.
- `SubmitFrame`: Store the content of the render target in a list of submitted frames
- `CheckFrames`: Call this when all frames are submitted. Checks all submitted frames against reference images if they can be found. Will also write out the captured image and a diff of captured image vs reference image if required by the WriteCapture and WriteDiffs settings. Compared frames get a similarity score between 0 and 1. If this score is higher than the set Similarity the test will pass. For ease of use, when no frame was submitted but PrepareFrameCapture has been called, this will submit a frame. Because of this tests that only submit one frame just need to be wrapped in PrepareFrameCapture/CheckFrames calls and can render as usual in between.


If you still think you need a visual test to properly test a feature, read the paragraphs below to get a sense of how to get started.

MonoGame's visual tests are implemented as  `Game`s and `GameComponent`s whose output is captured and compared to known-good reference renderings. Performance is ignored in these tests: the focus here is correct drawing.

### Workflow for Implementing a Visual Test

A good visual test, like any good test, should perform the minimum work necessary to verify that the functionality under test is correct. As much as possible, drawing and test code should be made modular by inheriting from  `GameComponent`,  `DrawableGameComponent`, `VisualTestGameComponent`, or  `VisualTestDrawableGameComponent` to encourage reuse, rather than duplication, in other tests.

Here is one possible workflow for implementing a visual test (assuming the test fixture is already set up):

> Before you start: 
> Examine the existing visual tests to get a sense of how components are being composed into a test and what components may already satisfy part of the requirements of the new test.


1. Implement any new drawing logic needed in a new subclass of one of the **Component** base classes.
2. Compose your test Game in a new [Test] method. As this stage, you can run the new test directly to visually verify the rendering.
3. Add a FrameCompareComponent to your test Game with at least one `IFrameComparer` implementation. Use the  `FrameCompareComponent`'s predicate to capture and compare frames.
4. Pass the FrameCompareComponent.Results to the diffing, logging and assertion utility methods provided by  `VisualTestFixtureBase`
5. The first time a visual test is run, it will fail for lack of reference images to compare the captured images to. However, it will write the captured frames to  `bin\$(Configuration)\CapturedFrames\{TestDir}`
6. Proof the images generated from the first run to ensure that they are correct, then add them to the test project in `Assets\ReferenceImages\$TestDir`

    **Be sure to add them in the  projects for all platforms!** These files should have their build actions set to "Compile" and "Copy if newer"

7. Rerun the visual test. The reference images should be copied into place and the test should now pass.
8. XOR diffs between the reference images and captured frames are output into  `bin\$(Configuration)\Diffs\{TestDir}` for debugging purposes.

### Notes For Implementing Correct Visual Tests

- **Visual tests must be marked as [RequiresSTA] to work correctly on all platforms**
- Try not to rely on  `GameComponent.Update`, since capturing frames can make the game run slowly and result in multiple Update calls per Draw. Instead, inherit from  `VisualTestGameComponent` or `VisualTestDrawableGameComponent` and override `UpdateOncePerDraw`.
- For similarity thresholds, prefer `Constants.StandardRequiredSimilarity` unless there is a very good reason to choose another value. This will allow the strictness of all the tests to be adjusted together in the future, as requirements change.
- Use the Paths static class to reduce typos in resource paths.

## Special Considerations

- For new test fixtures, call Paths.SetStandardWorkingDirectory() in [SetUp] \(VisualTestBase does this for you\) to ensure that the `ContentManager` can find your assets on all platforms.
- Note that all platforms are forced to run in Synchronous mode and that this doesn't always work perfectly on all platforms yet.

## NUnit Configuration

There are a few things to know about running these tests under NUnit:

- You must run the **-x86** versions of NUnit, because XNA won't work with the 64-bit versions.
- You must disable shadow copying because having it enabled makes it impossible for the ContentManager to find any assets.
 -  `GUI: Tools > Settings > Test Loader > Advanced`
 -  `CLI: /noshadow`
- For debugger support, Run tests directly in the NUnit process, (note that this may cause a few-seconds-long hang when exiting NUnit after running a visual test) otherwise choose 'single separate process'
 - This setting can be found in: `Tools > Settings > Test Loader > Assembly Isolation`
