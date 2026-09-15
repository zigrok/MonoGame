#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/emsdk-env.sh"
bash "$MONOGAME_ROOT/native/browser/build-sdl.sh"
emcc -O2 -I"$MONOGAME_ROOT/native/monogame/external/sdl3/include" \
  -c "$MONOGAME_ROOT/native/browser/proof/proof.c" -o "$MONOGAME_ROOT/Artifacts/browser/proof.o"
emar rcs "$MONOGAME_ROOT/Artifacts/browser/libbrowserproof.a" "$MONOGAME_ROOT/Artifacts/browser/proof.o"
dotnet publish "$MONOGAME_ROOT/native/browser/proof/NativeProof.csproj" -c Release \
  -o "$MONOGAME_ROOT/Artifacts/browser/proof"
