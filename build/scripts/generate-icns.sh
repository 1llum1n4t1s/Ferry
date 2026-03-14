#!/usr/bin/env bash
# PNG から macOS 用 .icns ファイルを生成するスクリプト
# macOS 上で sips + iconutil を使用する

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

SOURCE_PNG="$ROOT_DIR/icon/app_icon.png"
OUTPUT_ICNS="$ROOT_DIR/build/resources/app/App.icns"
ICONSET_DIR="$ROOT_DIR/build/resources/app/App.iconset"

if [[ ! -f "$SOURCE_PNG" ]]; then
    echo "ERROR: ソース PNG が見つかりません: $SOURCE_PNG" >&2
    exit 1
fi

# iconset ディレクトリを作成
rm -rf "$ICONSET_DIR"
mkdir -p "$ICONSET_DIR"

# 各サイズのアイコンを生成
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$SOURCE_PNG" --out "$ICONSET_DIR/icon_${size}x${size}.png" > /dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$SOURCE_PNG" --out "$ICONSET_DIR/icon_${size}x${size}@2x.png" > /dev/null
done

# iconutil で .icns に変換
iconutil -c icns "$ICONSET_DIR" -o "$OUTPUT_ICNS"

# 一時ファイルを削除
rm -rf "$ICONSET_DIR"

echo "✓ App.icns を生成しました: $OUTPUT_ICNS"
