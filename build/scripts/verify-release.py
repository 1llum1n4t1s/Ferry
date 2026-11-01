#!/usr/bin/env python3
"""同じ run の配布実物を検査し、送信直前にその SHA-256 を再照合する。標準ライブラリのみ。"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import struct
import subprocess
import sys
import tempfile
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

# Windows runner の既定 cp1252 でも日本語の検証結果を出力できるようにする (Python 3.8+)。
for stream in (sys.stdout, sys.stderr):
    if hasattr(stream, 'reconfigure'):
        stream.reconfigure(encoding='utf-8')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def run(*args, cwd=None, env=None):
    return subprocess.check_output([str(a) for a in args], cwd=cwd, env=env, text=True).strip()


def digest(path, algorithm='sha256'):
    h = hashlib.new(algorithm)
    with open(path, 'rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def metadata(data):
    root = ET.fromstring(data)
    return {e.tag.rsplit('}', 1)[-1]: e.text for e in root.iter()}


def native(data, rid):
    arch = rid.rsplit('-', 1)[1]
    if rid.startswith('win-'):
        require(data[:2] == b'MZ', 'PE header missing')
        offset = struct.unpack_from('<I', data, 60)[0]
        require(data[offset:offset + 4] == b'PE\0\0', 'PE signature missing')
        machine = struct.unpack_from('<H', data, offset + 4)[0]
        require(machine == {'x64': 0x8664, 'arm64': 0xaa64}[arch], 'PE RID mismatch')
    elif rid.startswith('linux-'):
        require(data[:6] == b'\x7fELF\x02\x01', '64-bit little-endian ELF missing')
        require(struct.unpack_from('<H', data, 18)[0] == {'x64': 62, 'arm64': 183}[arch], 'ELF RID mismatch')
    else:
        require(data[:4] == b'\xcf\xfa\xed\xfe', '64-bit Mach-O missing')
        require(struct.unpack_from('<I', data, 4)[0] == 0x100000c, 'Mach-O arm64 mismatch')


def check_binary(path, rid, original=None):
    with open(path, 'rb') as stream:
        native(stream.read(4096), rid)
    if original:
        require(digest(path) == digest(original), f'publish payload mismatch: {path}')


def check_payload(source, target, signed=False):
    """publish の全実ファイルを確認する。既存 pack が除く診断用出力だけを除外する。"""
    for original in source.rglob('*'):
        relative = original.relative_to(source)
        if (not original.is_file() or '_resources' in relative.parts
                or any(p.lower().endswith('.dsym') for p in relative.parts)
                or original.suffix.lower() in ('.pdb', '.dbg')):
            continue
        packaged = target / relative
        require(packaged.is_file(), f'publish file missing: {relative}')
        if signed and original.suffix.lower() in ('.dylib', '.so', '.dll'):
            # macOS の pack は native library にも署名し bytes を変更する。
            run('codesign', '--verify', '--strict', '--verbose=2', packaged)
            details = subprocess.run(['codesign', '-d', '--verbose=4', str(packaged)], capture_output=True, text=True, check=True)
            require('Authority=Developer ID Application:' in details.stderr, f'Developer ID library signature missing: {relative}')
        elif signed and original.name == 'Ferry':
            # main executable は app_bundle の Developer ID 検証で保全する。
            continue
        else:
            require(digest(original) == digest(packaged), f'publish file hash mismatch: {relative}')


def check_plist(path, version):
    info = plistlib.loads(path.read_bytes())
    require(info.get('CFBundleExecutable') == 'Ferry', 'bundle executable mismatch')
    require(info.get('CFBundleShortVersionString') == version, 'bundle version mismatch')
    require(info.get('CFBundleVersion') == version + '.0', 'bundle build version mismatch')


def extract_zip(path, destination, velopack_links=False):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), f'duplicate ZIP entries: {path}')
        require(all(not n.startswith(('/', '\\')) and '..' not in n.replace('\\', '/').split('/') for n in names), 'unsafe ZIP entry')
    if sys.platform == 'darwin':
        run('ditto', '-x', '-k', path, destination)
    else:
        with zipfile.ZipFile(path) as archive:
            archive.extractall(destination)
    if velopack_links:
        # nupkg はシンボリックリンクを .__symlink の UTF-8 相対パスとして保存する。
        # codesign 前に本来の bundle 構成へ戻し、展開領域外へのリンクは拒否する。
        for marker in destination.rglob('*.__symlink'):
            link = marker.with_name(marker.name[:-len('.__symlink')])
            target = marker.read_text(encoding='utf-8')
            require(not os.path.isabs(target), 'absolute Velopack link')
            resolved = (link.parent / target).resolve()
            require(os.path.commonpath([str(destination.resolve()), str(resolved)]) == str(destination.resolve()), 'escaping Velopack link')
            require(resolved.exists() and not link.exists(), 'invalid Velopack link')
            link.symlink_to(target)
            marker.unlink()


def app_bundle(root, version, signed=False):
    plists = list(root.rglob('Contents/Info.plist'))
    # ランタイムの補助 bundle ではなく Ferry 本体だけを対象にする。
    plists = [p for p in plists if plistlib.loads(p.read_bytes()).get('CFBundleExecutable') == 'Ferry']
    require(len(plists) == 1, 'exactly one Ferry app bundle required')
    check_plist(plists[0], version)
    app = plists[0].parent.parent
    check_binary(app / 'Contents/MacOS/Ferry', 'osx-arm64')
    if signed:
        run('codesign', '--verify', '--deep', '--strict', '--verbose=2', app)
        # ad-hoc 署名を成功扱いにしない。Developer ID の署名であることも確認する。
        details = subprocess.run(['codesign', '-d', '--verbose=4', str(app)], capture_output=True, text=True, check=True)
        require('Authority=Developer ID Application:' in details.stderr, 'Developer ID app signature missing')
    return app / 'Contents/MacOS/Ferry'


def appimage(path, root, rid, original):
    data = path.read_bytes()
    # runtime 内の magic 文字列ではなく、有効な SquashFS v4 header を選ぶ。
    offsets = []
    start = 0
    while True:
        offset = data.find(b'hsqs', start)
        if offset < 0:
            break
        if offset + 96 <= len(data) and struct.unpack_from('<HH', data, offset + 28) == (4, 0):
            size = struct.unpack_from('<Q', data, offset + 40)[0]
            if 96 <= size <= len(data) - offset:
                offsets.append(offset)
        start = offset + 4
    require(len(offsets) == 1, 'AppImage squashfs missing or ambiguous')
    offset = offsets[0]
    run('unsquashfs', '-no-progress', '-o', str(offset), '-d', root, path)
    candidates = [p for p in root.rglob('ferry') if p.is_file() and not p.is_symlink()]
    require(len(candidates) == 1, 'AppImage Ferry payload missing or ambiguous')
    check_binary(candidates[0], rid, original)
    check_payload(original.parent, candidates[0].parent)
    return candidates[0]


def check_feed(root, rid, version, original, work):
    package_name = f'Ferry-{version}-{rid}-full.nupkg'
    expected_index = {package_name: 'Full'}
    if rid == 'osx-arm64':
        expected_index.update({f'Ferry-{rid}-Setup.pkg': 'Installer', f'Ferry-{rid}-Portable.zip': 'Portable'})
    else:
        expected_index[f'Ferry-{rid}.AppImage'] = 'Portable'
    index = json.loads((root / ('assets.' + rid + '.json')).read_text(encoding='utf-8'))
    require(isinstance(index, list) and len(index) == len(expected_index), 'feed asset count mismatch')
    require({(item['RelativeFileName'], item['Type']) for item in index} == set(expected_index.items()), 'feed asset set/type mismatch')
    expected_files = set(expected_index) | {'releases.' + rid + '.json', 'assets.' + rid + '.json'}
    actual_files = {p.name for p in root.iterdir()}
    require(actual_files - {'RELEASES-' + rid} == expected_files, 'feed output set mismatch')
    require(all((root / name).is_file() for name in expected_files), 'feed asset file missing')
    manifest = root / ('releases.' + rid + '.json')
    assets = json.loads(manifest.read_text(encoding='utf-8'))['Assets']
    require(len(assets) == 1 and assets[0]['Type'] == 'Full', 'fresh feed must contain one full package')
    asset = assets[0]
    require(asset['PackageId'] == 'Ferry' and asset['Version'] == version, 'feed identity mismatch')
    name = asset['FileName']
    require(Path(name).name == name and name == f'Ferry-{version}-{rid}-full.nupkg', 'feed filename mismatch')
    package = root / name
    require(package.stat().st_size == asset['Size'], 'feed size mismatch')
    for key, algorithm in [('SHA256', 'sha256'), ('SHA1', 'sha1')]:
        require(digest(package, algorithm).lower() == asset[key].lower(), f'feed {key} mismatch')
    with zipfile.ZipFile(package) as archive:
        spec = metadata(archive.read('Ferry.nuspec'))
        require(spec.get('id') == 'Ferry' and spec.get('version') == version, 'nuspec identity mismatch')
        require(spec.get('channel') == rid, 'nuspec channel mismatch')
        if rid == 'osx-arm64':
            # mac vpk は generic rid=osx を出す。CPU は実 Mach-O で必ず確認する。
            require(spec.get('rid') in ('osx', rid), 'nuspec mac RID mismatch')
            require(spec.get('machineArchitecture') in (None, 'arm64'), 'nuspec mac CPU mismatch')
        else:
            require(spec.get('rid') == rid, 'nuspec RID mismatch')
            require(spec.get('machineArchitecture') == rid.rsplit('-', 1)[1], 'nuspec CPU mismatch')
        require(spec.get('os') == rid.split('-')[0], 'nuspec OS mismatch')
    extracted = work / 'nupkg'
    extract_zip(package, extracted, velopack_links=True)
    if rid.startswith('osx-'):
        payload = app_bundle(extracted, version, signed=True)
        check_payload(original.parent, payload.parent, signed=True)
    else:
        # Linux の更新 nupkg は publish directory ではなく完成 AppImage を格納する。
        bundled_image = extracted / 'lib/app/Ferry.AppImage'
        require(bundled_image.is_file(), 'nupkg AppImage missing')
        payload = appimage(bundled_image, work / 'nupkg-appimage', rid, original)
    for entry in root.iterdir():
        if entry.suffix == '.pkg':
            require(rid == 'osx-arm64', 'unexpected pkg')
            signature = run('pkgutil', '--check-signature', entry)
            require('Developer ID Installer:' in signature, 'Developer ID installer signature missing')
            run('xcrun', 'stapler', 'validate', entry)
            run('spctl', '--assess', '--type', 'install', '--verbose=2', entry)
            expanded = work / 'installer'
            run('pkgutil', '--expand-full', entry, expanded)
            installer_payload = app_bundle(expanded, version, signed=True)
            require(digest(installer_payload) == digest(payload), 'pkg/feed app mismatch')
            check_payload(payload.parent, installer_payload.parent)
        elif entry.suffix == '.zip':
            target = work / entry.stem
            extract_zip(entry, target)
            require(rid == 'osx-arm64', 'unexpected feed portable')
            portable_payload = app_bundle(target, version, signed=True)
            require(digest(portable_payload) == digest(payload), 'portable/feed app mismatch')
            check_payload(payload.parent, portable_payload.parent)
        elif entry.suffix == '.AppImage':
            require(digest(entry) == digest(bundled_image), 'portable/nupkg AppImage mismatch')
            appimage(entry, work / entry.stem, rid, original)
        else:
            require(entry.suffix in ('.nupkg', '.json') or entry.name == 'RELEASES-' + rid, f'unexpected feed file: {entry}')
    require(len(list(root.glob('*.nupkg'))) == 1, 'unexpected nupkg')
    if rid == 'osx-arm64':
        require(len(list(root.glob('*.pkg'))) == 1, 'signed notarized pkg required')
    legacy = root / ('RELEASES-' + rid)
    if legacy.exists():
        fields = legacy.read_text(encoding='utf-8-sig').split()
        require(len(fields) == 3 and fields[0].lower() == asset['SHA1'].lower() and fields[1:] == [name, str(asset['Size'])], 'legacy feed mismatch')


def check_package(root, rid, label, version, original, work):
    if rid.startswith(('win-', 'osx-')):
        package = root / f'ferry_{version}.{label}.zip'
        require(set(root.glob('ferry_*.zip')) == {package}, 'unexpected portable output')
        extract_zip(package, work)
        if rid.startswith('win-'):
            binary = work / 'Ferry/Ferry.exe'
            check_binary(binary, rid, original)
            check_payload(original.parent, binary.parent)
            os.environ['FERRY_VERIFY_BINARY'] = str(binary)
            actual = run('pwsh', '-NoProfile', '-Command', '(Get-Item -LiteralPath $env:FERRY_VERIFY_BINARY).VersionInfo.ProductVersion')
            require(actual.split('+')[0] == version, 'PE product version mismatch')
        else:
            binary = app_bundle(work, version)
            check_binary(binary, rid, original)
            check_payload(original.parent, binary.parent)
        return [package]
    arch, rpm_arch = {'linux-x64': ('amd64', 'x86_64'), 'linux-arm64': ('arm64', 'aarch64')}[rid]
    deb = root / f'ferry_{version}-1_{arch}.deb'
    rpm = root / f'ferry-{version}-1.{rpm_arch}.rpm'
    image = root / f'ferry-{version}.linux.{arch}.AppImage'
    require(set(root.glob('ferry-*.AppImage')) | set(root.glob('ferry_*.deb')) | set(root.glob('ferry-*.rpm')) == {deb, rpm, image}, 'unexpected Linux package output')
    require(run('dpkg-deb', '-f', deb, 'Package') == 'ferry', 'deb name mismatch')
    require(run('dpkg-deb', '-f', deb, 'Version') == version, 'deb version mismatch')
    require(run('dpkg-deb', '-f', deb, 'Architecture') == arch, 'deb RID mismatch')
    run('dpkg-deb', '-x', deb, work / 'deb')
    check_binary(work / 'deb/opt/ferry/ferry', rid, original)
    check_payload(original.parent, work / 'deb/opt/ferry')
    require(run('rpm', '-qp', '--qf', '%{NAME}|%{VERSION}|%{RELEASE}|%{ARCH}', rpm) == f'ferry|{version}|1|{rpm_arch}', 'rpm metadata mismatch')
    (work / 'rpm').mkdir()
    # 既存 rpm2cpio/cpio で最終 rpm の実物を展開する。
    with open(work / 'payload.cpio', 'wb') as output:
        subprocess.run(['rpm2cpio', str(rpm.resolve())], stdout=output, check=True)
    with open(work / 'payload.cpio', 'rb') as source:
        subprocess.run(['cpio', '-id', '--quiet'], stdin=source, cwd=work / 'rpm', check=True)
    check_binary(work / 'rpm/opt/ferry/ferry', rid, original)
    check_payload(original.parent, work / 'rpm/opt/ferry')
    appimage(image, work / 'appimage', rid, original)
    return [deb, rpm, image]


def check_remote(root, version):
    """CDN のキャッシュ済み 404 を使わず、R2 一覧と実物で同版不変を確認する。"""
    account = os.environ['CLOUDFLARE_ACCOUNT_ID']
    endpoint = f'https://api.cloudflare.com/client/v4/accounts/{account}/r2/buckets/ferry-updates/objects'
    keys, cursors = set(), set()
    cursor = ''
    while True:
        query = {'per_page': '1000'}
        if cursor:
            query['cursor'] = cursor
        request = urllib.request.Request(endpoint + '?' + urllib.parse.urlencode(query), headers={
            'Authorization': 'Bearer ' + os.environ['CLOUDFLARE_API_TOKEN']})
        with urllib.request.urlopen(request, timeout=60) as response:
            listing = json.load(response)
        require(listing.get('success') is True, 'R2 listing failed')
        keys.update(item['key'] for item in listing['result'])
        info = listing['result_info']
        if not info.get('is_truncated', False):
            break
        cursor = info.get('cursor')
        require(cursor and cursor not in cursors, 'R2 listing pagination incomplete')
        cursors.add(cursor)
    with tempfile.TemporaryDirectory(prefix='ferry-existing-', dir=os.environ['RUNNER_TEMP']) as directory:
        for group in ['packages', 'velopack-packages']:
            for path in (root / group).iterdir():
                name = path.name
                immutable = ((name.startswith(f'Ferry-{version}-') and name.endswith('.nupkg'))
                             or (name.startswith(f'ferry_{version}.') and name.endswith('.zip'))
                             or (name.startswith(f'ferry_{version}-') and name.endswith('.deb'))
                             or (name.startswith(f'ferry-{version}.') and name.endswith('.AppImage'))
                             or (name.startswith(f'ferry-{version}-') and name.endswith('.rpm')))
                if immutable and name in keys:
                    downloaded = Path(directory) / name
                    run('wrangler', 'r2', 'object', 'get', 'ferry-updates/' + name, '--file=' + str(downloaded), '--remote')
                    require(digest(downloaded) == digest(path), f'existing version has different bytes: {name}')
    print('既存の同版 version 付き配布物の不変条件を確認しました。')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['package', 'feed', 'aggregate', 'remote'])
    parser.add_argument('--version', required=True)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--runtime')
    parser.add_argument('--label')
    parser.add_argument('--publish', type=Path)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()
    if args.mode == 'remote':
        check_remote(args.root, args.version)
        return
    identity = {k: os.environ[k] for k in ['GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT']}
    identity.update(version=args.version, configuration='Release')
    if args.mode == 'aggregate':
        expected = {f'{mode}-{rid}' for mode, rids in [('package', ['win-x64', 'win-arm64', 'osx-arm64', 'linux-x64', 'linux-arm64']), ('feed', ['osx-arm64', 'linux-x64', 'linux-arm64'])] for rid in rids}
        reports = list(args.evidence.glob('*.json'))
        require({p.stem for p in reports} == expected, 'verification variant set mismatch')
        seen = {'packages': set(), 'velopack-packages': set()}
        for report in reports:
            proof = json.loads(report.read_text(encoding='utf-8'))
            require(proof['identity'] == identity, 'verification source/run/version mismatch')
            require(report.stem == proof['mode'] + '-' + proof['runtime'], 'verification variant identity mismatch')
            require(proof['dependency']['name'] == 'deps.' + proof['runtime'] + '.txt', 'dependency RID mismatch')
            require(len(proof['files']) > 0, 'empty verification report')
            dep = Path('dependencies') / proof['dependency']['name']
            require(digest(dep) == proof['dependency']['sha256'], 'dependency graph mismatch')
            group = 'packages' if proof['mode'] == 'package' else 'velopack-packages'
            for item in proof['files']:
                name = item['name']
                require(Path(name).name == name and name not in seen[group], 'duplicate/unsafe release file')
                actual = args.root / group / name
                require(actual.stat().st_size == item['size'] and digest(actual) == item['sha256'], f'upload bytes mismatch: {name}')
                seen[group].add(name)
        for group, names in seen.items():
            require({p.name for p in (args.root / group).iterdir() if p.is_file()} == names, f'unverified upload file: {group}')
        print('全 variant・source/run・依存・配布実物 SHA-256 を照合しました。')
        return
    props = metadata(Path('Directory.Build.props').read_bytes())
    require(props.get('Version') == args.version, 'source version mismatch')
    workspace = Path(os.environ['GITHUB_WORKSPACE'])
    require(workspace.is_absolute() and workspace.resolve() == Path.cwd().resolve(), 'checkout workspace mismatch')
    require((workspace / '.git').exists(), 'Git checkout missing')
    # 古い Git は safe.directory の -c 指定を認めない。子 Git 専用の一時 HOME に
    # 正式な global 設定を書き、この checkout の SHA 照会だけで使う。
    with tempfile.TemporaryDirectory(prefix='ferry-git-', dir=os.environ['RUNNER_TEMP']) as git_home:
        git_env = os.environ.copy()
        git_env.update(HOME=git_home, XDG_CONFIG_HOME=git_home,
                       GIT_CONFIG_GLOBAL=str(Path(git_home) / '.gitconfig'))
        run('git', 'config', '--global', '--add', 'safe.directory', workspace.resolve(),
            cwd=git_home, env=git_env)
        require(run('git', '-C', workspace.resolve(), 'rev-parse', 'HEAD',
                    env=git_env) == identity['GITHUB_SHA'], 'checkout SHA mismatch')
    require('dotnet publish src/Ferry/Ferry.csproj -c Release' in Path('.github/workflows/build.yml').read_text(encoding='utf-8'), 'Release build contract changed')
    executable = 'Ferry.exe' if args.runtime.startswith('win-') else 'Ferry' if args.runtime.startswith('osx-') else 'ferry'
    original = args.publish / executable
    check_binary(original, args.runtime)
    dependency = Path('dependencies') / f'deps.{args.runtime}.txt'
    require(dependency.stat().st_size > 0, 'dependency graph missing')
    # CI の一時領域を使い、各検査の展開先を他ジョブと分離する。
    with tempfile.TemporaryDirectory(prefix='ferry-verify-', dir=os.environ['RUNNER_TEMP']) as directory:
        work = Path(directory)
        if args.mode == 'feed':
            check_feed(args.root, args.runtime, args.version, original, work)
            files = list(args.root.iterdir())
            require(all(p.is_file() for p in files), 'unexpected feed directory')
        else:
            files = check_package(args.root, args.runtime, args.label, args.version, original, work)
    proof = {'identity': identity, 'mode': args.mode, 'runtime': args.runtime,
             'dependency': {'name': dependency.name, 'sha256': digest(dependency)},
             'files': [{'name': p.name, 'size': p.stat().st_size, 'sha256': digest(p)} for p in files]}
    args.evidence.mkdir(parents=True, exist_ok=True)
    (args.evidence / f'{args.mode}-{args.runtime}.json').write_text(json.dumps(proof, indent=2) + '\n', encoding='utf-8')
    print(f'{args.mode} {args.runtime}: 最終実物の検証に成功しました。')


if __name__ == '__main__':
    main()
