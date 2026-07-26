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

# rere レビュー #C-25: 旧実装はローリングタグ `continuous` の実行可能バイナリを
# sha256 / gpg 検証なしで取得して CI 内で実行していた。生成物 (AppImage) は R2 経由で
# エンドユーザーへ配布されるので、上流タグの差し替えやアカウント侵害が配布物へ直結する。
# さらに `curl` に --fail が無かったため、404 等のエラーページ本文を chmod +x して
# 実行しようとする経路も開いていた（失敗が「実行時の謎のエラー」に化ける）。
# deploy-relay.yml が wrangler を「サプライチェーン攻撃防止のため」固定しているのと方針を揃える。
#
# ⚠ APPIMAGETOOL_SHA256 は未設定。埋めるには次を実行して出た値をここへ書き写す:
#     curl -fLsS -o /tmp/appimagetool "$APPIMAGETOOL_URL" && sha256sum /tmp/appimagetool
#   値を入れると以降は不一致でビルドが止まる（差し替え検知）。空のままだと警告のみで続行する。
APPIMAGETOOL_URL=https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
APPIMAGETOOL_SHA256=""

cd build

if [[ ! -f "appimagetool" ]]; then
    # --fail: HTTP エラーを 0 終了させない / --show-error: 失敗理由を stderr に出す
    curl --fail --location --show-error --silent -o appimagetool "$APPIMAGETOOL_URL"

    actual_sha=$(sha256sum appimagetool | cut -d' ' -f1)
    if [[ -n "$APPIMAGETOOL_SHA256" ]]; then
        if [[ "$actual_sha" != "$APPIMAGETOOL_SHA256" ]]; then
            echo "appimagetool の sha256 が一致しません" >&2
            echo "  expected: $APPIMAGETOOL_SHA256" >&2
            echo "  actual  : $actual_sha" >&2
            rm -f appimagetool
            exit 1
        fi
    else
        echo "::warning::appimagetool の sha256 が未固定です (取得物: $actual_sha)。" \
             "package.linux.sh の APPIMAGETOOL_SHA256 に設定してください。"
    fi

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
