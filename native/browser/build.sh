#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/emsdk-env.sh"
if [[ "$(git -C "$MONOGAME_ROOT/native/monogame/external/sdl3" rev-parse HEAD)" != fa2c02bb6e21974a89ea9824bc53c9932abe5f9c ]]; then
  echo "SDL3 must be pinned to release-3.4.16 (fa2c02bb6e21974a89ea9824bc53c9932abe5f9c)." >&2
  exit 1
fi
cmake -S "$MONOGAME_ROOT/native/browser/shader-tool" -B "$MONOGAME_ROOT/Artifacts/browser/shader-tool" -DCMAKE_BUILD_TYPE=Release
cmake --build "$MONOGAME_ROOT/Artifacts/browser/shader-tool" --parallel "${CMAKE_BUILD_PARALLEL_LEVEL:-4}"
export MONOGAME_BROWSER_SHADER_TRANSLATOR="$MONOGAME_ROOT/Artifacts/browser/shader-tool/mggl-shader"
dotnet build "$MONOGAME_ROOT/Tools/MonoGame.Effect.Compiler/MonoGame.Effect.Compiler.csproj" -c Release --nologo
mkdir -p "$MONOGAME_ROOT/Artifacts/browser/shaders"
for effect in AlphaTestEffect BasicEffect DualTextureEffect EnvironmentMapEffect SkinnedEffect SpriteEffect; do
  dotnet "$MONOGAME_ROOT/Artifacts/MonoGame.Effect.Compiler/Release/mgfxc.dll" \
    "$MONOGAME_ROOT/MonoGame.Framework/Platform/Graphics/Effect/Resources/$effect.fx" \
    "$MONOGAME_ROOT/Artifacts/browser/shaders/$effect.gl.mgfxo.h" /Profile:BrowserGL
done
cmake -S "$MONOGAME_ROOT/native/browser" -B "$MONOGAME_ROOT/Artifacts/browser/native" \
  -DCMAKE_TOOLCHAIN_FILE="$EMSDK_PATH/emscripten/cmake/Modules/Platform/Emscripten.cmake" \
  -DCMAKE_BUILD_TYPE=Release
cmake --build "$MONOGAME_ROOT/Artifacts/browser/native" --parallel "${CMAKE_BUILD_PARALLEL_LEVEL:-4}"
