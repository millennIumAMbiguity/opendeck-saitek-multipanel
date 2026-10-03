#!/bin/sh
# Builds the plugin on Linux and installs it into OpenDeck's plugin folder.
# Native AOT needs the .NET 10 SDK, clang and zlib1g-dev; --no-aot builds a regular self-contained binary.
#   ./scripts/build-linux.sh [--no-aot]
set -e

root="$(cd "$(dirname "$0")/.." && pwd)"
plugin="io.github.millenniumambiguity.saitekmultipanel.sdPlugin"
staging="$root/artifacts/$plugin"
target="${XDG_CONFIG_HOME:-$HOME/.config}/opendeck/plugins/$plugin"
aot=true
[ "$1" = "--no-aot" ] && aot=false

rm -rf "$staging"
cp -r "$root/plugin/$plugin" "$staging"
dotnet publish "$root/src/SaitekMultiPanel/SaitekMultiPanel.csproj" -c Release -r linux-x64 -p:PublishAot=$aot -o "$staging/bin/linux-x64" --nologo -v q
find "$staging/bin" \( -name '*.pdb' -o -name '*.dbg' \) -delete

mkdir -p "$(dirname "$target")"
rm -rf "$target"
cp -r "$staging" "$target"
echo "Installed $target. Restart OpenDeck to load it."

if [ ! -f /etc/udev/rules.d/40-saitek-multipanel.rules ]; then
    echo "First time? Allow access to the panel without root:"
    echo "  sudo cp '$root/plugin/$plugin/40-saitek-multipanel.rules' /etc/udev/rules.d/"
    echo "  sudo udevadm control --reload-rules && sudo udevadm trigger"
fi
