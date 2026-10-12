#!/usr/bin/env bash
# Puts the README's screenshots and demo into site/assets/generated, so the website always shows the
# same pictures as the README: the screenshots, the demo as a video with its first frame as a poster,
# a social preview cut from Overview, and the brand mark. Needs ffmpeg. The Pages workflow runs it;
# run it yourself to preview the site, then open site/index.html.
set -euo pipefail
cd "$(dirname "$0")/.."

out=site/assets/generated
rm -rf "$out"
mkdir -p "$out"

cp docs/screenshots/*.png "$out/"
cp docs/brand/mark-dark.svg docs/brand/app-icon-256.png "$out/"

# H.264 with even dimensions and the index up front, so it starts playing before it has downloaded.
ffmpeg -v error -y -i docs/screenshots/demo.gif -an -c:v libx264 -preset slow -crf 23 \
  -pix_fmt yuv420p -vf "scale=trunc(iw/2)*2:trunc(ih/2)*2" -movflags +faststart "$out/demo.mp4"
ffmpeg -v error -y -i docs/screenshots/demo.gif -frames:v 1 "$out/demo-poster.png"
ffmpeg -v error -y -i docs/screenshots/overview.png -vf "scale=1200:-1,crop=1200:630:0:0" "$out/og.png"

echo "Staged $(ls "$out" | wc -l) files in $out"
