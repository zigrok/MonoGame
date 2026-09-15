#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/emsdk-env.sh"
SDL_SOURCE="$MONOGAME_ROOT/native/monogame/external/sdl3"
EXPECTED_SDL=fa2c02bb6e21974a89ea9824bc53c9932abe5f9c
if [[ "$(git -C "$SDL_SOURCE" rev-parse HEAD)" != "$EXPECTED_SDL" ]]; then
  echo "SDL3 must be pinned to release-3.4.16 ($EXPECTED_SDL)" >&2
  exit 1
fi
cmake -S "$SDL_SOURCE" -B "$MONOGAME_ROOT/Artifacts/browser/sdl3" \
  -DCMAKE_TOOLCHAIN_FILE="$EMSDK_PATH/emscripten/cmake/Modules/Platform/Emscripten.cmake" \
  -DCMAKE_BUILD_TYPE=Release -DSDL_SHARED=OFF -DSDL_STATIC=ON \
  -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF
cmake --build "$MONOGAME_ROOT/Artifacts/browser/sdl3" --parallel "${CMAKE_BUILD_PARALLEL_LEVEL:-4}"
