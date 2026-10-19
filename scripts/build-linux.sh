#!/usr/bin/env bash
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"
export PATH="$HOME/.dotnet:/usr/local/cuda/bin:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
mkdir -p repos .build app runtime/linux-cuda
if [[ ! -d repos/jev.cpp/.git ]]; then
  git clone https://github.com/thomasgauthier/jev.cpp.git repos/jev.cpp
  git -C repos/jev.cpp checkout aa885f0a86efe36079d70b522f927cd2fc38683b
  git -C repos/jev.cpp apply "$ROOT/patches/autojev-prefix-cache.patch"
  git -C repos/jev.cpp apply "$ROOT/patches/autojev-context-cache.patch"
fi
# GB10 uses SM 12.1. Override for other GPUs.
cmake -S repos/jev.cpp -B .build/jev-cuda \
  -DCMAKE_BUILD_TYPE=Release -DGGML_CUDA=ON \
  -DCMAKE_CUDA_ARCHITECTURES="${CUDA_ARCHITECTURES:-121}" \
  -DGGML_NATIVE=OFF -DLLAMA_CURL=OFF -DLLAMA_BUILD_TESTS=OFF
cmake --build .build/jev-cuda --target llama-server -j "${BUILD_JOBS:-4}"
cp -a .build/jev-cuda/bin/llama-server .build/jev-cuda/bin/*.so* runtime/linux-cuda/
dotnet publish src/AutoJev.Local -c Release -o app
cp src/AutoJev.Local/index.html src/AutoJev.Local/usage.html app/
cp docs/api-agent.md app/api.md
printf '\nBuild complete. Run scripts/install-linux-service.sh next.\n'
