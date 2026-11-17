#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
取数前的"产物来源"体检：四条前置一次跑完，输出 PASS/FAIL，不启动被测进程。

## 为什么需要它
今天反复栽在同一件事上：**测的产物和以为的产物不是一个**。
- `git status` 干净 ≠ exe 是新的（exe 可能早于 src，SE 2026-11-17 提醒并实测成立）；
- 两个产物 `EyeLite.exe` / `EyeLite-dev.exe` 是**不同时间**编的（差 8h45m），
  所以"exe mtime > src mtime"必须针对**你实际要跑的那个文件**判，不能拿另一个的上；
- `git show HEAD:src | md5sum` 给的是仓库 blob（LF），工作区文件可能因
  `core.autocrlf` 变成 CRLF → **直接 md5sum 会假报"源码不一致"**。

## 四条前置
  1. `git status --porcelain -- src/Program.cs` 为空
  2. 工作区 `src/Program.cs` == `HEAD:src/Program.cs`（**先做 CRLF 规范化再比**，同时打印原始两个哈希）
  3. **被测 exe 的 mtime > src 的 mtime**（按 `--exe` 指定的文件，不套用另一个）
  4. 打印被测 exe 的 `size + md5 + mtime`，以及 HEAD commit —— 供报告里引用

## 用法（**推荐把"取数"和"判定"放进同一条命令**，避免跨时刻引用）

    stat -c '%n size=%s mtime=%y' src/Program.cs EyeLite.exe EyeLite-dev.exe &&     md5sum src/Program.cs EyeLite.exe EyeLite-dev.exe &&     python tools/precheck-provenance.py --exe EyeLite.exe --expect-size <N>         --needle "<新文案>" --absent "<旧文案>"

> 为什么要一条命令：2026-11-17 一天内出现多次"引用了上一刻的指纹"（源码在这几分钟里
> 被反复修改），人肉分两三条命令取数就会漂。一条命令里的数字与判定必然来自同一时刻。

    python tools/precheck-provenance.py                      # 默认检查 EyeLite.exe
    python tools/precheck-provenance.py --exe EyeLite-dev.exe
    python tools/precheck-provenance.py --needle 正在输入，稍后提醒
        # 可选：检查产物二进制里是否含某个字符串（比 md5 更硬，能证明某处改动在产物里）
    python tools/precheck-provenance.py --needle 严格模式：锁键盘，Esc/空格退出                                        --absent 严格模式：休息期间锁住键盘
        # **成对使用**才有判别力：前者证明"新版本在"，后者证明"旧版本不在"

> **只能对 exe 判，绝不能对源码判**（SE 2026-11-17 提醒）：
> 被替换掉的旧文案往往**仍留在源码注释里**（如 `v1.6.0`、"休息期间锁住键盘"），
> 注释不会编进 PE —— 对 exe 做 `--absent` 有效，对 `src/Program.cs` 做会**假阳性**，
> 判定"修复没进去"。本脚本因此只读 `--exe` 指定的文件，并会拒绝非 PE 文件。
>
> 实现要点（踩过）：.NET 的用户字符串在二进制里是 **UTF-16LE**，
> 必须 `s.encode('utf-16-le') in data`。**不要用 `grep '中文'` 搜二进制** ——
> 那是按 UTF-8 字节比，永远不命中，会得到"缺失"的假结论（假阴性）。

