#!/usr/bin/env python3
"""把 dist/ 下的成品打成发布包。

用法：
    python scripts/package.py                       # 输出到 ..\\release
    python scripts/package.py --out D:\\somewhere

每个发布包含三样：
    成品 exe + 使用说明.txt + SHA256SUMS.txt

注意两个 Windows 上的坑，这里都踩过：
  1. 中文文件名必须用 UTF-8 写 zip 条目 —— PowerShell 的 Compress-Archive
     会把非 ASCII 名字搞乱，所以用 Python 的 zipfile。
  2. 使用说明.txt 必须带 UTF-8 BOM，否则记事本按 ANSI 解码显示乱码。
"""

import argparse
import hashlib
import os
import shutil
import sys
import zipfile

# (品牌显示名, dist 里的 exe 文件名, 发布包目录名)
BUILDS = [
    ('Codex 汉化增强工具', 'CodexZh-Windows-x64.exe', 'CodexZh-v1.0.0-Windows-x64'),
    ('GPT汉化',            'GPT汉化.exe',             'GPT汉化-v1.0.0-Windows-x64'),
]


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

    os.makedirs(args.out, exist_ok=True)
    built = 0

    for brand, exename, folder in BUILDS:
        src = os.path.join(args.dist, exename)
        if not os.path.exists(src):
            print('跳过（dist 里没有 %s）' % exename)
            continue

        d = os.path.join(args.out, folder)
        os.makedirs(d, exist_ok=True)
        shutil.copy2(src, os.path.join(d, exename))

        # 使用说明：补 BOM
        txt = os.path.join(d, '使用说明.txt')
        if os.path.exists(txt):
            raw = open(txt, 'rb').read()
            if not raw.startswith(b'\xef\xbb\xbf'):
                open(txt, 'wb').write(b'\xef\xbb\xbf' + raw)
        else:
            print('  ! 缺少 %s（发布包里会没有说明书）' % txt)

        digest = hashlib.sha256(open(src, 'rb').read()).hexdigest().upper()
        with open(os.path.join(d, 'SHA256SUMS.txt'), 'w', encoding='utf-8-sig', newline='') as f:
            f.write(digest + '  ' + exename + '\n')

        zpath = os.path.join(args.out, folder + '.zip')
        if os.path.exists(zpath):
            os.remove(zpath)
        with zipfile.ZipFile(zpath, 'w', zipfile.ZIP_DEFLATED) as z:
            for name in [exename, '使用说明.txt', 'SHA256SUMS.txt']:
                p = os.path.join(d, name)
                if os.path.exists(p):
                    z.write(p, folder + '/' + name)

        print('%-34s %8d 字节   %s   SHA256=%s...'
              % (folder + '.zip', os.path.getsize(zpath), brand, digest[:24]))
        built += 1

    print('\n共打包 %d 个发布包 -> %s' % (built, args.out))
    return 0 if built else 1


if __name__ == '__main__':
    sys.exit(main())
