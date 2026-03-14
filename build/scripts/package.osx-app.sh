#!/usr/bin/env bash

set -euo pipefail

cd build

mkdir -p Ferry.app/Contents/Resources
mv Ferry Ferry.app/Contents/MacOS
cp resources/app/App.icns Ferry.app/Contents/Resources/App.icns
sed "s/FERRY_VERSION/$VERSION/g" resources/app/App.plist > Ferry.app/Contents/Info.plist
rm -rf Ferry.app/Contents/MacOS/Ferry.dsym
rm -f Ferry.app/Contents/MacOS/*.pdb

zip "ferry_$VERSION.$LABEL.zip" -r Ferry.app
