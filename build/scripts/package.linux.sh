#!/usr/bin/env bash

set -euo pipefail

arch=
appimage_arch=
target=
case "$RUNTIME" in
    linux-x64)
        arch=amd64
        appimage_arch=x86_64
        target=x86_64;;
    linux-arm64)
        arch=arm64
        appimage_arch=arm_aarch64
        target=aarch64;;
    *)
        echo "Unknown runtime $RUNTIME"
        exit 1;;
esac

APPIMAGETOOL_URL=https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage

cd build

if [[ ! -f "appimagetool" ]]; then
    curl -o appimagetool -L "$APPIMAGETOOL_URL"
    chmod +x appimagetool
fi

rm -f Ferry/*.dbg
rm -f Ferry/*.pdb

mkdir -p Ferry.AppDir/opt
mkdir -p Ferry.AppDir/usr/share/metainfo
mkdir -p Ferry.AppDir/usr/share/applications

cp -r Ferry Ferry.AppDir/opt/ferry
desktop-file-install resources/_common/applications/ferry.desktop --dir Ferry.AppDir/usr/share/applications \
    --set-icon com.1llum1n4t1s.Ferry --set-key=Exec --set-value=AppRun
mv Ferry.AppDir/usr/share/applications/{ferry,com.1llum1n4t1s.Ferry}.desktop
cp resources/_common/icons/ferry.png Ferry.AppDir/com.1llum1n4t1s.Ferry.png
ln -rsf Ferry.AppDir/opt/ferry/ferry Ferry.AppDir/AppRun
ln -rsf Ferry.AppDir/usr/share/applications/com.1llum1n4t1s.Ferry.desktop Ferry.AppDir
cp resources/appimage/ferry.appdata.xml Ferry.AppDir/usr/share/metainfo/com.1llum1n4t1s.Ferry.appdata.xml

ARCH="$appimage_arch" ./appimagetool -v Ferry.AppDir "ferry-$VERSION.linux.$arch.AppImage"

mkdir -p resources/deb/opt/ferry/
mkdir -p resources/deb/usr/bin
mkdir -p resources/deb/usr/share/applications
mkdir -p resources/deb/usr/share/icons
cp -f Ferry/* resources/deb/opt/ferry
ln -rsf resources/deb/opt/ferry/ferry resources/deb/usr/bin
cp -r resources/_common/applications resources/deb/usr/share
cp -r resources/_common/icons resources/deb/usr/share
# インストールサイズ（KB）を計算
installed_size=$(du -sk resources/deb | cut -f1)
# control ファイルを更新
sed -i -e "s/^Version:.*/Version: $VERSION/" \
    -e "s/^Architecture:.*/Architecture: $arch/" \
    -e "s/^Installed-Size:.*/Installed-Size: $installed_size/" \
    resources/deb/DEBIAN/control
# メンテナンススクリプトの実行権限を設定
chmod 0755 resources/deb/DEBIAN/preinst resources/deb/DEBIAN/prerm
# deb パッケージをビルド
dpkg-deb -Zgzip --root-owner-group --build resources/deb "ferry_$VERSION-1_$arch.deb"

rpmbuild -bb --target="$target" resources/rpm/SPECS/build.spec --define "_topdir $(pwd)/resources/rpm" --define "_version $VERSION"
mv "resources/rpm/RPMS/$target/ferry-$VERSION-1.$target.rpm" ./
