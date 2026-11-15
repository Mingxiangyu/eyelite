#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
打包：把 exe + 使用说明打成一个 zip，放在 dist/ 下。

为什么要有这个脚本而不是手工压缩：
  · 每次发版都要做一次，手工最容易漏文件、漏写版本号；
  · 包里必须带**指纹**（大小 + md5），否则出了问题无法确认对方跑的是哪一版；
  · 安装说明要跟着包走，不能只存在聊天记录里。

用法：
    python tools/make-release.py            # 用源码里的 AppVersion 命名
    python tools/make-release.py v1.4.0     # 覆盖版本号

产物：
    dist/EyeLite-<version>.zip
        EyeLite.exe
        README.md
        安装与使用.txt
        FINGERPRINT.txt
"""

import hashlib
import io
import os
import re
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, 'EyeLite.exe')
SRC = os.path.join(ROOT, 'src', 'Program.cs')
README = os.path.join(ROOT, 'README.md')
DIST = os.path.join(ROOT, 'dist')


def md5(path):
    h = hashlib.md5()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 16), b''):
            h.update(chunk)
    return h.hexdigest()


def version_from_source():
    try:
        s = io.open(SRC, encoding='utf-8').read()
        m = re.search(r'AppVersion\s*=\s*"([^"]+)"', s)
        return m.group(1) if m else 'v0'
    except Exception:
        return 'v0'


USAGE = """EyeLite 安装与使用
================================

一、怎么装
  1. 把 EyeLite.exe 放到一个**固定目录**（例如 D:\\Tools\\EyeLite\\）。
     不要放在桌面或下载文件夹 —— 以后清理下载会把程序一起删掉。
  2. 双击 EyeLite.exe。它没有主窗口，只会出现在**任务栏右下角托盘**。
  3. 如果需要开机自启：右键托盘图标 → 勾「开机自动启动」。

二、用到哪些系统资源
  · 配置写在 %APPDATA%\\EyeLite\\config.ini（删掉即恢复默认）
  · 开机自启只写 HKCU 的 Run 项，不写系统目录、不需要管理员权限
  · 严格模式会装一个键盘低级钩子（WH_KEYBOARD_LL），只用于休息期间拦截按键；
    个别杀毒软件可能提示"键盘记录器"，可以关掉严格模式，其余功能不受影响

三、卸载
  1. 右键托盘 → 退出
  2. 删除 EyeLite.exe 所在目录
  3. 删除 %APPDATA%\\EyeLite 目录
  4. 若开过自启：设置 → 应用 → 启动 里确认没有残留项

四、第一次用建议先改这两个数
  · 工作时长：默认 20 分钟（测试时可先设 1 分钟，确认流程）
  · 预告时长：默认 30 秒（横幅里可以「延后 / 跳过 / 立即休息」）

五、出问题怎么反馈
  记下三件事：时间点、当时前台是什么程序、现象（例如"晚了几秒"）。
  再附上 FINGERPRINT.txt 里的 md5 —— 否则无法确认你跑的是哪一版。
"""


def main():
    if not os.path.exists(EXE):
        print('FAIL: 找不到 EyeLite.exe，先编译')
        return 2

    ver = sys.argv[1] if len(sys.argv) > 1 else version_from_source()
    exe_size = os.path.getsize(EXE)
    exe_md5 = md5(EXE)
    src_md5 = md5(SRC) if os.path.exists(SRC) else '-'

    fingerprint = (
        'EyeLite %s\n'
        'EyeLite.exe  %d bytes  md5 %s\n'
        'Program.cs   md5 %s\n'
        '\n'
        '（反馈问题时请附上这两行，否则无法确认版本）\n'
        % (ver, exe_size, exe_md5, src_md5))

    if not os.path.isdir(DIST):
        os.makedirs(DIST)
    out = os.path.join(DIST, 'EyeLite-%s.zip' % ver)

    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        z.write(EXE, 'EyeLite.exe')
        if os.path.exists(README):
            z.write(README, 'README.md')
        z.writestr('安装与使用.txt', USAGE.replace('\n', '\r\n'))
        z.writestr('FINGERPRINT.txt', fingerprint.replace('\n', '\r\n'))

    print('已打包: %s' % out)
    print('  版本    %s' % ver)
    print('  exe     %d bytes / md5 %s' % (exe_size, exe_md5))
    print('  源码    md5 %s' % src_md5)
    print('  大小    %.1f KB' % (os.path.getsize(out) / 1024.0))
    return 0


if __name__ == '__main__':
    sys.exit(main())
