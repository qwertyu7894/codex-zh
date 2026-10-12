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
    args = ap.parse_args()

    if not os.path.isdir(args.dist):
        print('找不到 dist 目录：' + args.dist, file=sys.stderr)
        return 1

    version = read_version(root)
    print('版本号（来自 src/AppInfo.cs）：%s\n' % version)

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

    print('\n共打包 %d 个发布包 -> %s' % (built, args.out))
    return 0 if built else 1


if __name__ == '__main__':
    sys.exit(main())
