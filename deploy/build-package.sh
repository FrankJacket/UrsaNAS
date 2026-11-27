#!/bin/sh
# Builds an installable UrsaNAS package from the sources. Nothing is published and nothing is installed.
#
#   sh deploy/build-package.sh <x64|arm64> <version>      (example: sh deploy/build-package.sh arm64 1.0.2)
#
# Run it on Linux or in WSL, with the .NET 10 SDK and the Git submodules. The result is
# artifacts-package/ursanas-<version>-linux-<arch>.tar.gz, with its .sha256 file next to it.
# Only the x64 package is officially supported.
set -eu
umask 022

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
usage() { sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; }

case "${1:-}" in -h|--help) usage; exit 0 ;; esac
[ $# -eq 2 ] || { usage >&2; exit 2; }
ARCH=$1; VERSION=$2
case "$ARCH" in
  x64)   ELF_MACHINE=3e ;;
  arm64) ELF_MACHINE=b7 ;;
  *) die "unknown architecture '$ARCH' (x64 or arm64)" ;;
esac
printf '%s' "$VERSION" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$' || die "the version must look like 1.2.3 (got '$VERSION')"

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$ROOT"
command -v dotnet >/dev/null 2>&1 || PATH="$HOME/.dotnet:$PATH"
command -v dotnet >/dev/null 2>&1 || die "the .NET 10 SDK was not found"
[ -f external/Ursacopy/src/Ursacopy.Core/Ursacopy.Core.csproj ] && [ -f external/UrsaUITools/src/Ursa.UITools/Ursa.UITools.csproj ] \
  || die "the Git submodules are missing: run 'git submodule update --init --recursive'"

# The submodules have their own Directory.Build.props: these keep their output apart from a Windows build of the same folder.
export BaseIntermediateOutputPath=obj-linux/ MSBuildProjectExtensionsPath=obj-linux/ BaseOutputPath=bin-linux/
SOURCE=${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}

NAME="ursanas-$VERSION-linux-$ARCH"
OUT="$ROOT/artifacts-package"
# Built on the Linux file system: under /mnt/c (WSL) file permissions cannot be set.
STAGE=$(mktemp -d); trap 'rm -rf "$STAGE"' EXIT
P="$STAGE/$NAME"
mkdir -p "$P"

dotnet publish src/Nas.Agent -c Release -r "linux-$ARCH" --self-contained -p:PublishSingleFile=true -p:Version="$VERSION" --source "$SOURCE" -o "$P/Nas.Agent"
dotnet publish src/Nas.Web   -c Release -r "linux-$ARCH" --self-contained -p:Version="$VERSION" --source "$SOURCE" -o "$P/Nas.Web"

cp deploy/install.sh deploy/update-apply.sh deploy/ursanas-agent.service deploy/ursanas-web.service Install.md "$P/"
sed -i 's/\r$//' "$P/install.sh" "$P/update-apply.sh" "$P/ursanas-agent.service" "$P/ursanas-web.service"
echo "$VERSION" > "$P/VERSION"

for f in "$P/Nas.Agent/Nas.Agent" "$P/Nas.Web/Nas.Web"; do
  [ "$(od -An -tx1 -j18 -N1 "$f" | tr -d ' ')" = "$ELF_MACHINE" ] || die "unexpected processor for ${f#"$P"/}"
done

find "$P" -type d -exec chmod 755 {} +
find "$P" -type f -exec chmod 644 {} +
chmod 755 "$P/install.sh" "$P/update-apply.sh" "$P/Nas.Agent/Nas.Agent" "$P/Nas.Web/Nas.Web"

mkdir -p "$OUT"
tar -czf "$OUT/$NAME.tar.gz" --owner=0 --group=0 -C "$STAGE" "$NAME"
(cd "$OUT" && sha256sum "$NAME.tar.gz" > "$NAME.tar.gz.sha256")

printf '\nPackage: %s\n' "$OUT/$NAME.tar.gz"