退出码：全部通过 0；任一不通过 1（此时**不要取数**）。
"""

import argparse
import datetime
import hashlib
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_REL = 'src/Program.cs'


def run(cmd):
    return subprocess.run(cmd, cwd=ROOT, capture_output=True)


def md5(b):
    return hashlib.md5(b).hexdigest()


def fmt(ts):
    return datetime.datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--exe', default='EyeLite.exe')
    ap.add_argument('--needle', action='append', default=[],
                    help='可选（可多次）：该字符串**必须存在**。注意按 UTF-16LE 搜。')
    ap.add_argument('--expect-md5', default=None,
                    help='可选：期望的 md5（lead 给的那组）。不符即 FAIL 并 exit 1，'
                         '把"人眼比"变成"机器判"')
    ap.add_argument('--expect-size', type=int, default=None,
                    help='可选：期望的字节数（与 --expect-md5 一起用更稳）')
    ap.add_argument('--emit-handoff', action='store_true',
                    help='输出一段可直接贴给别人的"交接单"（全部为**当场读盘**的值）+ '
                         '可复验的命令行。用于替代人肉传数字 —— '
                         '2026-11-17 一天内出现三次"引用旧指纹"，都是人肉传的。')
    ap.add_argument('--baseline-mode', action='store_true',
                    help='基线模式：用于**已知的修复前产物**。只判定 5)/6)（needle/absent），'
                         '1)~4) 降为 [info] 且不影响退出码 —— 否则基线产物永远红，'
                         '读报告的人会误以为基线坏了。退出码只反映 5/6。')
    ap.add_argument('--absent', action='append', default=[],
                    help='可选（可多次）：该字符串**必须不存在**。'
                         '与 --needle 成对使用才有判别力：'
                         '单查"含新文案"只证明"不是太老的版本"，'
                         '配上"缺失旧文案"才证明"这一版修复在产物里"')
    args = ap.parse_args()

    exe = os.path.join(ROOT, args.exe)
    src = os.path.join(ROOT, SRC_REL)
    ok = True

    print('=== 产物来源体检 %s ===' % datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S'))

    # --- HEAD 信息
    head = run(['git', 'log', '-1', '--format=%h %cI %s']).stdout.decode('utf-8', 'ignore').strip()
    print('HEAD: %s' % head)

    # --- 前置 1：**只**判 src/Program.cs 有没有未提交改动
    # 注意：不要改成"git status 必须全局干净" —— tools/ 与 docs/ 长期有未提交改动
    # （QA 的 settings-ui-check.py、我的 p3-* 脚本等），那样会永远失败。
    # 我们要回答的问题是"这个 exe 是不是由这份 src 编出来的"，与 tools/ 无关。
    _tag = '[info]' if args.baseline_mode else '[FAIL]'
    st = run(['git', 'status', '--porcelain', '--', SRC_REL]).stdout.decode('utf-8', 'ignore').strip()
    if st:
        print('%s 1) src 有未提交改动：%s%s'
              % (_tag, st, '（基线模式：仅信息）' if args.baseline_mode else ''))
        if not args.baseline_mode:
            ok = False
    else:
        print('[ ok ] 1) src 无未提交改动')

    # --- 前置 2：工作区 src == HEAD blob（CRLF 规范化后比）
    disk = open(src, 'rb').read()
    src_lines = disk.count(bytes([10])) + (0 if disk.endswith(bytes([10])) else 1)
    blob = run(['git', 'show', 'HEAD:%s' % SRC_REL]).stdout
    disk_norm = disk.replace(b'\r\n', b'\n')
    has_crlf = b'\r\n' in disk
    same_raw = md5(disk) == md5(blob)
    same_norm = md5(disk_norm) == md5(blob)
    print('        src 磁盘 %d 字节 md5=%s%s'
          % (len(disk), md5(disk), '  (CRLF)' if has_crlf else '  (LF)'))
    print('        HEAD blob %d 字节 md5=%s' % (len(blob), md5(blob)))
    if same_raw:
        print('[ ok ] 2) 工作区 src == HEAD（原始字节一致）')
    elif same_norm:
        print('[ ok ] 2) 工作区 src == HEAD（**仅行尾不同**：磁盘 CRLF / blob LF，'
              '规范化后一致 —— 不是源码不一致，别误判）')
    else:
        print('%s 2) 工作区 src 与 HEAD 不一致（规范化后仍不同）→ 源码真的改过%s'
              % ('[info]' if args.baseline_mode else '[FAIL]',
                 '（基线模式：仅信息）' if args.baseline_mode else ''))
        if not args.baseline_mode:
            ok = False

    # --- 前置 3：被测 exe 的 mtime > src 的 mtime
    if not os.path.exists(exe):
        print('[FAIL] 3) 找不到被测产物：%s' % args.exe)
        return 1
    # 硬保护：本脚本只应针对**编译产物**判。若有人把 src/Program.cs 当 --exe 传进来，
    # needle/absent 会因为"旧文案还在注释里"而得出完全相反的结论。
    head4 = open(exe, 'rb').read(2)
    is_pe = head4 == b'MZ'
    if is_pe:
        blob_pe = open(exe, 'rb').read()
        off = int.from_bytes(blob_pe[0x3C:0x40], 'little') if len(blob_pe) > 0x40 else 0
        is_pe = blob_pe[off:off + 4] == b'PE\x00\x00'
    if not is_pe:
        print('[FAIL] 3) %s 不是 PE 可执行文件 → 只能对 exe 做来源判定；'
              '对源码判 needle/absent 会假阳性（旧文案可能留在注释里）' % args.exe)
        return 1

    t_src = os.path.getmtime(src)
    t_exe = os.path.getmtime(exe)
    if t_exe > t_src:
        print('[ ok ] 3) %s 晚于 src（exe %s > src %s）'
              % (args.exe, fmt(t_exe), fmt(t_src)))
    else:
        msg = ('3) %s **早于** src（exe %s <= src %s）→ 产物不含当前源码'
               % (args.exe, fmt(t_exe), fmt(t_src)))
        if args.baseline_mode:
            print('[info] %s（基线模式：这是**预期**——基线产物本来就早于当前 src）' % msg)
        else:
            print('[FAIL] %s，不要取数' % msg)
            ok = False

    # --- 前置 4：指纹（供报告引用）
    data = open(exe, 'rb').read()
    got_md5, got_size = md5(data), len(data)
    print('[info] 4) 被测产物 %s  size=%d  md5=%s  mtime=%s'
          % (args.exe, got_size, got_md5, fmt(t_exe)))
    if args.expect_md5 or args.expect_size:
        bad = []
        if args.expect_md5 and args.expect_md5.lower() != got_md5:
            bad.append('md5 期望 %s / 实际 %s' % (args.expect_md5, got_md5))
        if args.expect_size and args.expect_size != got_size:
            bad.append('size 期望 %d / 实际 %d' % (args.expect_size, got_size))
        if bad:
            print('%s 4) 与预期不符：%s%s'
                  % ('[info]' if args.baseline_mode else '[FAIL]', '；'.join(bad),
                     '（基线模式：仅信息）' if args.baseline_mode else ''))
            if not args.baseline_mode:
                ok = False
        else:
            print('[ ok ] 4) 与预期一致（%s）' % ('md5' if args.expect_md5 else 'size'))
    # 另一个产物也打出来，避免"拿另一个的时间戳/哈希套过来"
    other = 'EyeLite-dev.exe' if args.exe.lower() == 'eyelite.exe' else 'EyeLite.exe'
    op = os.path.join(ROOT, other)
    if os.path.exists(op):
        od = open(op, 'rb').read()
        print('        （对照）%s size=%d md5=%s mtime=%s —— 与上面**不是同一个文件**，'
              '报告里别混用' % (other, len(od), md5(od), fmt(os.path.getmtime(op))))

    # --- 可选：二进制里是否含某个新文案（比 md5 硬，能证明"这处改动在产物里"）
    # 注意：--needle 可传多次，**每一个都要判**（早期版本只取最后一个，静默漏判过）
    for want in args.needle:
        pat = want.encode('utf-16-le')
        hit = pat in data
        print('[%s] 被测产物含 "%s" = %s%s'
              % (' ok ' if hit else 'FAIL', want, hit,
                 '' if hit else ' → 这处改动不在产物里，别用它验修复'))
        if not hit:
            ok = False
        # 另一个产物也查一次：两个 exe 是不同时间编的，各自含不含这个字符串
        # 是判断"它属于哪个版本"的独立证据（比 mtime 更硬）。
        if os.path.exists(op):
            print('[info] 对照产物 %s 含该文案 = %s（仅信息，不参与判定）'
                  % (other, pat in od))

    # --- 可选：--absent（必须不存在）。与 --needle 成对才有判别力。
    for dead in args.absent:
        pat = dead.encode('utf-16-le')
        found = pat in data
        print('[%s] 被测产物**不含** "%s" = %s%s'
              % ('FAIL' if found else ' ok ', dead, not found,
                 ' → 旧文案还在，说明这个修复不在产物里' if found else ''))
        if found:
            ok = False
        if os.path.exists(op) and pat in od:
            print('[info] 对照产物 %s 仍含该旧文案（仅信息）' % other)

    print('--- 判据强度分级（别把绿读成"修好了"）---')
    print('    1)~4) 只证明「产物与源码对应」：这个 exe 是这份源码编的，')
    print('          **不证明源码改对了**，也不覆盖"无字符串可判"的改动（布局/颜色/尺寸/hover）。')
    if args.needle or args.absent:
        print('    5)~6) 只证明「这几处文案对应的改动在产物里」，')
        print('          每条改动必须用自己的字符串对；**未被给字符串的改动不在覆盖范围内**。')
    print('    因此：本脚本全绿 ≠ 所有修复生效。无字符串的改动请用行为判据'
          '（如 QA 的 settings-ui-check.py 的 B/C 两条）单独验。')

    # 【固定免责声明】给三个月后翻记录的人看：门禁覆盖不到布局/尺寸/颜色/事件类改动。
    # 实例（2026-11-17）：某版产物 needle/absent 全绿，但设置页输入框仍上下贴死 ——
    # 只看门禁会误以为"改完了"。
    print('[提示] 本门禁只覆盖"文案/版本号"类改动。')
    print('       本轮产物含 5 批改动：4 批布局/颜色（无字符串可判）+ 1 批文案（3 组字符串对）。')
    print('       门禁 5)6) 只覆盖文案；布局/间距/颜色的验收归属为：')
    print('         · lead 的像素取样 —— 只能判"深浅方向"，判不了"间距是否 4px"')
    print('         · tools/settings-ui-check.py —— 几何/交互判据，能判越界与点击后取值')
    print('       本门禁对这些**不提供任何证据**。')

    if args.emit_handoff:
        print('')
        print('---8<--- HANDOFF（以下全部为当场读盘值，可直接贴）---')
        print('generated: %s' % datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S'))
        print('head:      %s' % head)
        print('src:       size=%d  lines=%d  md5=%s  mtime=%s  eol=%s'
              % (len(disk), src_lines, md5(disk), fmt(os.path.getmtime(src)),
                 'CRLF' if has_crlf else 'LF'))
        print('exe:       %s  size=%d  md5=%s  mtime=%s'
              % (args.exe, got_size, got_md5, fmt(t_exe)))
        if os.path.exists(op):
            print('other:     %s  size=%d  md5=%s  mtime=%s  （不同文件，别混用）'
                  % (other, len(od), md5(od), fmt(os.path.getmtime(op))))
        print('checks:    1=%s 2=%s 3=%s 4=%s 5=%s 6=%s'
              % ('pass' if not st else 'FAIL',
                 'pass' if (same_raw or same_norm) else 'FAIL',
                 'pass' if t_exe > t_src else 'FAIL',
                 'pass' if not (args.expect_md5 or args.expect_size) or not bad else 'FAIL',
                 str(all(x.encode('utf-16-le') in data for x in args.needle)),
                 str(not any(x.encode('utf-16-le') in data for x in args.absent))))
        if args.needle or args.absent:
            print('needle:    %s' % args.needle)
            print('absent:    %s' % args.absent)
        cmd = ('python tools/precheck-provenance.py --exe %s' % args.exe)
        if args.expect_md5:
            cmd += ' --expect-md5 %s' % args.expect_md5
        if args.expect_size:
            cmd += ' --expect-size %d' % args.expect_size
        for x in args.needle:
            cmd += ' --needle "%s"' % x
        for x in args.absent:
            cmd += ' --absent "%s"' % x
        if args.baseline_mode:
            cmd += ' --baseline-mode'
        print('reverify:  %s' % cmd)
        print('note:      本交接单由命令生成；若 src/exe 在上表 mtime 之后再次变更，')
        print('          本单一切数字作废，请重新生成本单。')
        print('---8<--- END HANDOFF ---')

    if args.baseline_mode:
        print('=== 结论（基线模式）：%s ==='
              % ('文案判定通过 —— 该产物与"修复前"特征一致' if ok
                 else '**文案判定不通过 —— 该产物不符合"修复前"特征**'))
        print('    注意：基线模式**不代表"可以取数"**，它只验证"这个产物属于哪个版本"。')
    else:
        print('=== 结论：%s ===' % ('全部通过，可以取数' if ok else '**不通过，不要取数**'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
