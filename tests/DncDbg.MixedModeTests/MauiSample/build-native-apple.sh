#!/bin/bash
set -euo pipefail

platform="$1"
architecture="$2"
output="$3"
fixture="$(cd "$(dirname "$0")/../fixture" && pwd)"
objects="$(dirname "$output")"
mkdir -p "$objects"

case "$platform" in
  macos)
    target="${architecture}-apple-macos14.0"
    ;;
  maccatalyst)
    target="${architecture}-apple-ios15.0-macabi"
    ;;
  *)
    echo "Unsupported Apple native target: $platform" >&2
    exit 1
    ;;
esac

sdk="$(xcrun --sdk macosx --show-sdk-path)"
common=(-target "$target" -isysroot "$sdk" -O0 -g -fno-omit-frame-pointer -fPIC -Wall -Wextra -Werror)
xcrun --sdk macosx clang "${common[@]}" -std=c11 -c "$fixture/native.c" -o "$objects/native.o"
xcrun --sdk macosx clang++ "${common[@]}" -std=c++17 -c "$fixture/native.cpp" -o "$objects/native-cpp.o"
xcrun --sdk macosx clang++ -target "$target" -isysroot "$sdk" -dynamiclib \
  "$objects/native.o" "$objects/native-cpp.o" \
  -Wl,-install_name,@rpath/libmixedfixture.dylib \
  -o "$output"
