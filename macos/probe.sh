#!/bin/bash
# Codex 中文化工具 —— macOS 只读诊断
#
#   ⚠️ 本脚本只读，不修改任何文件、不需要 sudo。
#   目的是先拿到在 Mac 上实现补丁所必需的事实，避免靠猜写代码。
#
# 用法：
#   chmod +x probe.sh && ./probe.sh
#
# 把完整输出贴回来即可。

set -u
export LC_ALL=C

say() { printf '\n===== %s =====\n' "$1"; }

# ---------- 1. 定位 App ----------
say "1. 定位 ChatGPT / Codex App"
APP=""
for c in "/Applications/ChatGPT.app" "/Applications/Codex.app" \
         "$HOME/Applications/ChatGPT.app" "$HOME/Applications/Codex.app"; do
  if [ -d "$c" ]; then APP="$c"; break; fi
done
if [ -z "$APP" ]; then
  APP=$(mdfind -name "ChatGPT.app" 2>/dev/null | head -1)
fi
if [ -z "$APP" ]; then
  APP=$(mdfind -name "Codex.app" 2>/dev/null | head -1)
fi

if [ -z "$APP" ] || [ ! -d "$APP" ]; then
  echo "未找到 App。请手动编辑本脚本顶部的路径。"
  echo "（也可以跑：ls -d /Applications/*.app | grep -iE 'chatgpt|codex'）"
  exit 1
fi
echo "APP = $APP"

# ---------- 2. 版本与基本信息 ----------
say "2. 版本 / 架构 / 签名"
PLIST="$APP/Contents/Info.plist"
echo "CFBundleShortVersionString = $(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$PLIST" 2>/dev/null || echo '读取失败')"
echo "CFBundleIdentifier         = $(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$PLIST" 2>/dev/null || echo '读取失败')"
echo "主程序架构                 = $(file -b "$APP/Contents/MacOS/"* 2>/dev/null | head -1)"
echo "系统                       = $(sw_vers -productVersion)  /  $(uname -m)"
echo
echo "--- codesign 状态 ---"
codesign -dv --verbose=2 "$APP" 2>&1 | head -12

# ---------- 3. asar ----------
say "3. app.asar"
ASAR="$APP/Contents/Resources/app.asar"
if [ ! -f "$ASAR" ]; then
  echo "未找到 $ASAR"
  echo "Resources 目录内容："
  ls -la "$APP/Contents/Resources" 2>/dev/null | head -20
  exit 1
fi
echo "路径 = $ASAR"
echo "大小 = $(stat -f %z "$ASAR") 字节"

# asar 头部：u1 在偏移 4（头部 pickle 总长），u3 在偏移 12（JSON 字节数）
u1=$(od -An -tu4 -j 4  -N 4 "$ASAR" | tr -d ' ')
u3=$(od -An -tu4 -j 12 -N 4 "$ASAR" | tr -d ' ')
echo "头部字段 u1=$u1  u3=$u3   （数据区起点应为 8+u1 = $((8 + u1))）"

# ---------- 4. 关键补丁点 ----------
say "4. 关键补丁点是否可命中"
PAT='`enable_i18n`,!1'
HIT=$(LC_ALL=C grep -abo -F "$PAT" "$ASAR" 2>/dev/null | head -3)
if [ -z "$HIT" ]; then
  echo "未命中 '${PAT}' —— 需要人工确认这一版的写法。"
  echo "下面列出所有 enable_i18n 出现处（带偏移）："
  LC_ALL=C grep -abo -F 'enable_i18n' "$ASAR" 2>/dev/null | head -5
else
  echo "命中 '${PAT}'："
  echo "$HIT"
fi
echo
echo "已打补丁的写法 '${PAT/!1/!0}' 出现次数："
LC_ALL=C grep -abo -F '`enable_i18n`,!0' "$ASAR" 2>/dev/null | wc -l | tr -d ' '

# ---------- 5. Info.plist 里的 asar 完整性哈希 ----------
say "5. Info.plist: ElectronAsarIntegrity（这是改完 asar 必须同步的地方）"
PB=/usr/libexec/PlistBuddy
if $PB -c 'Print :ElectronAsarIntegrity' "$PLIST" >/dev/null 2>&1; then
  echo "--- 原始 plist 片段 ---"
  $PB -c 'Print :ElectronAsarIntegrity' "$PLIST" 2>&1 | head -20
  echo
  echo "--- 当前 asar 头部实际的 SHA-256 ---"
  if [ "$u3" -gt 0 ] 2>/dev/null; then
    ACTUAL=$(dd if="$ASAR" bs=1 skip=16 count=$u3 2>/dev/null | shasum -a 256 | cut -d' ' -f1)
    echo "SHA256(头部 JSON 区 [16, 16+u3)) = $ACTUAL"
    echo "（若与上面的 hash 不一致，说明算法假设有误，请把两者都贴回来）"
  fi
else
  echo "Info.plist 中没有 ElectronAsarIntegrity —— 该构建可能未开启 asar 完整性校验。"
fi

# ---------- 6. Electron fuse ----------
say "6. Electron fuse（决定 --inspect 还能不能用）"
FW="$APP/Contents/Frameworks/Electron Framework.framework/Versions/A/Electron Framework"
if [ ! -f "$FW" ]; then
  FW=$(find "$APP/Contents/Frameworks" -maxdepth 1 -name "*.framework" -type d 2>/dev/null | head -1)
  FW="$FW/Versions/A/$(basename "$FW" .framework)"
fi
if [ -f "$FW" ]; then
  echo "框架二进制 = $FW"
  OFF=$(LC_ALL=C grep -abo -F 'dL7pKGdnNz796PbbjQWNKmHXBZaB9tsX' "$FW" 2>/dev/null | head -1 | cut -d: -f1)
  if [ -n "$OFF" ]; then
    echo "哨兵偏移 = $OFF"
    echo -n "wire 前 12 字节 = "
    dd if="$FW" bs=1 skip=$((OFF + 32)) count=12 2>/dev/null | od -An -c | tr -s ' ' | head -2
    echo "（第 1 字节=版本，第 2 字节=数量，其后即 0/1 各位）"
  else
    echo "未找到 fuse 哨兵（可能被剥离，此时 Electron 用默认值：--inspect 可用）"
  fi
else
  echo "未找到 Electron Framework 二进制"
fi

# ---------- 7. 本地化副本现状 ----------
say "7. 已有的本地化副本 / 其他汉化工具痕迹"
for d in "$HOME/.codex/codex-zh" "$HOME/.codex/codex-localized" "$HOME/.codex/zh-cn-patched"; do
  if [ -e "$d" ]; then
    echo "存在: $d"
    ls -la "$d" 2>/dev/null | head -8
  fi
done
echo "(以上为空则说明这台机器还没有本地化副本)"

say "完成"
echo "请把从 '1. 定位' 到这里的完整输出贴回来。"
