#!/usr/bin/env bash
# Build and run G-RAR on macOS.
# Usage: ./build-run-macos.sh [--release | -c Debug|Release] [-- app-args...]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT/src/G-RAR/G-RAR.csproj"
CONFIG="Debug"
APP_ARGS=()

usage() {
  cat <<'EOF'
Build and run G-RAR on macOS.

Usage:
  ./build-run-macos.sh [options] [-- app-args...]

Options:
  -c, --configuration <Debug|Release>  Build configuration (default: Debug)
  --release                            Shortcut for --configuration Release
  -h, --help                           Show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -c|--configuration)
      [[ $# -ge 2 ]] || { echo "error: $1 requires a value" >&2; exit 1; }
      CONFIG="$2"
      shift 2
      ;;
    --release)
      CONFIG="Release"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --)
      shift
      APP_ARGS+=("$@")
      break
      ;;
    *)
      APP_ARGS+=("$1")
      shift
      ;;
  esac
done

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: .NET SDK not found. Install .NET 9 from https://dotnet.microsoft.com/download" >&2
  exit 1
fi

ARCH="$(uname -m)"
case "$ARCH" in
  arm64) RID_HINT="osx-arm64" ;;
  x86_64) RID_HINT="osx-x64" ;;
  *) RID_HINT="$ARCH" ;;
esac

echo "Building and running G-RAR ($CONFIG) for macOS ($RID_HINT)..."
exec dotnet run --project "$PROJECT" --configuration "$CONFIG" -- "${APP_ARGS[@]}"
