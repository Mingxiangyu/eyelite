#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
解析 EyeLite 的 TRACE 文件，把"到点晚"这件事拆成可判读的两半：**丢 tick** 还是 **路径阻塞**。

## 行格式（源码 Program.cs，前缀**大小写敏感**，不要 lower() 归一化）
    T <nowMs>                              大写 T，每个 1s tick 一行
    P <nowMs> <sec>                        大写 P，预告期"显示秒数变化"打点（需 TRACE_UI）
    end\t<ISO>\t<durMs>                    小写 end + \t
    <ISO>\t<dueMs>\t<fireMs>\t<lateMs>      BEGIN 行**没有前缀**：恰好 4 个 tab 字段且首字段是 ISO

> 为什么强调大小写：`end` 与小写 `t`/`p` 只差大小写，`line.lower()` 会把它们混成一类，
> 将来再加 `E` 也会撞 `end`。识别 BEGIN 也**不能只数 tab 个数**，要"4 字段 + 首字段 ISO"。

## 三条解析硬规则（lead 2026-11-16 定）
1. 前缀按原样大小写匹配
2. BEGIN 用"恰好 4 个 tab 字段 + 首字段匹配 ISO 正则"识别
3. **未知行首必须计数并报出**（举例），不许静默忽略 —— 否则将来加一个打点会被当成"没数据"
另加：`T` 的 ms 若比上一条小 → **时间戳回退告警**，不能当负间隔算进统计

## 分段
`p0a` 每个轮次是一个新进程，`T` 用系统级单调时钟（跨轮连续、不会归零），
所以按 **`end` 行**切段：遇到 `end` 后的第一条 `T` 就是下一轮。

## 用法
    python tools/trace-interval-check.py tools/_probe/p0a-trace.log
    python tools/trace-interval-check.py <trace> --big 1500     # 自定义大间隔阈值
