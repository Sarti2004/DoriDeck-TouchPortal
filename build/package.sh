#!/bin/sh
# Builds one Touch Portal package per runtime: dist/DoriDeck-<rid>.tpp
# Usage: build/package.sh [rid ...]   (default: win-x64 osx-arm64)
set -eu

root="$(cd "$(dirname "$0")/.." && pwd)"
rids="${*:-win-x64 osx-arm64}"

# Clear contents only; Windows refuses to delete a folder open in Explorer or a shell.
mkdir -p "$root/dist"
rm -rf "$root/dist"/*

for rid in $rids; do
	staging="$root/dist/staging/$rid"
	dotnet publish "$root/src/DoriDeck.csproj" -c Release -r "$rid" \
		--self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
		-p:DebugType=none -o "$staging/DoriDeck"
	# Git Bash has no zip; fall back to the bsdtar that ships with Windows, which can write zip archives.
	if command -v zip >/dev/null 2>&1; then
		(cd "$staging" && zip -qr "../../DoriDeck-$rid.tpp" DoriDeck)
	else
		(cd "$staging" && "${SYSTEMROOT:-C:/Windows}/System32/tar.exe" --format zip -cf "../../DoriDeck-$rid.tpp" DoriDeck)
	fi
	echo "Packaged dist/DoriDeck-$rid.tpp"
done

rm -rf "$root/dist/staging"

# Pages are imported separately in TP (Pages > Import page), so they ship next to the .tpp as well.
cp "$root/src/Pages/"*.tpz2 "$root/dist/"
echo "Copied pages to dist/"
