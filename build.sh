#!/usr/bin/env bash
# Builds the native solver for the game's Windows x64 process and then the BepInEx plugin,
# staging both OptimalPlay.dll and OptimalPlaySolver.dll in bin/Release/.
#
# Requirements:
#   - rustup with the nightly toolchain and the x86_64-pc-windows-gnu std target
#     (auto-installed by rustup for the nightly toolchain when first used)
#   - mingw-w64-gcc (Arch: sudo pacman -S mingw-w64-gcc) for the Windows linker
#   - dotnet SDK
set -euo pipefail
cd "$(dirname "$0")"

# Build from inside native/: cargo discovers .cargo/config.toml from the working
# directory, and that file is what pins the static CRT for the Windows target.
(cd native && cargo build --release --target x86_64-pc-windows-gnu)
dotnet build -c Release

echo
echo "Staged:"
ls -l bin/Release/OptimalPlay.dll bin/Release/OptimalPlaySolver.dll
