#!/usr/bin/env bash
# Source the Emscripten toolchain shipped with the pinned .NET workload, never a global emsdk.
set -euo pipefail
if [[ "$(dotnet --version)" != "10.0.103" ]]; then
  echo "This browser build requires the matched .NET SDK 10.0.103 / wasm-tools 10.0.110 toolchain." >&2
  return 1
fi
MONOGAME_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/share/dotnet}"
DOTNET_WASM_PACK_VERSION=10.0.10
DOTNET_EMSCRIPTEN_VERSION=3.1.56
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) EMSDK_HOST=osx-arm64 ;;
  Linux-x86_64) EMSDK_HOST=linux-x64 ;;
  *) echo "Unsupported pinned toolchain host: $(uname -s)-$(uname -m)" >&2; return 1 ;;
esac
PACKS="$DOTNET_ROOT/packs"
EMSDK_PATH="$PACKS/Microsoft.NET.Runtime.Emscripten.$DOTNET_EMSCRIPTEN_VERSION.Sdk.$EMSDK_HOST/$DOTNET_WASM_PACK_VERSION/tools"
export DOTNET_EMSCRIPTEN_LLVM_ROOT="$EMSDK_PATH/bin"
export DOTNET_EMSCRIPTEN_BINARYEN_ROOT="$EMSDK_PATH"
export DOTNET_EMSCRIPTEN_NODE_JS="$PACKS/Microsoft.NET.Runtime.Emscripten.$DOTNET_EMSCRIPTEN_VERSION.Node.$EMSDK_HOST/$DOTNET_WASM_PACK_VERSION/tools/bin/node"
export EM_CACHE="$PACKS/Microsoft.NET.Runtime.Emscripten.$DOTNET_EMSCRIPTEN_VERSION.Cache.$EMSDK_HOST/$DOTNET_WASM_PACK_VERSION/tools/emscripten/cache"
export PATH="$EMSDK_PATH/emscripten:$EMSDK_PATH/bin:$PATH"
mkdir -p "$MONOGAME_ROOT/Artifacts/browser/scratch"
export TMPDIR="$MONOGAME_ROOT/Artifacts/browser/scratch"
test -x "$EMSDK_PATH/emscripten/emcc"
test -x "$DOTNET_EMSCRIPTEN_NODE_JS"
