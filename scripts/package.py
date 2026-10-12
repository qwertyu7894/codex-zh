#!/usr/bin/env python3
"""把 dist/ 下的成品打成发布包。

用法：
    python scripts/package.py                       # 输出到 ..\\release
    python scripts/package.py --out D:\\somewhere

版本号单一来源：src/AppInfo.cs 里的 AppInfo.Version。
发布包目录/压缩包名自动带上版本，例如 GPT汉化-v1.1.0-Windows-x64.zip。
使用说明由 docs/使用说明.template.txt 按品牌 + 版本 + exe 名渲染而成。

两个反复踩到的 Windows 坑，这里都处理了：
  1. 中文文件名必须用 UTF-8 写 zip 条目 —— PowerShell 的 Compress-Archive
     会把非 ASCII 名字搞乱，所以用 Python 的 zipfile。
  2. 使用说明.txt 必须带 UTF-8 BOM，否则记事本按 ANSI 解码显示乱码。
"""

import argparse
import hashlib
import io
import os
import re
import shutil
import sys
import zipfile

# (品牌显示名, dist 里的 exe 文件名, 发布包目录前缀)
#
# ⚠️ exe 文件名与发布包名必须**纯 ASCII**。
# 客户端实测：含中文的 exe 名在对方机器上打不开，改成纯英文名就正常。
# 中文只保留在"显示名"里（窗口标题、程序属性），那是编译进 exe 内部的，
# 不经过文件系统，不受影响。
BUILDS = [
    ('Codex 汉化增强工具', 'CodexZh-Windows-x64.exe', 'CodexZh'),
    ('GPT汉化',            'GPTHanhua.exe',           'GPTHanhua'),
]

# 交付用的主品牌：用户日常直接从这个 exe 发给客户，所以打包后要同步一份过去
# （见 --handoff）。少这一步就会出现"源码已是新版、发出去的还是旧版"。
HANDOFF_EXE = 'GPTHanhua.exe'


def read_version(root):
    """版本号唯一来源：src/AppInfo.cs。避免 C# 和打包脚本各写一份。"""
    path = os.path.join(root, 'src', 'AppInfo.cs')
    text = io.open(path, encoding='utf-8-sig').read()
    m = re.search(r'Version\s*=\s*"([^"]+)"', text)
    if not m:
        raise SystemExit('在 %s 里找不到 AppInfo.Version' % path)
    return m.group(1)


def render_readme(root, brand, exename, version):
    tpl = os.path.join(root, 'docs', '使用说明.template.txt')
    if not os.path.exists(tpl):
        return None
    text = io.open(tpl, encoding='utf-8').read()
    text = text.replace('{{BRAND}}', brand)
    text = text.replace('{{VERSION}}', version)
    text = text.replace('{{EXE}}', exename)
    # 行尾统一成 CRLF，Windows 记事本才不会挤成一行
    return text.replace('\r\n', '\n').replace('\n', '\r\n')


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)

    ap = argparse.ArgumentParser()
    ap.add_argument('--dist', default=os.path.join(root, 'dist'))
    ap.add_argument('--out', default=os.path.join(os.path.dirname(root), 'release'))
    ap.add_argument('--handoff', default=None,
                    help='打包后把 %s 额外复制到这个目录（交付用，例如音乐文件夹）'
                         % HANDOFF_EXE)
    args = ap.parse_args()

    if not os.path.isdir(args.dist):
        print('找不到 dist 目录：' + args.dist, file=sys.stderr)
        return 1

    version = read_version(root)

    # 对账：dist/buildinfo.json 是 build.ps1 成功编译后才写的。
    # 若版本对不上，说明 dist 里还是上一次的旧产物（编译失败时最容易发生），
    # 打出来的包会"名字是 vX 内容却是 vY"。宁可拒绝打包也不要发出名不副实的包。
    import json
    stamp_path = os.path.join(args.dist, 'buildinfo.json')
    if not os.path.exists(stamp_path):
        print('拒绝打包：找不到 %s\n'
              '  dist 里没有构建戳，说明还没成功构建过。先跑 build.ps1。'
              % stamp_path, file=sys.stderr)
        return 1
    stamp = json.load(io.open(stamp_path, encoding='utf-8'))
    if stamp.get('version') != version:
        print('拒绝打包：版本对不上\n'
              '  源码 AppInfo.Version = %s\n'
              '  dist 构建戳 version  = %s\n'
              '  —— dist 里是上一次的旧产物，请先重新构建。'
              % (version, stamp.get('version')), file=sys.stderr)
        return 1

    print('版本号对账通过：%s（构建于 %s）\n' % (version, stamp.get('builtAt', '?')))

    os.makedirs(args.out, exist_ok=True)
    built = 0

    for brand, exename, prefix in BUILDS:
        src = os.path.join(args.dist, exename)
        if not os.path.exists(src):
            print('跳过（dist 里没有 %s）' % exename)
            continue

        folder = '%s-v%s-Windows-x64' % (prefix, version)
        d = os.path.join(args.out, folder)
        os.makedirs(d, exist_ok=True)
        shutil.copy2(src, os.path.join(d, exename))

        readme = render_readme(root, brand, exename, version)
        if readme is None:
            print('  ! 缺少 docs/使用说明.template.txt，发布包里不会有说明书')
        else:
            # 带 BOM，记事本才能正确显示中文
            with io.open(os.path.join(d, '使用说明.txt'), 'w',
                         encoding='utf-8-sig', newline='') as f:
                f.write(readme)

        digest = hashlib.sha256(open(src, 'rb').read()).hexdigest().upper()
        with io.open(os.path.join(d, 'SHA256SUMS.txt'), 'w',
                     encoding='utf-8-sig', newline='') as f:
            f.write(digest + '  ' + exename + '\r\n')

        # MIT 第 2 条：分发二进制时必须随附版权声明与许可文本。
        # 少了这个文件，客户端拿到的就是"无证分发"。
        license_src = os.path.join(root, 'LICENSE')
        if os.path.exists(license_src):
            shutil.copy2(license_src, os.path.join(d, 'LICENSE.txt'))
        else:
            print('  ! 缺少 LICENSE —— 二进制分发会违反 MIT 第 2 条')

        zpath = os.path.join(args.out, folder + '.zip')
        if os.path.exists(zpath):
            os.remove(zpath)
        with zipfile.ZipFile(zpath, 'w', zipfile.ZIP_DEFLATED) as z:
            for name in [exename, '使用说明.txt', 'LICENSE.txt', 'SHA256SUMS.txt']:
                p = os.path.join(d, name)
                if os.path.exists(p):
                    z.write(p, folder + '/' + name)

        print('%-38s %8d 字节  %s  SHA256=%s...'
              % (folder + '.zip', os.path.getsize(zpath), brand, digest[:20]))
        built += 1

    # 交付副本：用户直接从音乐文件夹把单个 exe 发给客户，
    # 所以这里必须跟着一起更新，否则源码是新版、发出去的还是旧版。
    if args.handoff:
        src = os.path.join(args.dist, HANDOFF_EXE)
        if os.path.exists(src):
            os.makedirs(args.handoff, exist_ok=True)
            dst = os.path.join(args.handoff, HANDOFF_EXE)
            shutil.copy2(src, dst)
            print('\n已同步交付副本 -> %s' % dst)
        else:
            print('\n! 交付副本未同步：dist 里没有 %s' % HANDOFF_EXE)

    print('\n共打包 %d 个发布包 -> %s' % (built, args.out))
    return 0 if built else 1


if __name__ == '__main__':
    sys.exit(main())