"""

import os
import re
import sys

ISO_RE = re.compile(r'^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}$')


def parse(path):
    """严格按三条规则解析。返回 (tick段列表, begins, ends, previews, warns)。"""
    segs = []
    cur = []
    begins, ends, previews = [], [], []
    rest_ticks = []
    warns = {'unknown': [], 'bad_fields': [], 'regress': []}

    with open(path, 'r', encoding='utf-8', errors='ignore') as f:
        for lineno, raw in enumerate(f, 1):
            line = raw.rstrip('\r\n')
            if not line:
                continue

            # 规则 1：前缀大小写敏感，按原样匹配
            if line.startswith('T '):
                parts = line.split()
                if len(parts) != 2:
                    warns['bad_fields'].append((lineno, line))
                    continue
                try:
                    ms = int(parts[1])
                except ValueError:
                    warns['bad_fields'].append((lineno, line))
                    continue
                if cur and ms < cur[-1]:
                    warns['regress'].append((lineno, cur[-1], ms))
                cur.append(ms)
                continue

            if line.startswith('P '):
                parts = line.split()
                if len(parts) != 3:
                    warns['bad_fields'].append((lineno, line))
                    continue
                try:
                    previews.append((int(parts[1]), int(parts[2])))
                except ValueError:
                    warns['bad_fields'].append((lineno, line))
                continue

            if line.startswith('end\t'):
                cols = line.split('\t')
                if len(cols) != 3:
                    warns['bad_fields'].append((lineno, line))
                    continue
                ends.append(cols)
                if cur:
                    segs.append(cur)
                    cur = []
                continue

            # 规则 2：BEGIN = 恰好 4 个 tab 字段 且 首字段是 ISO
            cols = line.split('\t')
            if len(cols) == 4 and ISO_RE.match(cols[0]):
                try:
                    begins.append({'iso': cols[0], 'due': int(cols[1]),
                                   'fire': int(cols[2]), 'late': int(cols[3])})
                except ValueError:
                    warns['bad_fields'].append((lineno, line))
                continue

            # 休息期倒计时：裸整数（无前缀）。实测自然坐满最后一条是 1（不是 0）。
            if line.isdigit():
                rest_ticks.append(int(line))
                continue

            # 规则 3：未知行首 → 计数，不静默
            warns['unknown'].append((lineno, line))

    if cur:
        segs.append(cur)
    return segs, begins, ends, previews, warns, rest_ticks


def classify_late(late_ms, seg_gaps):
    """按 lead 2026-11-16 的三档判读，把"到点晚"归因。

    lateMs ∈ [4500,5500] 且 T 无空档 → 疑似让路（C4 一次让路就是 5 秒；C3 一次 60 秒）
    lateMs 其它大值 + T 有空档       → 丢 tick 或 UI 线程阻塞
    lateMs < 300 且 T 无空档         → 正常
    （整倍数 5000/10000/15000 尤其要想到让路，不是阻塞）
    """
    has_gap = any(g >= 2000 for g in seg_gaps)
    if late_ms is None:
        return '-'
    if late_ms < 300 and not has_gap:
        return '正常'
    if 4500 <= late_ms <= 5500 and not has_gap:
        return '疑似让路(C3/C4)'
    if has_gap:
        return '丢tick/阻塞'
    return '需人工判读'


def gaps_of(seq):
    return [seq[i] - seq[i - 1] for i in range(1, len(seq))]


def buckets(gaps):
    b = {}
    for g in gaps:
        k = (g // 500) * 500
        b[k] = b.get(k, 0) + 1
    return ', '.join('%d-%dms:%d' % (k, k + 499, b[k]) for k in sorted(b))


def main():
    if len(sys.argv) < 2:
        print('usage: trace-interval-check.py <trace-file> [--big 1500]')
        return 2
    path = sys.argv[1]
    big_thr = 1500
    if '--big' in sys.argv:
        try:
            big_thr = int(sys.argv[sys.argv.index('--big') + 1])
        except (IndexError, ValueError):
            pass
    if not os.path.exists(path):
        print('文件不存在：%s' % path)
        return 2

    segs, begins, ends, previews, warns, rest_ticks = parse(path)
    print('=== %s ===' % os.path.basename(path))
    print('T 段数=%d  begin=%d  end=%d  P=%d  休息倒计时行=%d'
          % (len(segs), len(begins), len(ends), len(previews), len(rest_ticks)))
    if rest_ticks:
        print('休息倒计时序列：%s  ← 自然坐满应以 1 结尾；末尾 >1 说明被提前结束'
              % rest_ticks)

    print('')
    print('%-4s %-7s %-9s %-11s %-9s %-16s %s'
          % ('轮', 'T条数', 'T最大间隔', '≥%dms 条数' % big_thr, 'lateMs',
             '判定', 'T分桶'))
    allgaps = []
    for i, seg in enumerate(segs):
        g = gaps_of(seg)
        allgaps.extend(g)
        late = begins[i]['late'] if i < len(begins) else None
        print('%-4d %-7d %-9d %-11d %-9s %-16s %s'
              % (i + 1, len(seg), max(g) if g else 0,
                 len([x for x in g if x >= big_thr]),
                 late if late is not None else '-',
                 classify_late(late, g),
                 buckets(g) if g else '-'))

    if allgaps:
        print('')
        print('段内合计 %d 个间隔：最大 %dms，≥%dms %d 条，≥2000ms %d 条'
              % (len(allgaps), max(allgaps), big_thr,
                 len([x for x in allgaps if x >= big_thr]),
                 len([x for x in allgaps if x >= 2000])))

    if begins:
        lates = [b['late'] for b in begins]
        print('lateMs 序列：%s' % lates)
        biglate = [x for x in lates if x >= 2000]
        has_gap = any(x >= 2000 for x in allgaps)
        if biglate:
            print('交叉判定：lateMs 有大值(%s)，T 出现 ≥2000ms 空档=%s → %s'
                  % (biglate, has_gap,
                     '**丢了 tick**' if has_gap else '**不是丢 tick（路径阻塞/让路）**'))

    if ends:
        durs = []
        for cols in ends:
            try:
                durs.append(int(cols[2]))
            except (IndexError, ValueError):
                pass
        print('休息持续(ms)：%s' % durs)

    if previews:
        pg = gaps_of([ms for (ms, _s) in previews])
        print('')
        print('P 行 %d 条，间隔最大 %dms，≥2000ms %d 条；分桶 %s'
              % (len(previews), max(pg) if pg else 0,
                 len([x for x in pg if x >= 2000]), buckets(pg) if pg else '-'))
        print('P 秒数序列：%s' % [s for (_ms, s) in previews])

    # 规则 3：解析告警必须报出来
    print('')
    print('解析告警：未知行首 %d 条 / 字段数异常 %d 条 / 时间戳回退 %d 条'
          % (len(warns['unknown']), len(warns['bad_fields']), len(warns['regress'])))
    if warns['unknown']:
        print('  未知行首示例：%s' % [l for (_n, l) in warns['unknown'][:3]])
    if warns['bad_fields']:
        print('  字段数异常示例：%s' % [l for (_n, l) in warns['bad_fields'][:3]])
    if warns['regress']:
        print('  时间戳回退示例：%s' % ['%d: %d→%d' % (n, a, b)
                                      for (n, a, b) in warns['regress'][:3]])
    return 0


if __name__ == '__main__':
    sys.exit(main())
