// =====================================================================
//  EyeLite v3 - 轻量护眼提醒
//
//  设计约束（针对"开机常驻到关机"这个场景）：
//   1. 启动快：不做任何重活，休息窗体懒创建，首屏出现前不碰 GDI+
//   2. 内存平：休息窗体只创建一次，之后 Show/Hide 复用；
//              字体全程序共用静态实例（避免每次 new Font 泄漏 GDI 句柄）；
//              每秒 tick 的路径上不 new 任何 GDI 对象（画刷/画笔全部构造期建好）
//   3. 零第三方依赖：只用 .NET Framework 自带 WinForms
//   4. 不向任何长生命周期对象订阅事件（原 ProjectEye 就是死在 21 处 += / 0 处 -=）
//   5. 绝不缓存全屏位图（3840x2160x4 ≈ 33MB），渐变一律 OnPaintBackground 实时画
//
//  视觉：深色静谧（Quiet Depth）遮罩 + 浅色卡片式设置面板
//  语言版本：C# 5.0（兼容系统自带 csc.exe 4.0.30319）
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace EyeLite
{
    // ================================================================
    // 0. 视觉常量与共享字体（全程序只创建一次，避免 GDI 句柄泄漏）
    // ================================================================
    internal static class Ui
    {
        // ---- 跨线程投递（钩子回调里绝不做 UI）----
        // 低级键盘钩子回调超过 LowLevelHooksTimeout（默认 300ms）会被系统静默摘钩，
        // 现象是"之后既不吞也不跳过"且没有任何日志，现场极难复现。
        // 所以钩子里只做标记 + Post，UI 工作全部回到消息循环执行。
        public static System.Threading.SynchronizationContext Context;

        /// <summary>把动作投递到 UI 线程；没有 Context 或投递失败时退化为同步执行（宁可慢，不能丢）。</summary>
        public static void Post(Action action)
        {
            if (action == null) return;

            System.Threading.SynchronizationContext ctx = Context;
            if (ctx == null)
            {
                try { action(); }
                catch { }
                return;
            }

            try
            {
                ctx.Post(delegate { try { action(); } catch { } }, null);
            }
            catch
            {
                try { action(); }
                catch { }
            }
        }

        // ---- 深色遮罩（主色 60% / 辅助 30% / 强调 10%）----
        public static readonly Color GradTop = Color.FromArgb(0x08, 0x0B, 0x10);      // #080B10
        public static readonly Color GradBottom = Color.FromArgb(0x10, 0x17, 0x20);   // #101720
        public static readonly Color GlowCore = Color.FromArgb(0x1A, 0x24, 0x32);     // #1A2432
        public static readonly Color TimeText = Color.FromArgb(0xF2, 0xF6, 0xFA);     // #F2F6FA
        public static readonly Color RingTrack = Color.FromArgb(0x1E, 0x27, 0x33);    // #1E2733
        public static readonly Color RingLabel = Color.FromArgb(0x5F, 0x70, 0x85);    // #5F7085
        public static readonly Color Accent = Color.FromArgb(0x5F, 0xD3, 0xA6);       // #5FD3A6

        // ---- 番茄钟休息是一套暖橙配色（只换颜色，不换任何尺寸）----
        public static readonly Color AccentPomodoro = Color.FromArgb(0xF2, 0x81, 0x5C);    // #F2815C
        public static readonly Color RingTrackPomodoro = Color.FromArgb(0x3A, 0x2A, 0x22); // #3A2A22
        public static readonly Color RingLabelPomodoro = Color.FromArgb(0x8A, 0x6A, 0x5A); // #8A6A5A

        public static readonly Color EyebrowText = Color.FromArgb(0x6B, 0x7F, 0x96);  // #6B7F96（遮罩眉标，原 #4E5C70 太暗）
        public static readonly Color HintText = Color.FromArgb(0x8F, 0xA6, 0xC0);     // #8FA6C0
        public static readonly Color DarkTipText = Color.FromArgb(0x46, 0x53, 0x6A);  // #46536A
        public static readonly Color DarkBtnBg = Color.FromArgb(0x14, 0x1C, 0x26);    // #141C26
        public static readonly Color DarkBtnHover = Color.FromArgb(0x1E, 0x28, 0x36); // #1E2836
        public static readonly Color DarkBtnBorder = Color.FromArgb(0x2E, 0x39, 0x47); // #2E3947
        public static readonly Color DarkBtnText = Color.FromArgb(0x9F, 0xB0, 0xC4);  // #9FB0C4
        public static readonly Color SkipText = Color.FromArgb(0xD8, 0xF3, 0xE6);     // #D8F3E6

        // ---- 浅色设置面板 ----
        public static readonly Color PageBg = Color.FromArgb(0xF5, 0xF7, 0xFA);        // #F5F7FA
        public static readonly Color CardBg = Color.FromArgb(0xFF, 0xFF, 0xFF);        // #FFFFFF
        public static readonly Color CardBorder = Color.FromArgb(0xEA, 0xEE, 0xF4);    // #EAEEF4
        public static readonly Color TitleText = Color.FromArgb(0x1A, 0x22, 0x30);     // #1A2230
        public static readonly Color LabelText = Color.FromArgb(0x4A, 0x55, 0x68);     // #4A5568
        public static readonly Color CaptionText = Color.FromArgb(0x9A, 0xA4, 0xB2);   // #9AA4B2
        public static readonly Color PrimaryBg = Color.FromArgb(0x1F, 0x29, 0x37);     // #1F2937
        public static readonly Color PrimaryHover = Color.FromArgb(0x2C, 0x39, 0x4B);
        public static readonly Color SecondaryBorder = Color.FromArgb(0xD5, 0xDB, 0xE4); // #D5DBE4
        public static readonly Color SecondaryHover = Color.FromArgb(0xF0, 0xF3, 0xF7);

        // ---- 共享字体（static readonly，进程内只建一次，永不释放）----
        public static readonly Font FontHint = new Font("微软雅黑", 20f, FontStyle.Regular);      // 遮罩主提示
        public static readonly Font FontTip = new Font("微软雅黑", 11f, FontStyle.Regular);       // 遮罩副提示
        public static readonly Font FontEyebrow = new Font("微软雅黑", 9f, FontStyle.Regular);    // 设置页眉标 / 卡片标题
        public static readonly Font FontRestEyebrow = new Font("微软雅黑", 12f, FontStyle.Regular); // 遮罩眉标「休息一下」（9pt 太小）
        public static readonly Font FontTitle = new Font("微软雅黑", 19f, FontStyle.Bold);        // 设置页主标题
        public static readonly Font FontRingLabel = new Font("微软雅黑", 12f, FontStyle.Regular); // 环心「剩余」
        public static readonly Font FontRowLabel = new Font("微软雅黑", 10.5f, FontStyle.Regular);
        public static readonly Font FontMenu = new Font("微软雅黑", 9.5f);
        public static readonly Font FontUi = new Font("微软雅黑", 10f);

        // 倒计时数字用等宽 Consolas，字号随屏幕高度变化。
        // 尺寸不同不能做成单个 static readonly，用静态字典缓存：每种字号只建一次，
        // 多显示器/分辨率变化最多多出 1~2 个 Font，且永不重建（不进 tick 路径）。
        private static readonly Dictionary<int, Font> timerFonts = new Dictionary<int, Font>();

        public static Font GetTimeFont(int pixelSize)
        {
            if (pixelSize < 24) pixelSize = 24;
            if (pixelSize > 240) pixelSize = 240;
            Font f;
            if (timerFonts.TryGetValue(pixelSize, out f)) return f;
            f = new Font("Consolas", (float)pixelSize, FontStyle.Regular, GraphicsUnit.Pixel);
            timerFonts[pixelSize] = f;
            return f;
        }

        /// <summary>
        /// 探测 DPI 缩放因子（1.0 = 96dpi）。
        /// 本程序用的是 SetProcessDPIAware + AutoScaleMode.None + 物理像素版式常量，
        /// 但 font 是按 pt 定义的，会随系统 DPI 放大 —— 所以版式常量必须乘上这个因子，
        /// 否则"字体高度 : 盒子高度"的比值会随 DPI 变化，文字就会被裁。
        ///
        /// 顺序很重要：**屏幕 DC 优先**。
        /// Control.DeviceDpi 在控件还没建句柄时返回内部默认值 96，
        /// 这会让 (96/96=1.0) 命中并提前 return，永远走不到回退分支
        /// —— 上一版就是这样让 DPI 恒等于 1.0、所有文字继续被裁的。
        /// </summary>
        public static float DetectScale(Control c)
        {
            // 第 1 步：屏幕 DC。进程已 SetProcessDPIAware（系统级 DPI 感知），
            //    GetDeviceCaps(LOGPIXELSX) 直接给系统 DPI（144 -> 1.5），
            //    与"控件是否已建句柄"无关，最可靠。
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float k1 = g.DpiX / 96f;
                    if (k1 > 0.5f && k1 < 4f) return k1;
                }
            }
            catch { }

            // 第 2 步（回退）：只有已建句柄时 DeviceDpi 才可信；无句柄时它返回 96 会误判成 1.0，
            //    所以这里必须再加 >1.05 的门槛。走反射以兼容 .NET 4.7 以下的引用程序集。
            try
            {
                System.Reflection.PropertyInfo pi = typeof(Control).GetProperty("DeviceDpi");
                if (pi != null && c != null && c.IsHandleCreated)
                {
                    object v = pi.GetValue(c, null);
                    if (v is int)
                    {
                        float k2 = (float)((int)v) / 96f;
                        if (k2 > 1.05f && k2 < 4f) return k2;
                    }
                }
            }
            catch { }

            return 1f;
        }

        /// <summary>圆角矩形路径。调用方负责 Dispose。</summary>
        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0)
            {
                p.AddRectangle(new Rectangle(r.X, r.Y, 1, 1));
                return p;
            }
            int d = radius * 2;
            if (d <= 1)
            {
                p.AddRectangle(r);
                return p;
            }
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ================================================================
    // 1. 通用自绘控件（构造期建 Pen/Path，重绘零分配）
    // ================================================================

    /// <summary>圆角面板：圆角用 Region 裁出窗口形状，边框用构造期创建的 Pen 画。</summary>
    internal class RoundPanel : Panel
    {
        private readonly int radius;
        private readonly Pen borderPen;
        private GraphicsPath path;
        private Size pathSize = Size.Empty;

        public RoundPanel(int radius, Color back, Color border)
        {
            this.radius = radius;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint |
                          ControlStyles.OptimizedDoubleBuffer |
                          ControlStyles.UserPaint |
                          ControlStyles.ResizeRedraw, true);
            this.BackColor = back;
            this.borderPen = new Pen(border, 1f);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Rebuild();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Rebuild();
        }

        private void Rebuild()
        {
            Size s = this.ClientSize;
            if (s.Width <= 4 || s.Height <= 4) return;
            if (path != null && pathSize.Width == s.Width && pathSize.Height == s.Height) return;

            // 圆角必须钳死：radius 一旦等于高度的一半，左右两段弧正好退化成点、
            // 路径自交，DrawPath 会把描边切成虚线。留 1px 直线段保证是简单闭合曲线。
            int r = radius;
            int maxR = Math.Min(s.Width, s.Height) / 2 - 1;
            if (r > maxR) r = maxR;
            if (r < 2) r = 2;

            if (path != null) { path.Dispose(); path = null; }
            pathSize = s;
            path = Ui.RoundedRect(new Rectangle(0, 0, s.Width - 1, s.Height - 1), r);

            Region old = this.Region;
            this.Region = new Region(path);
            if (old != null) old.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (path == null) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(borderPen, path);
        }

        protected override void Dispose(bool disposing)
        {
            Region reg = null;
            if (disposing)
            {
                borderPen.Dispose();
                if (path != null) { path.Dispose(); path = null; }
                reg = this.Region;
            }
            base.Dispose(disposing);
            if (reg != null) reg.Dispose();
        }
    }

    /// <summary>圆角按钮：窗口形状裁圆角，文字与边框自绘，hover 只改 BackColor。</summary>
    internal class RoundButton : Button
    {
        private readonly int radius;
        private readonly Color normalBack;
        private readonly Color hoverBack;
        private Pen borderPen;              // null 表示无边框；SetBorderColor 会替换它
        private GraphicsPath path;
        private Size pathSize = Size.Empty;

        public RoundButton(string text, int radius, Color back, Color hoverBack,
                           Color fore, Color border, int borderWidth)
        {
            this.radius = radius;
            this.normalBack = back;
            this.hoverBack = hoverBack;
            this.Text = text;
            this.BackColor = back;
            this.ForeColor = fore;
            this.SetStyle(ControlStyles.UserPaint |
                          ControlStyles.AllPaintingInWmPaint |
                          ControlStyles.OptimizedDoubleBuffer |
                          ControlStyles.ResizeRedraw, true);
            this.FlatStyle = FlatStyle.Flat;
            this.FlatAppearance.BorderSize = 0;
            this.UseVisualStyleBackColor = false;
            this.TabStop = false;
            this.Cursor = Cursors.Hand;
            this.borderPen = borderWidth > 0 ? new Pen(border, (float)borderWidth) : null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Rebuild();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Rebuild();
        }

        private void Rebuild()
        {
            Size s = this.ClientSize;
            if (s.Width <= 4 || s.Height <= 4) return;
            if (path != null && pathSize.Width == s.Width && pathSize.Height == s.Height) return;

            // 圆角必须钳死：radius 一旦等于高度的一半，左右两段弧正好退化成点、
            // 路径自交，DrawPath 会把描边切成虚线。留 1px 直线段保证是简单闭合曲线。
            int r = radius;
            int maxR = Math.Min(s.Width, s.Height) / 2 - 1;
            if (r > maxR) r = maxR;
            if (r < 2) r = 2;

            if (path != null) { path.Dispose(); path = null; }
            pathSize = s;
            path = Ui.RoundedRect(new Rectangle(0, 0, s.Width - 1, s.Height - 1), r);

            Region old = this.Region;
            this.Region = new Region(path);
            if (old != null) old.Dispose();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            this.BackColor = hoverBack;
        }

        /// <summary>
        /// 运行时换边框色（番茄钟换暖橙）。只在每次开始休息时调一次，
        /// 不在每秒 tick 路径上，所以这里新建一个 Pen 是可接受的。
        /// </summary>
        public void SetBorderColor(Color c)
        {
            if (borderPen == null) return;          // 无边框按钮（如「保存」）不处理
            if (borderPen.Color == c) return;

            Pen old = borderPen;
            borderPen = new Pen(c, old.Width);
            old.Dispose();
            this.Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            this.BackColor = normalBack;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (path == null) { base.OnPaint(e); return; }
            Graphics g = e.Graphics;
            // 窗口已被 Region 裁成圆角，这里整块填充就是圆角底
            g.Clear(this.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (borderPen != null) g.DrawPath(borderPen, path);
            TextRenderer.DrawText(g, this.Text, this.Font, this.ClientRectangle, this.ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            Region reg = null;
            if (disposing)
            {
                if (borderPen != null) borderPen.Dispose();
                if (path != null) { path.Dispose(); path = null; }
                reg = this.Region;
            }
            base.Dispose(disposing);
            if (reg != null) reg.Dispose();
        }
    }

    // ================================================================
    // 2. 音效：代码合成 WAV，不依赖任何音频文件
    // ================================================================
    internal static class Sfx
    {
        // SND_ASYNC 下 winmm 会在 API 返回后继续读这块内存约 1 秒，
        // 所以必须传"固定地址"而不是每个调用临时 pin 的 byte[]，
        // 否则中途一次 Gen0 压缩搬走数组就会读到垃圾 -> 破音/提前中断。
        [DllImport("winmm.dll")]
        private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

        private const uint SND_ASYNC = 0x0001;
        private const uint SND_MEMORY = 0x0004;
        private const uint SND_NODEFAULT = 0x0002;

        // 缓存合成的音频（进程内只合成一次）
        private static byte[] startWave = null;
        private static byte[] endWave = null;

        // 两个波各自一个 pin 句柄：异步播放期间地址固定。
        // 不在每次播放前 Free/重新 pin —— 那会在上一段音频还没播完时释放 pin，
        // 正好复现要修的 bug。这两个数组本身是永久缓存，pin 到进程退出即可。
        private static GCHandle startHandle;
        private static GCHandle endHandle;

        public static void PlayStart()
        {
            try
            {
                if (!startHandle.IsAllocated)
                {
                    if (startWave == null)
                    {
                        // 温暖上行两音 G4 -> C5（低一个八度，去掉刺耳高频），每音 500ms
                        startWave = BuildWave(new double[] { 392.00, 523.25 }, 500, 0.20);
                    }
                    startHandle = GCHandle.Alloc(startWave, GCHandleType.Pinned);
                }
                if (!PlaySound(startHandle.AddrOfPinnedObject(), IntPtr.Zero,
                               SND_MEMORY | SND_ASYNC | SND_NODEFAULT))
                    System.Media.SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        public static void PlayEnd()
        {
            try
            {
                if (!endHandle.IsAllocated)
                {
                    if (endWave == null)
                    {
                        // C 大三和弦分解 C5 -> E5 -> G5，每音 333ms，收尾安静不刺耳
                        endWave = BuildWave(new double[] { 523.25, 659.25, 783.99 }, 333, 0.20);
                    }
                    endHandle = GCHandle.Alloc(endWave, GCHandleType.Pinned);
                }
                if (!PlaySound(endHandle.AddrOfPinnedObject(), IntPtr.Zero,
                               SND_MEMORY | SND_ASYNC | SND_NODEFAULT))
                    System.Media.SystemSounds.Exclamation.Play();
            }
            catch { }
        }

        /// <summary>
        /// 合成 16bit / 22050Hz 单声道 WAV。
        /// 音色：基频 + 0.22 倍二次谐波（补一点暖度，不做锯齿/方波）。
        /// 包络：指数衰减 exp(-2.6 * i / perNote) + 20ms 线性 attack + 20ms 线性释放。
        /// </summary>
        private static byte[] BuildWave(double[] freqs, int msPerNote, double amp)
        {
            const int rate = 22050;
            const int attackMs = 20;
            const int releaseMs = 20;
            const double secondHarmonic = 0.22;
            const double decayK = -2.6;

            int perNote = rate * msPerNote / 1000;
            int totalSamples = perNote * freqs.Length;
            int dataBytes = totalSamples * 2;

            int attack = rate * attackMs / 1000;
            int release = rate * releaseMs / 1000;
            if (attack > perNote / 4) attack = perNote / 4;
            if (release > perNote / 4) release = perNote / 4;

            using (MemoryStream ms = new MemoryStream(44 + dataBytes))
            {
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(new char[] { 'R', 'I', 'F', 'F' });
                    w.Write(36 + dataBytes);
                    w.Write(new char[] { 'W', 'A', 'V', 'E' });
                    w.Write(new char[] { 'f', 'm', 't', ' ' });
                    w.Write(16);
                    w.Write((short)1);
                    w.Write((short)1);
                    w.Write(rate);
                    w.Write(rate * 2);
                    w.Write((short)2);
                    w.Write((short)16);
                    w.Write(new char[] { 'd', 'a', 't', 'a' });
                    w.Write(dataBytes);

                    for (int n = 0; n < freqs.Length; n++)
                    {
                        double f = freqs[n];
                        for (int i = 0; i < perNote; i++)
                        {
                            double env = Math.Exp(decayK * i / perNote);
                            if (attack > 0 && i < attack) env *= (double)i / attack;
                            int tail = perNote - 1 - i;
                            if (release > 0 && tail < release) env *= (double)tail / release;
                            if (env < 0.0) env = 0.0;

                            double t = (double)i / rate;
                            double tone = Math.Sin(2.0 * Math.PI * f * t)
                                        + secondHarmonic * Math.Sin(2.0 * Math.PI * 2.0 * f * t);
                            double v = tone * amp * env;
                            w.Write((short)(v * 32767));
                        }
                    }
                }
                return ms.ToArray();
            }
        }
    }

    // ================================================================
    // 3. 严格模式：低级键盘钩子。休息期间可屏蔽系统键，甚至全吞键盘
    //    （只放行 Esc / 空格用于跳过），详见 HookCallback 的分层注释。
    // ================================================================
    internal static class KeyboardGuard
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_TAB = 0x09;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_F4 = 0x73;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_SHIFT = 0x10;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // 静态字段持有委托，防止 GC 回收导致回调野指针
        private static HookProc proc = new HookProc(HookCallback);
        private static IntPtr hookHandle = IntPtr.Zero;
        private static bool active = false;

        // 跳过键（裸 Esc / 空格）的回调：遮罩按设计不抢焦点（见 RestForm.ShowWithoutActivation），
        // 窗体的 KeyDown 永远收不到键，跳过完全靠这个全局钩子兜住。
        // 由 TrayApp 在 Install() 前挂上、Uninstall() 后清空；AllowSkip=false 时不挂。
        public static Action OnSkipKey;

        // 是否屏蔽系统键（Win / Tab / Alt+F4 / Ctrl+Esc）
        private static bool blockSystemKeys = false;

        // 是否"全吞键盘"（严格模式 + 有跳过出口时）。任何按键消息都不放行，
        // 效果是"休息期间按键盘没反应，结束后接着打"，而不是盲打落到文档里。
        private static bool blockAllKeys = false;

        /// <summary>
        /// 安装全局键盘钩子。
        /// blockSystem = true 时屏蔽系统键；blockAll = true 时连普通键一起吞。
        /// 返回 false 表示没装上。
        /// </summary>
        public static bool Install(bool blockSystem, bool blockAll)
        {
            if (hookHandle != IntPtr.Zero)
            {
                blockSystemKeys = blockSystem;    // 已装则只更新屏蔽策略
                blockAllKeys = blockAll;
                return true;
            }
            try
            {
                active = true;
                blockSystemKeys = blockSystem;
                blockAllKeys = blockAll;
                hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
                if (hookHandle == IntPtr.Zero) active = false;
            }
            catch { active = false; hookHandle = IntPtr.Zero; }
            return hookHandle != IntPtr.Zero;
        }

        public static void Uninstall()
        {
            active = false;
            blockSystemKeys = false;
            blockAllKeys = false;

            // 钩子不跑了，drain 状态必须一起归零。
            // 否则 ExitApp / Dispose / OnTick 自愈里直接调 Uninstall() 之后 drainVk 仍留着，
            // 下次休息一装钩子就进 ⓪ 分支把所有按键静默吞掉、且等不到那个 keyup。
            //
            // 注意：这里**只清钩子内部状态**，不要顺手把 OnSkipKey 也清了 ——
            // 两者生命周期不同：OnSkipKey 是回调，由 EndRest 挂载/清理；
            // 若在这里清，ExitApp/Dispose 等路径会提前抹掉还没走完的收尾逻辑。
            drainVk = 0;
            drainRearms = 0;
            if (drainTimer != null) { try { drainTimer.Dispose(); } catch { } drainTimer = null; }

            if (hookHandle != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(hookHandle); }
                catch { }
                hookHandle = IntPtr.Zero;
            }
        }

        // ------------------------------------------------------------
        // 跳过后不能立刻卸钩子：用户的手可能还按着那个键，后续 key-repeat
        // 会漏进下层文档（打出一串空格）。做法是记下这个键，继续吞它的
        // down/up，直到收到 keyup 才真正卸载。
        //
        // 三重复位，任何一条先到都算收尾：
        //   ① 收到该键的 keyup（正常路径，通常 200ms 内）
        //   ② OnTick 每秒检查一次"键其实已经抬起了"（keyup 事件丢失时提前收尾）
        //   ③ 2 秒定时器兜底，最多续期 3 次（合计 8 秒）后强制卸载
        //      —— 防止某个键物理卡住时钩子永久挂着、整个键盘都不能用。
        // ------------------------------------------------------------
        private static int drainVk = 0;
        private static int drainRearms = 0;
        private static System.Threading.Timer drainTimer;

        /// <summary>是否处于收尾期（OnTick 用它做双保险检查）。</summary>
        public static bool IsDraining
        {
            get { return drainVk != 0; }
        }

        /// <summary>收尾键是否仍处于物理按下状态。</summary>
        public static bool IsDrainKeyDown
        {
            get { return drainVk != 0 && Down(drainVk); }
        }

        /// <summary>强制立即卸载（OnTick 发现键已抬起时用）。</summary>
        public static void ForceUninstall()
        {
            DoUninstall();
        }

        /// <summary>休息结束时调用：有键还按着就等它的 keyup，否则立刻卸。</summary>
        public static void RequestUninstall()
        {
            if (drainVk == 0)
            {
                DoUninstall();
                return;
            }

            if (drainTimer != null) { try { drainTimer.Dispose(); } catch { } }
            drainTimer = new System.Threading.Timer(delegate
            {
                // 键还按着就续期，但最多 3 次（2s × 4 轮 = 8 秒）后强制卸载。
                if (drainRearms < 3 && Down(drainVk))
                {
                    drainRearms++;
                    RequestUninstall();
                    return;
                }
                DoUninstall();
            }, null, 2000, System.Threading.Timeout.Infinite);
        }

        private static void DoUninstall()
        {
            // 复位 drainVk / 释放 drainTimer 都在 Uninstall() 里统一做，
            // 保证"直接卸载"与"drain 后卸载"两条路径的状态一致。
            Uninstall();
        }

        private static bool Down(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && active)
            {
                int msg = wParam.ToInt32();
                bool isDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                bool isUp = (msg == WM_KEYUP || msg == WM_SYSKEYUP);

                if (isDown || isUp)
                {
                    int vk = Marshal.ReadInt32(lParam);

                    // ---- ⓪ drain 期：只吞不判，等到 keyup 或 2 秒超时才真正卸载 ----
                    // 必须排在最前面：跳过之后用户可能还按着那个键，
                    // 这里继续吞掉它的 repeat，避免漏进下层文档。
                    if (drainVk != 0)
                    {
                        if (isUp && vk == drainVk) DoUninstall();
                        return new IntPtr(1);
                    }

                    // ---- ① 跳过键：必须排在"全吞"之前，且只处理按下 ----
                    // 顺序错了就会出现"严格模式下按 Esc 被吞掉、却什么都不发生"。
                    if (isDown)
                    {
                        // 跳过必须是「裸按键」：Shift / Ctrl / Alt 任一个按下都不算。
                        // 原因：中文输入法里 Shift+Space 是切换全角/半角、Ctrl+Space 是切换中英文，
                        // 这两个都是"切换动作"而非"打字动作"，用户休息时随手一按就会把休息结束掉，
                        // 比 Alt+Space（窗口系统菜单）常见得多。
                        // Esc 与空格用同一个判据 —— 对称的基准是"裸按键才表达意图"，
                        // 而不是"两个键各自保留 Shift"。
                        //
                        // 注：严格模式下这三个组合会被下面的"全吞"吞掉，也就是说休息期间
                        // 切输入法/切全角同样失效。这是刻意的（休息就该离开键盘），不是 bug，别"修"。
                        bool noModifier = !Down(VK_CONTROL) && !Down(VK_MENU) && !Down(VK_SHIFT);
                        bool plainEsc = (vk == VK_ESCAPE) && noModifier;
                        bool plainSpace = (vk == VK_SPACE) && noModifier;
                        if (plainEsc || plainSpace)
                        {
                            Action cb = OnSkipKey;
                            if (cb != null)
                            {
                                drainVk = vk;    // 记下这个键，继续吞它的后续消息

                                // 只投递、立刻返回：低级钩子回调超过 LowLevelHooksTimeout
                                // 会被系统静默摘钩（之后既不吞也不跳过，且没有日志）。
                                Ui.Post(cb);
                                return new IntPtr(1);
                            }
                        }
                    }

                    // ---- ② 严格模式：任何按键消息都不放行（含抬起，避免目标程序收到"孤立的抬起"）----
                    if (blockAllKeys) return new IntPtr(1);

                    // ---- ③ 仅屏蔽系统键（AllowSkip=false 时的降级路径）----
                    if (blockSystemKeys)
                    {
                        if (vk == VK_LWIN || vk == VK_RWIN) return new IntPtr(1);
                        if (vk == VK_TAB) return new IntPtr(1);
                        if (vk == VK_F4 && Down(VK_MENU)) return new IntPtr(1);
                        if (vk == VK_ESCAPE && (Down(VK_CONTROL) || Down(VK_MENU))) return new IntPtr(1);
                    }
                }
            }
            return CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }
    }

    // ================================================================
    // 4. 配置
    // ================================================================
    internal class AppConfig
    {
        public int WorkMinutes = 20;
        public int RestSeconds = 20;
        public bool PomodoroEnabled = false;
        public int PomodoroWork = 25;
        public int PomodoroRest = 5;
        public bool AllowSkip = true;
        public int PostponeMinutes = 5;
        public int IdleMinutes = 3;
        public bool SoundEnabled = true;
        public bool StrictMode = true;
        public bool StartWithWindows = false;

        public static string ConfigDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "EyeLite");
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(ConfigDir, "config.ini"); }
        }

        public static AppConfig Load()
        {
            AppConfig c = new AppConfig();
            try
            {
                if (!File.Exists(ConfigPath)) return c;
                string[] lines = File.ReadAllLines(ConfigPath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "WorkMinutes": c.WorkMinutes = Clamp(v, 20, 1, 240); break;
                        case "RestSeconds": c.RestSeconds = Clamp(v, 20, 5, 1800); break;
                        case "PomodoroEnabled": c.PomodoroEnabled = v == "1"; break;
                        case "PomodoroWork": c.PomodoroWork = Clamp(v, 25, 1, 240); break;
                        case "PomodoroRest": c.PomodoroRest = Clamp(v, 5, 1, 120); break;
                        case "AllowSkip": c.AllowSkip = v == "1"; break;
                        case "PostponeMinutes": c.PostponeMinutes = Clamp(v, 5, 1, 60); break;
                        case "IdleMinutes": c.IdleMinutes = Clamp(v, 3, 0, 120); break;
                        case "SoundEnabled": c.SoundEnabled = v == "1"; break;
                        case "StrictMode": c.StrictMode = v == "1"; break;
                        case "StartWithWindows": c.StartWithWindows = v == "1"; break;
                    }
                }
            }
            catch { }
            return c;
        }

        private static int Clamp(string s, int def, int min, int max)
        {
            int r;
            if (!int.TryParse(s, out r)) return def;
            if (r < min) r = min;
            if (r > max) r = max;
            return r;
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# EyeLite config");
                sb.AppendLine("[General]");
                sb.AppendLine("WorkMinutes=" + WorkMinutes);
                sb.AppendLine("RestSeconds=" + RestSeconds);
                sb.AppendLine("PomodoroEnabled=" + (PomodoroEnabled ? "1" : "0"));
                sb.AppendLine("PomodoroWork=" + PomodoroWork);
                sb.AppendLine("PomodoroRest=" + PomodoroRest);
                sb.AppendLine("AllowSkip=" + (AllowSkip ? "1" : "0"));
                sb.AppendLine("PostponeMinutes=" + PostponeMinutes);
                sb.AppendLine("IdleMinutes=" + IdleMinutes);
                sb.AppendLine("SoundEnabled=" + (SoundEnabled ? "1" : "0"));
                sb.AppendLine("StrictMode=" + (StrictMode ? "1" : "0"));
                sb.AppendLine("StartWithWindows=" + (StartWithWindows ? "1" : "0"));
                File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ================================================================
    // 5. 全屏休息窗体（只创建一次，之后 Show/Hide 复用）
    // ================================================================
    internal class RestForm : Form
    {
        // 淡入：0.02 -> 0.97，16ms 一帧，约 250ms 到位
        private const float TargetOpacity = 0.97f;
        private const float FadeStep = TargetOpacity * 16f / 250f;

        private Label lblEyebrow;   // 「休息一下」/「番茄钟休息」
        private Label lblHint;
        private Label lblTip;       // 副提示：护眼 / 番茄钟两套文案
        private RingMeter ring;     // 环 + 环心数字，单一视觉单元
        private RoundButton btnSkip; // 需要运行时换边框色（番茄钟暖橙）
        private bool isPomodoro;    // 当前是番茄钟休息（只影响颜色与文案，不影响任何尺寸）

        // 构造期建好、整个窗体生命周期复用；Dispose(bool) 里释放
        private LinearGradientBrush bgBrush;
        private PathGradientBrush glowBrush;
        private Rectangle glowRect;

        // 淡入定时器：字段复用，不每次 new
        private System.Windows.Forms.Timer fadeTimer;

        private bool showButtons;
        private bool allowSkip;

        // DPI 缩放因子（1.0 = 96dpi）。所有物理像素版式常量都过 Sc()，
        // 让任何 DPI 下"字体高度 : 盒子高度"的比值恒定，文字不会被裁。
        private float dpiScale = 1f;

        // 用 +0.5f 截断而非 Math.Round：后者是银行家舍入（Math.Round(1.5)=2、Math.Round(2.5)=2），
        // 在 1.25 / 1.5 这类缩放上会出现按值取偶导致的 ±1px 抖动。
        private int Sc(int px)
        {
            return (int)(px * dpiScale + 0.5f);
        }

        public event EventHandler RequestSkip;
        public event EventHandler RequestPostpone;

        public RestForm(Screen screen, bool allowSkip, int postponeMinutes, bool withButtons)
        {
            // 必须在算任何版式之前取到 DPI 因子（此时窗体还没有句柄）
            dpiScale = Ui.DetectScale(this);

            this.allowSkip = allowSkip;
            this.showButtons = withButtons;

            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = screen.Bounds;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.BackColor = Ui.GradTop;
            this.Opacity = TargetOpacity;
            this.KeyPreview = true;
            this.AutoScaleMode = AutoScaleMode.None;
            this.DoubleBuffered = true;

            int w = screen.Bounds.Width;
            int h = screen.Bounds.Height;

            // ---- 背景垂直渐变（brush 只建一次，重绘直接 FillRectangle）----
            try
            {
                bgBrush = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                                                 Ui.GradTop, Ui.GradBottom,
                                                 LinearGradientMode.Vertical);
            }
            catch { bgBrush = null; }

            // ---- 中央极弱径向光晕（PathGradientBrush 只建一次）----
            try
            {
                int gw = (int)(w * 0.46);
                int gh = (int)(h * 0.62);
                if (gw < 320) gw = 320;
                if (gh < 320) gh = 320;
                glowRect = new Rectangle((w - gw) / 2, (h - gh) / 2, gw, gh);
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(glowRect);
                    glowBrush = new PathGradientBrush(gp);
                    glowBrush.CenterColor = Color.FromArgb(50, Ui.GlowCore);
                    glowBrush.SurroundColors = new Color[] { Color.FromArgb(0, Ui.GlowCore) };
                }
            }
            catch { glowBrush = null; }

            // ---- 版式：单一焦点。环（含环心数字）是绝对主体，上下留白相等 ----
            // 所有常量都过 Sc()（DPI 缩放），字体是 pt 自适应，二者比例才恒定
            bool hasButtons = allowSkip && showButtons;

            // 环直径：屏幕短边的 30%，夹在 Sc(220) ~ Sc(400)
            int ringD = (int)(Math.Min(w, h) * 0.30);
            if (ringD < Sc(220)) ringD = Sc(220);
            if (ringD > Sc(400)) ringD = Sc(400);

            int eyebrowH = Sc(22);   // 12pt 行高约 21px（96dpi），给 22 才不会切下缘
            int gapEyebrowRing = Sc(28);
            int gapRingHint = Sc(40);
            int hintH = Sc(34);
            int gapHintTip = Sc(14);
            int tipH = Sc(24);
            int gapTipBtn = Sc(44);
            int btnH = Sc(42);

            // 按钮区必须计入整块高度：漏掉它会让整块内容被垂直居中"顶偏"，按钮贴底。
            int btnBlockH = hasButtons ? (gapTipBtn + btnH) : 0;
            int contentH = eyebrowH + gapEyebrowRing + ringD + gapRingHint
                         + hintH + gapHintTip + tipH + btnBlockH;

            // ---- 小屏兜底：标准间距装不下就进紧凑模式（间距减半 + 环径按剩余空间收）----
            // 典型场景：副屏是 1024x600 这类小屏，标准版式会溢出屏幕。
            if (contentH > h - Sc(48))
            {
                gapEyebrowRing = Sc(28) / 2;
                gapRingHint = Sc(40) / 2;
                gapHintTip = Sc(14) / 2;
                gapTipBtn = Sc(44) / 2;
                eyebrowH = Sc(22);        // 不用 Sc(20)：12pt 行高约 21px，20 会切掉下缘 1px
                hintH = Sc(34);
                tipH = Sc(24);
                btnH = Sc(42);

                // btnBlockH 依赖 gapTipBtn / btnH，必须在重算 contentH 之前更新，
                // 否则会拿旧值算出错误的 contentH。
                btnBlockH = hasButtons ? (gapTipBtn + btnH) : 0;

                int fixedH = eyebrowH + gapEyebrowRing + gapRingHint + hintH
                           + gapHintTip + tipH + btnBlockH;
                int avail = h - Sc(48) - fixedH;
                int d2 = avail;
                if (d2 < Sc(160)) d2 = Sc(160);
                if (d2 > ringD) d2 = ringD;      // 只收不放
                ringD = d2;

                contentH = fixedH + ringD;
            }

            int top = (h - contentH) / 2;
            if (top < Sc(24)) top = Sc(24);

            // y 坐标链：每块紧接上一块，最后一块的底部 = top + contentH
            int yEyebrow = top;
            int yRing = yEyebrow + eyebrowH + gapEyebrowRing;
            int yHint = yRing + ringD + gapRingHint;
            int yTip = yHint + hintH + gapHintTip;
            int yBtn = yTip + tipH + gapTipBtn;

            lblEyebrow = new Label();
            lblEyebrow.Text = "休息一下";
            lblEyebrow.ForeColor = Ui.EyebrowText;
            lblEyebrow.Font = Ui.FontRestEyebrow;
            lblEyebrow.TextAlign = ContentAlignment.MiddleCenter;
            lblEyebrow.AutoSize = false;
            lblEyebrow.BackColor = Color.Transparent;
            lblEyebrow.SetBounds(0, yEyebrow, w, eyebrowH);
            this.Controls.Add(lblEyebrow);

            // 环 + 数字是一个不可分割的整体：数字画在环心，不再用独立 Label
            ring = new RingMeter(ringD, dpiScale);
            ring.SetBounds((w - ringD) / 2, yRing, ringD, ringD);
            // 把父窗体的背景 brush 借给子控件复刻背景（含光晕），彻底消除纯色方块的硬边。
            // 所有权仍在 RestForm，RingMeter 不释放。
            ring.SetBackdrop(bgBrush, glowBrush, glowRect, new Size(w, h));
            this.Controls.Add(ring);

            lblHint = new Label();
            lblHint.Text = "让眼睛看看 6 米外的东西，放松睫状肌";
            lblHint.ForeColor = Ui.HintText;
            lblHint.Font = Ui.FontHint;
            lblHint.TextAlign = ContentAlignment.MiddleCenter;
            lblHint.AutoSize = false;
            lblHint.BackColor = Color.Transparent;
            lblHint.SetBounds(0, yHint, w, hintH);
            this.Controls.Add(lblHint);

            lblTip = new Label();
            lblTip.Text = "眨眼 · 远眺 · 深呼吸";
            lblTip.ForeColor = Ui.DarkTipText;
            lblTip.Font = Ui.FontTip;
            lblTip.TextAlign = ContentAlignment.MiddleCenter;
            lblTip.AutoSize = false;
            lblTip.BackColor = Color.Transparent;
            lblTip.SetBounds(0, yTip, w, tipH);
            this.Controls.Add(lblTip);

            if (hasButtons)
            {
                // 两枚按钮整体水平居中：总宽 Sc(176) + Sc(16) + Sc(150) = Sc(342)，半宽 Sc(171)。
                //   跳过：w/2 - Sc(171)，占 [-171, +5]
                //   推迟：w/2 + Sc(21)，占 [+21, +171]（5 + 16 = 21）
                // 之前写的是 -Sc(184) / +Sc(8)，组中心落在 -13px 上，肉眼可见偏左。
                // 描边宽度随 DPI 缩放，否则 150% 下 1px 细得像没画
                int bw = Math.Max(1, Sc(1));
                btnSkip = new RoundButton("跳过 (Esc/空格)", Sc(21),
                                          Ui.DarkBtnBg, Ui.DarkBtnHover,
                                          Ui.SkipText, Ui.Accent, bw);
                btnSkip.Font = Ui.FontRowLabel;
                btnSkip.SetBounds(w / 2 - Sc(171), yBtn, Sc(176), btnH);
                btnSkip.Click += delegate
                {
                    if (RequestSkip != null) RequestSkip(this, EventArgs.Empty);
                };
                this.Controls.Add(btnSkip);

                RoundButton btnPostpone = new RoundButton("推迟 " + postponeMinutes + " 分钟", Sc(21),
                                                          Ui.DarkBtnBg, Ui.DarkBtnHover,
                                                          Ui.DarkBtnText, Ui.DarkBtnBorder, bw);
                btnPostpone.Font = Ui.FontRowLabel;
                btnPostpone.SetBounds(w / 2 + Sc(21), yBtn, Sc(150), btnH);
                btnPostpone.Click += delegate
                {
                    if (RequestPostpone != null) RequestPostpone(this, EventArgs.Empty);
                };
                this.Controls.Add(btnPostpone);
            }

            // 这里原来挂着一个 KeyDown 处理器（Esc/空格跳过）。已删除，原因：
            //   1. 遮罩是 WS_EX_NOACTIVATE + ShowWithoutActivation，永远不是前台窗体，
            //      这段 KeyDown 永远不会触发（是死代码）；
            //   2. 它内部的判定是裸 e.KeyCode == Keys.Space，没有修饰键守卫，
            //      一旦以后有人恢复 Activate()，Ctrl/Shift+Space 误跳过的 bug 会原样复活。
            // 跳过现在只走 KeyboardGuard 全局钩子（含裸按键判定与 drain 收尾）。
            // RequestSkip 事件保留不动 —— btnSkip.Click 仍在用。

            // ---- 淡入定时器（字段复用，进程内每个屏幕一个）----
            fadeTimer = new System.Windows.Forms.Timer();
            fadeTimer.Interval = 16;
            fadeTimer.Tick += OnFadeTick;
        }

        // ------------------------------------------------------------
        // 遮罩只负责"显示"，绝不抢键盘焦点：抢焦点会打断用户输入法组词
        //（未上屏内容丢失）并重置光标/选择区。
        // WS_EX_NOACTIVATE 下窗体永不成为前台窗口，但鼠标点击按钮仍然有效
        //（WM_LBUTTONDOWN 照常派发）；跳过键由全局钩子兜底，不依赖焦点。
        // ------------------------------------------------------------
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab、不占任务栏
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (bgBrush != null)
            {
                try { g.FillRectangle(bgBrush, this.ClientRectangle); }
                catch { g.Clear(Ui.GradTop); }
            }
            else
            {
                g.Clear(Ui.GradTop);
            }

            if (glowBrush != null)
            {
                try { g.FillEllipse(glowBrush, glowRect); }
                catch { }
            }
        }

        private void OnFadeTick(object sender, EventArgs e)
        {
            try
            {
                if (!this.Visible)
                {
                    fadeTimer.Stop();
                    return;
                }
                // Form.Opacity 是 double，这里必须用 double 接，float 会 CS0266
                double next = this.Opacity + FadeStep;
                if (next >= TargetOpacity)
                {
                    this.Opacity = TargetOpacity;
                    fadeTimer.Stop();
                }
                else
                {
                    this.Opacity = next;
                }
            }
            catch
            {
                try { fadeTimer.Stop(); } catch { }
                try { this.Opacity = TargetOpacity; } catch { }
            }
        }

        /// <summary>只更新主提示语，眉标保持「休息一下」。</summary>
        public void SetHint(string text)
        {
            SetHint("休息一下", text);
        }

        /// <summary>同时更新眉标与主提示语（番茄钟用「番茄钟休息」）。</summary>
        public void SetHint(string eyebrow, string hint)
        {
            if (lblEyebrow != null) lblEyebrow.Text = eyebrow;
            if (lblHint != null) lblHint.Text = hint;
        }

        /// <summary>
        /// 切换「护眼 / 番茄钟」两套配色与副提示文案。
        /// 只换颜色和文字，绝不碰任何尺寸，所以布局与居中计算完全不受影响。
        /// 由 TrayApp.BeginRest 在每次休息开始时调用一次（不在 tick 路径）。
        /// </summary>
        public void SetPomodoro(bool on)
        {
            this.isPomodoro = on;

            if (ring != null) ring.SetPomodoro(isPomodoro);

            if (lblTip != null)
            {
                lblTip.Text = isPomodoro ? "走动 · 喝水 · 看远处" : "眨眼 · 远眺 · 深呼吸";
            }

            if (btnSkip != null)
            {
                btnSkip.SetBorderColor(isPomodoro ? Ui.AccentPomodoro : Ui.Accent);
            }
        }

        public void UpdateView(int remaining, int total)
        {
            if (remaining < 0) remaining = 0;

            // 环与环心数字是一个单元，一起更新；
            // RingMeter 内部只在数值变化时 Invalidate，tick 路径零 GDI 新建
            ring.SetValue(remaining, total);
        }

        public void ShowRest()
        {
            try
            {
                this.Opacity = 0.02;
                this.Visible = true;
                this.TopMost = true;
                // 不再抢前台焦点：强制挂靠前台线程的输入队列 + 抢占前台窗口，
                // 会打断用户的输入法组词（未上屏内容丢失）并重置光标/选择区。
                // 跳过键完全由 KeyboardGuard 全局钩子处理，不依赖窗体焦点。
                fadeTimer.Start();
            }
            catch
            {
                // 淡入失败也必须让遮罩可见，否则会出现"屏蔽了键盘却看不见界面"
                try { this.Visible = true; this.Opacity = TargetOpacity; } catch { }
            }
        }

        public void HideRest()
        {
            try { fadeTimer.Stop(); } catch { }
            try { this.Visible = false; } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (fadeTimer != null)
                {
                    try { fadeTimer.Stop(); } catch { }
                    fadeTimer.Dispose();
                    fadeTimer = null;
                }
                // 先断开子控件对背景 brush 的借用，再释放 brush，避免释放后残留一次重绘
                if (ring != null) ring.SetBackdrop(null, null, Rectangle.Empty, Size.Empty);
                if (bgBrush != null) { bgBrush.Dispose(); bgBrush = null; }
                if (glowBrush != null) { glowBrush.Dispose(); glowBrush = null; }
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------
        // 环形进度面板：4 个 Pen 全部构造期建好（护眼/番茄各一组），
        // 每秒只改两个 int + Invalidate，tick 路径零 GDI 新建。
        // ------------------------------------------------------------
        private class RingMeter : Panel
        {
            private readonly Pen trackPen;
            private readonly Pen progPen;
            private readonly Pen trackPenPomo;    // 番茄钟暖灰底环
            private readonly Pen progPenPomo;     // 番茄钟暖橙进度弧
            private bool pomodoroMode;            // false = 护眼（默认）
            private readonly float lineWidth;
            private readonly int diameter;
            private readonly float dpiScale;
            private int valueFontPx;          // 随文本长度降档，OnPaint 只读

            private int remaining;
            private int total;
            private string valueText = "0";   // 只在数值变化时重拼，OnPaint 里零分配

            // 这两个 brush 属于 RestForm（由 RestForm 的 Dispose 释放），
            // 本控件只借用引用做背景复刻，绝不 Dispose，避免双重释放。
            private LinearGradientBrush backdropBrush;
            private PathGradientBrush glowBrush;
            private Rectangle glowRect;
            private Size backdropSize;

            public RingMeter(int diameter, float scale)
            {
                this.diameter = diameter;
                this.dpiScale = (scale > 0.5f && scale < 4f) ? scale : 1f;

                // 兜底底色：SetBackdrop 之前、或父窗体 brush 构造失败时也是深色，
                // 不会闪出控件默认的浅灰底。
                this.BackColor = Ui.GradTop;

                // 底环线宽 max(3, D*0.012)：324 -> 3.9；进度弧再粗 1px
                float lw = (float)(diameter * 0.012);
                if (lw < 3f) lw = 3f;
                lineWidth = lw;

                // 环心数字像素字号 = D*0.30：324 -> 97
                int px = (int)(diameter * 0.30);
                if (px < 20) px = 20;
                valueFontPx = px;

                this.SetStyle(ControlStyles.AllPaintingInWmPaint |
                              ControlStyles.UserPaint |
                              ControlStyles.OptimizedDoubleBuffer, true);
                this.TabStop = false;

                trackPen = new Pen(Ui.RingTrack, lineWidth);
                progPen = new Pen(Ui.Accent, lineWidth + 1f);
                progPen.StartCap = LineCap.Round;
                progPen.EndCap = LineCap.Round;

                // 番茄钟一套暖橙，线宽算法与护眼组完全一致（尺寸不变，只换颜色）
                trackPenPomo = new Pen(Ui.RingTrackPomodoro, lineWidth);
                progPenPomo = new Pen(Ui.AccentPomodoro, lineWidth + 1f);
                progPenPomo.StartCap = LineCap.Round;
                progPenPomo.EndCap = LineCap.Round;
            }

            /// <summary>切换护眼 / 番茄钟配色。只换画笔选择 + 一次重绘，不新建任何 GDI。</summary>
            public void SetPomodoro(bool on)
            {
                if (pomodoroMode == on) return;
                pomodoroMode = on;
                this.Invalidate();
            }

            private int Sc(int px)
            {
                return (int)(px * dpiScale + 0.5f);   // 不用 Math.Round：避免银行家舍入的 ±1px 抖动
            }

            /// <summary>
            /// 借用父窗体的背景 brush（含光晕），在本控件坐标系里复刻父窗体背景，
            /// 这样既没有透明子控件的坑，也不会出现纯色方块的硬边。
            /// 两个 brush 的所有权仍归 RestForm。
            /// </summary>
            public void SetBackdrop(LinearGradientBrush bg, PathGradientBrush glow, Rectangle glowR, Size parentSize)
            {
                backdropBrush = bg;
                glowBrush = glow;
                glowRect = glowR;
                backdropSize = parentSize;
            }

            /// <summary>每秒调用一次：值没变就完全不重绘，字符串也只在变化时拼。</summary>
            public void SetValue(int rem, int tot)
            {
                if (rem < 0) rem = 0;
                if (tot < 0) tot = 0;
                if (rem == remaining && tot == total) return;
                remaining = rem;
                total = tot;

                string text = FormatValue(rem);
                valueText = text;

                // 长文本降档：番茄休息上限 120 分钟 -> "120:00" 六字符，
                // 97px Consolas 下约 320px，超过 textW（297）会两侧被裁。
                int d = diameter > 8 ? diameter : Math.Min(this.Width, this.Height);
                int px = (int)(d * 0.30);
                if (px < 20) px = 20;
                if (text.Length > 5)
                {
                    px = (int)(d * 0.24);
                    if (px < 16) px = 16;
                }
                valueFontPx = px;

                Invalidate();
            }

            private static string FormatValue(int rem)
            {
                if (rem >= 60) return (rem / 60) + ":" + (rem % 60).ToString("00");
                return rem.ToString();
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                if (backdropBrush == null || backdropSize.Width <= 0 || backdropSize.Height <= 0)
                {
                    base.OnPaintBackground(e);
                    return;
                }

                Graphics g = e.Graphics;
                // brush 是按父窗体坐标 (0,0,w,h) / glowRect 定义的，这里把世界坐标平移到父窗体原点。
                // 用两次 TranslateTransform（正/反）而不是 g.Transform 存取，避免每秒 new 一个 Matrix。
                try
                {
                    g.TranslateTransform((float)(-this.Left), (float)(-this.Top));
                    g.FillRectangle(backdropBrush, 0, 0, backdropSize.Width, backdropSize.Height);
                    if (glowBrush != null) g.FillEllipse(glowBrush, glowRect);
                }
                catch
                {
                    try { g.Clear(this.BackColor); } catch { }
                }
                finally
                {
                    try { g.TranslateTransform((float)this.Left, (float)this.Top); } catch { }
                }
            }

            /// <summary>环 + 环心数字。用到的 Pen 和 Font 全部是构造期/静态缓存，本方法内零 new。</summary>
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                int d = Math.Min(this.Width, this.Height);
                if (d <= 8) d = diameter;
                if (d <= 8) return;

                int inset = (int)Math.Ceiling(lineWidth / 2.0) + 1;
                int ringSize = d - inset * 2;
                if (ringSize <= 8) return;

                Rectangle trackRect = new Rectangle(inset, inset, ringSize, ringSize);

                // 按模式选画笔：两组 Pen 都是构造期建好的，这里只是取引用，零新建
                Pen useTrack = pomodoroMode ? trackPenPomo : trackPen;
                Pen useProg = pomodoroMode ? progPenPomo : progPen;

                g.DrawEllipse(useTrack, trackRect);

                if (total > 0 && remaining > 0)
                {
                    float sweep = 359.9f * (float)remaining / (float)total;
                    if (sweep > 359.9f) sweep = 359.9f;   // GDI+ 画 360° 会退化成空白
                    if (sweep > 0f) g.DrawArc(useProg, trackRect, -90f, sweep);
                }

                // ---- 环心：数字（Consolas 等宽）+ 语义小字 ----
                int textW = d - (int)(lineWidth * 2f) - Sc(20);
                if (textW < 40) textW = 40;
                int textH = (int)(d * 0.34);   // 0.42 会让数字与「剩余」拉开 45px，不像一个整体
                int textX = (d - textW) / 2;
                int textY = (int)((d - textH) / 2.0 - d * 0.06);

                Rectangle valueRect = new Rectangle(textX, textY, textW, textH);
                TextRenderer.DrawText(g, valueText, Ui.GetTimeFont(valueFontPx), valueRect, Ui.TimeText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                Rectangle labelRect = new Rectangle(textX, textY + textH, textW, Sc(22));
                Color useLabel = pomodoroMode ? Ui.RingLabelPomodoro : Ui.RingLabel;
                TextRenderer.DrawText(g, "剩余", Ui.FontRingLabel, labelRect, useLabel,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    // 四个 Pen 全部在这里释放（护眼组 + 番茄钟组）
                    trackPen.Dispose();
                    progPen.Dispose();
                    trackPenPomo.Dispose();
                    progPenPomo.Dispose();
                    // backdropBrush / glowBrush 是借用，不在这里释放
                    backdropBrush = null;
                    glowBrush = null;
                }
                base.Dispose(disposing);
            }
        }
    }

    // ================================================================
    // 6. 设置窗体（浅色卡片式）
    // ================================================================
    internal class SettingsForm : Form
    {
        private NumericUpDown numWork, numRest, numPomoWork, numPomoRest, numPostpone, numIdle;
        private CheckBox chkPomodoro, chkAllowSkip, chkSound, chkStrict, chkAutoStart;

        // 版式常量（以下全部是 96dpi 下的设计值，运行时一律经 Sc() 乘 DPI 因子）
        // 设计基准客户区 520 x 692
        private const int FormW = 520;
        private const int FormH = 692;
        private const int CardLeft = 24;
        private const int CardWidth = 472;      // 520 - 24 * 2
        private const int CardPad = 22;         // 卡片左右内边距
        private const int CardTitleTop = 14;
        private const int FirstRowTop = 38;
        private const int RowHeight = 32;       // 所有行（复选 / 数值）统一 32
        private const int CheckHeight = 26;
        private const int NumWidth = 96;
        private const int NumHeight = 30;
        private const int Gap = 14;

        // DPI 缩放因子（1.0 = 96dpi）。字体是 pt 会随 DPI 放大，盒子必须同比放大，
        // 否则"字体高度 : 盒子高度"随 DPI 变化，文字就被裁。
        private float dpiScale = 1f;

        private int Sc(int px)
        {
            return (int)(px * dpiScale + 0.5f);   // 不用 Math.Round：避免银行家舍入的 ±1px 抖动
        }

        public SettingsForm(AppConfig cfg)
        {
            // 必须在算任何版式 / 设 ClientSize 之前取到 DPI 因子
            dpiScale = Ui.DetectScale(this);

            this.Text = "护眼小助手 - 设置";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = Ui.FontUi;
            this.BackColor = Ui.PageBg;
            this.AutoScaleMode = AutoScaleMode.None;

            // ---------- 客户区尺寸：高 DPI + 小屏时钳制并开 AutoScroll ----------
            // 典型场景：笔记本内屏 1080p @150% -> 窗口 1068 高于工作区 1032，
            // 底部「保存」会被任务栏盖住。钳高 + 滚动保证按钮永远可达；
            // 放得下时 AutoScroll 不显示滚动条，完全无感。
            int clientW = Sc(FormW);
            int clientH = Sc(FormH);
            int maxH;
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                maxH = wa.Height - Sc(56);          // 预留标题栏 + 边距
                if (maxH < Sc(320)) maxH = Sc(320);
            }
            catch { maxH = clientH; }

            if (clientH > maxH)
            {
                this.AutoScroll = true;
                // 宽度传 0：让水平范围跟随客户区宽度。
                // 若把宽度也设成 clientW，垂直滚动条吃掉约 17px 后内容反而"超宽"，会多出一条横向滚动条。
                this.AutoScrollMinSize = new Size(0, clientH);
                clientH = maxH;
            }

            this.ClientSize = new Size(clientW, clientH);

            // ---------- 页头：直接画在窗体上 ----------
            // 不再用固定高度的 Panel 装标题：Panel 会裁掉超出边界的子控件。
            // 窗体本身就是画布，不存在裁剪边界；盒子高度已按 DPI 放大。
            Label lblEyebrow = new Label();
            lblEyebrow.Text = "偏好设置";
            lblEyebrow.Font = Ui.FontEyebrow;
            lblEyebrow.ForeColor = Ui.CaptionText;
            lblEyebrow.BackColor = Ui.PageBg;
            lblEyebrow.AutoSize = false;
            lblEyebrow.TextAlign = ContentAlignment.MiddleLeft;
            // 9pt 行高约 16px（96dpi），盒子给 18 避免下缘被切 1px（视觉位置不变）
            lblEyebrow.SetBounds(Sc(CardLeft), Sc(25), Sc(240), Sc(18));
            this.Controls.Add(lblEyebrow);

            Label lblTitle = new Label();
            lblTitle.Text = "护眼小助手";
            lblTitle.Font = Ui.FontTitle;
            lblTitle.ForeColor = Ui.TitleText;
            lblTitle.BackColor = Ui.PageBg;
            lblTitle.AutoSize = false;
            lblTitle.TextAlign = ContentAlignment.MiddleLeft;
            lblTitle.SetBounds(Sc(CardLeft), Sc(48), Sc(340), Sc(28));
            this.Controls.Add(lblTitle);

            // ---------- 卡片 1：节奏（2 行）----------
            int y = Sc(88);
            RoundPanel card1 = AddCard(y, Sc(112), "节奏");
            numWork = AddRow(card1, Sc(FirstRowTop), "工作时长（分钟）", 1, 240, cfg.WorkMinutes);
            numRest = AddRow(card1, Sc(FirstRowTop) + Sc(RowHeight), "休息时长（秒）", 5, 1800, cfg.RestSeconds);
            y += Sc(112) + Sc(Gap);

            // ---------- 卡片 2：番茄钟（3 行）----------
            RoundPanel card2 = AddCard(y, Sc(144), "番茄钟");
            chkPomodoro = AddCheck(card2, Sc(FirstRowTop), "启用番茄钟模式", cfg.PomodoroEnabled);
            numPomoWork = AddRow(card2, Sc(FirstRowTop) + Sc(RowHeight), "番茄工作（分钟）", 1, 240, cfg.PomodoroWork);
            numPomoRest = AddRow(card2, Sc(FirstRowTop) + Sc(RowHeight) * 2, "番茄休息（分钟）", 1, 120, cfg.PomodoroRest);
            y += Sc(144) + Sc(Gap);

            // ---------- 卡片 3：行为（6 行）----------
            RoundPanel card3 = AddCard(y, Sc(240), "行为");
            chkAllowSkip = AddCheck(card3, Sc(FirstRowTop), "允许跳过 / 推迟休息", cfg.AllowSkip);
            numPostpone = AddRow(card3, Sc(FirstRowTop) + Sc(RowHeight), "推迟时长（分钟）", 1, 60, cfg.PostponeMinutes);
            numIdle = AddRow(card3, Sc(FirstRowTop) + Sc(RowHeight) * 2, "离开多少分钟后暂停计时", 0, 120, cfg.IdleMinutes);
            chkSound = AddCheck(card3, Sc(FirstRowTop) + Sc(RowHeight) * 3, "开始 / 结束都播放提示音", cfg.SoundEnabled);
            chkStrict = AddCheck(card3, Sc(FirstRowTop) + Sc(RowHeight) * 4,
                                 "严格模式：休息时屏蔽整个键盘（Esc / 空格可跳过）", cfg.StrictMode);
            chkAutoStart = AddCheck(card3, Sc(FirstRowTop) + Sc(RowHeight) * 5,
                                    "开机自动启动", cfg.StartWithWindows);

            // ---------- 底部按钮：最后一排卡片底部 + Sc(24) ----------
            int btnY = y + Sc(240) + Sc(24);

            RoundButton ok = new RoundButton("保存", Sc(8), Ui.PrimaryBg, Ui.PrimaryHover,
                                             Color.White, Color.Empty, 0);
            ok.Font = Ui.FontRowLabel;
            ok.SetBounds(Sc(FormW) - Sc(24) - Sc(128), btnY, Sc(128), Sc(38));
            ok.DialogResult = DialogResult.OK;
            ok.Click += delegate { ApplyTo(cfg); };
            this.Controls.Add(ok);

            RoundButton cancel = new RoundButton("取消", Sc(8), Ui.CardBg, Ui.SecondaryHover,
                                                 Ui.LabelText, Ui.SecondaryBorder, Math.Max(1, Sc(1)));
            cancel.Font = Ui.FontRowLabel;
            cancel.SetBounds(Sc(FormW) - Sc(24) - Sc(128) - Sc(10) - Sc(96), btnY, Sc(96), Sc(38));
            cancel.DialogResult = DialogResult.Cancel;
            this.Controls.Add(cancel);

            // 严格模式的实际效果取决于「允许跳过」：
            //   能跳过时 → 全吞键盘（Esc/空格就是出口）
            //   禁跳时   → 不能全吞（否则无出口死锁），只能拦系统键
            // 两个开关的名字看不出这层关系，必须用文案说清（行为不变，只改文字）。
            chkAllowSkip.CheckedChanged += delegate { UpdateStrictText(); };
            UpdateStrictText();

            this.AcceptButton = ok;
            this.CancelButton = cancel;
        }

        /// <summary>
        /// 严格模式的说明文字随「允许跳过」联动。
        /// 构造期间控件可能还没建好，必须判空。
        /// </summary>
        private void UpdateStrictText()
        {
            if (chkStrict == null || chkAllowSkip == null) return;

            chkStrict.Text = chkAllowSkip.Checked
                ? "严格模式：休息时屏蔽整个键盘（Esc / 空格可跳过）"
                : "严格模式：休息时只拦系统键（键盘仍可用）";
        }

        private RoundPanel AddCard(int top, int height, string title)
        {
            RoundPanel card = new RoundPanel(Sc(12), Ui.CardBg, Ui.CardBorder);
            card.SetBounds(Sc(CardLeft), top, Sc(CardWidth), height);
            this.Controls.Add(card);

            Label lt = new Label();
            lt.Text = title;
            lt.Font = Ui.FontEyebrow;
            lt.ForeColor = Ui.CaptionText;
            lt.BackColor = Ui.CardBg;               // 卡片自绘，不用透明色，避免露出父容器色
            lt.AutoSize = false;
            lt.TextAlign = ContentAlignment.MiddleLeft;
            lt.SetBounds(Sc(CardPad), Sc(CardTitleTop), Sc(220), Sc(18));
            card.Controls.Add(lt);

            return card;
        }

        private NumericUpDown AddRow(RoundPanel card, int top, string text, int min, int max, int val)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Ui.FontRowLabel;
            l.ForeColor = Ui.LabelText;
            l.BackColor = Ui.CardBg;
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.SetBounds(Sc(CardPad), top + Sc(4), Sc(250), Sc(24));
            card.Controls.Add(l);

            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = val;
            n.Font = Ui.FontRowLabel;
            n.BorderStyle = BorderStyle.FixedSingle;
            n.TextAlign = HorizontalAlignment.Right;
            n.AutoSize = false;
            n.SetBounds(Sc(CardWidth) - Sc(CardPad) - Sc(NumWidth) - 1, top + Sc(1),
                        Sc(NumWidth), Sc(NumHeight));
            card.Controls.Add(n);
            return n;
        }

        private CheckBox AddCheck(RoundPanel card, int top, string text, bool val)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = val;
            c.Font = Ui.FontRowLabel;
            c.ForeColor = Ui.LabelText;
            c.BackColor = Ui.CardBg;                // 必须显式设成卡片白底
            c.AutoSize = false;
            // Sc(32) 的行槽里放 Sc(26) 高的复选行，下压 Sc(3) 与相邻数值行视觉居中对齐
            c.SetBounds(Sc(CardPad), top + Sc(3), Sc(CardWidth) - Sc(CardPad) * 2, Sc(CheckHeight));
            card.Controls.Add(c);
            return c;
        }

        private void ApplyTo(AppConfig cfg)
        {
            cfg.WorkMinutes = (int)numWork.Value;
            cfg.RestSeconds = (int)numRest.Value;
            cfg.PomodoroEnabled = chkPomodoro.Checked;
            cfg.PomodoroWork = (int)numPomoWork.Value;
            cfg.PomodoroRest = (int)numPomoRest.Value;
            cfg.AllowSkip = chkAllowSkip.Checked;
            cfg.PostponeMinutes = (int)numPostpone.Value;
            cfg.IdleMinutes = (int)numIdle.Value;
            cfg.SoundEnabled = chkSound.Checked;
            cfg.StrictMode = chkStrict.Checked;
            cfg.StartWithWindows = chkAutoStart.Checked;
        }
    }

    // ================================================================
    // 7. 主控
    // ================================================================
    internal class TrayApp : ApplicationContext
    {
        private AppConfig cfg;
        private NotifyIcon tray;
        private ToolStripMenuItem miPause;
        private ToolStripMenuItem miAutoStart;
        private System.Windows.Forms.Timer timer;

        private bool paused = false;
        private bool isResting = false;
        private bool strictWarned = false;    // 严格模式降级提示只弹一次
        private int workRemaining = 0;
        private int restRemaining = 0;
        private int restTotal = 0;
        private int lastShown = -1;

        // 休息开始时的前台窗口；休息结束若发现前台被我们占了就还回去（安全网）
        private IntPtr savedForeground = IntPtr.Zero;

        private List<RestForm> restForms = new List<RestForm>();
        private string screenSignature = "";
        private Icon iconIdle;
        private Icon iconRest;

        public TrayApp()
        {
            cfg = AppConfig.Load();

            iconIdle = MakeIcon(Ui.Accent);
            iconRest = MakeIcon(Color.FromArgb(240, 128, 72));

            tray = new NotifyIcon();
            tray.Icon = iconIdle;
            tray.Text = "护眼小助手";
            tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = Ui.FontMenu;

            ToolStripMenuItem miRest = new ToolStripMenuItem("立即休息");
            miRest.Click += delegate { IfWorkingStartRest(); };
            menu.Items.Add(miRest);

            miPause = new ToolStripMenuItem("暂停计时");
            miPause.Click += delegate { TogglePause(); };
            menu.Items.Add(miPause);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miSettings = new ToolStripMenuItem("设置…");
            miSettings.Click += delegate { ShowSettings(); };
            menu.Items.Add(miSettings);

            miAutoStart = new ToolStripMenuItem("开机自动启动");
            miAutoStart.Checked = cfg.StartWithWindows;
            miAutoStart.Click += delegate
            {
                miAutoStart.Checked = !miAutoStart.Checked;
                SetAutoStart(miAutoStart.Checked);
            };
            menu.Items.Add(miAutoStart);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.Add(miExit);

            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { IfWorkingStartRest(); };

            if (cfg.StartWithWindows) EnsureAutoStartRegistered(true);

            ResetWork();

            // 定时器最后启动，保证初始化完成前不触发 tick
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += OnTick;
            timer.Start();
        }

        // ---------- 计时 ----------
        private int WorkTotalSeconds
        {
            get { return (cfg.PomodoroEnabled ? cfg.PomodoroWork : cfg.WorkMinutes) * 60; }
        }

        private int RestTotalSeconds
        {
            get { return cfg.PomodoroEnabled ? cfg.PomodoroRest * 60 : cfg.RestSeconds; }
        }

        private void ResetWork()
        {
            workRemaining = WorkTotalSeconds;
            UpdateTrayText();
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                // drain 收尾双保险：keyup 事件可能丢失（secure desktop 切换 / RDP 断连 /
                // 拔键盘），这里一旦发现键其实已经物理抬起就立刻收尾，不必干等 2 秒定时器。
                try
                {
                    if (KeyboardGuard.IsDraining && !KeyboardGuard.IsDrainKeyDown)
                        KeyboardGuard.ForceUninstall();
                }
                catch { }

                // 空闲暂停只在"工作"阶段生效：休息倒计时必须一直走。
                // （paused 不会冻住休息——TogglePause 在休息期间直接拒绝。）
                if (paused || (!isResting && IsIdleTooLong()))
                {
                    UpdateTrayText();
                    return;
                }

                if (isResting)
                {
                    restRemaining--;
                    if (restRemaining <= 0) { EndRest(true); return; }
                    UpdateRestUI();

                    // 每秒重申一次置顶：TopMost + WS_EX_TOOLWINDOW 只保证层级，
                    // 压不住别的 TopMost 窗口（播放器小窗、录屏、部分 Electron 应用）。
                    // 遮罩被盖住 = 提醒静默失效，用户以为程序坏了。
                    // SWP_NOACTIVATE 是铁律：少了它就会抢焦点，破坏"不打断输入法组词"。
                    for (int i = 0; i < restForms.Count; i++)
                    {
                        try
                        {
                            RestForm f = restForms[i];
                            // 重建/释放过的窗体可能还在列表里被遍历到，
                            // 对已释放窗体调 SetWindowPos 会抛 ObjectDisposedException。
                            if (f == null || f.IsDisposed || !f.IsHandleCreated) continue;

                            SetWindowPos(f.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                        }
                        catch { }
                    }
                }
                else
                {
                    workRemaining--;
                    if (workRemaining <= 0) { BeginRest(); return; }
                    UpdateTrayText();
                }
            }
            catch
            {
                // 自愈：显示器热插拔 / GDI 资源耗尽等异常不能冒泡到 ThreadException，
                // 也不能把用户留在"钩子在跑、遮罩没出来"的状态里。
                try { isResting = false; } catch { }
                try { KeyboardGuard.OnSkipKey = null; } catch { }
                try { KeyboardGuard.Uninstall(); } catch { }
                try
                {
                    for (int i = 0; i < restForms.Count; i++)
                    {
                        try { restForms[i].HideRest(); } catch { }
                    }
                }
                catch { }
                try { tray.Icon = iconIdle; } catch { }
                try { ResetWork(); } catch { }
            }
        }

        private void BeginRest()
        {
            // 记下当时的前台窗口，休息结束时若前台被我们占了就还回去。
            // 遮罩是 WS_EX_NOACTIVATE，理论上不会抢，这是点击按钮等情况下的保险。
            try { savedForeground = GetForegroundWindow(); }
            catch { savedForeground = IntPtr.Zero; }

            isResting = true;
            restTotal = RestTotalSeconds;
            restRemaining = restTotal;
            lastShown = -1;

            EnsureRestForms();

            bool pomo = cfg.PomodoroEnabled;
            string eyebrow = pomo ? "番茄钟休息" : "休息一下";
            string hint = pomo
                ? "这一轮专注结束了，起身活动一下"
                : "让眼睛看看 6 米外的东西，放松睫状肌";

            for (int i = 0; i < restForms.Count; i++)
            {
                RestForm f = restForms[i];
                f.SetPomodoro(pomo);          // 必须在 SetHint / ShowRest 之前（换配色与副提示）
                f.SetHint(eyebrow, hint);
                f.ShowRest();
            }

            tray.Icon = iconRest;
            if (cfg.SoundEnabled) Sfx.PlayStart();

            // 跳过键由全局钩子兜底（遮罩不抢焦点，KeyDown 永远收不到键）。
            // AllowSkip=false 时不挂回调 —— 否则"禁止跳过"会被这两个键绕过。
            if (cfg.AllowSkip) KeyboardGuard.OnSkipKey = delegate { EndRest(true); };
            else KeyboardGuard.OnSkipKey = null;

            // 钩子有三个维度：跳过键（Esc/空格）、屏蔽系统键、全吞键盘。
            //
            //   AllowSkip | StrictMode | 结果
            //   ----------+------------+----------------------------------------------
            //   true      | true       | 全吞键盘，Esc/空格可跳过（默认组合）
            //   true      | false      | 只吞 Esc/空格（用于跳过），其余键照常输入
            //   false     | true       | 只吞 Win/Tab/Alt+F4/Ctrl+Esc，其余照常
            //   false     | false      | 不装钩子，零开销
            //
            // blockAll 只在"有跳过出口"（AllowSkip=true）时才开：
            // AllowSkip=false 时若也全吞键盘，用户既不能跳过也不能打字，
            // 只剩 Ctrl+Alt+Del 一条路，太危险。
            bool needHook = cfg.AllowSkip || cfg.StrictMode;
            if (needHook)
            {
                bool blockAll = cfg.StrictMode && cfg.AllowSkip;
                if (!KeyboardGuard.Install(cfg.StrictMode, blockAll)) WarnStrictUnavailable();
            }

            UpdateRestUI();
            UpdateTrayText();
        }

        /// <summary>
        /// 钩子没装上时的降级：只弹一次托盘气泡说明后果，不再尝试抢前台
        ///（抢焦点会打断用户输入法组词）。倒计时本身仍会正常结束休息。
        /// </summary>
        private void WarnStrictUnavailable()
        {
            if (strictWarned) return;
            strictWarned = true;

            // 钩子同时承担"跳过键"和"屏蔽系统键"两件事，失败时哪个受影响取决于配置
            string msg = cfg.AllowSkip
                ? "键盘钩子安装失败：休息时 Esc / 空格可能无法跳过，请用鼠标点「跳过」按钮。"
                : "严格模式未生效（键盘钩子安装失败），Win / Alt+Tab 可能仍然可用。";

            try { tray.ShowBalloonTip(4000, "护眼小助手", msg, ToolTipIcon.Warning); }
            catch { }
        }

        private void EndRest(bool resetWork)
        {
            if (!isResting) return;            // 重入守卫：钩子 / 窗体 KeyDown / 按钮可能重复触发
            isResting = false;

            // 计时状态必须立刻复位：即使后面任何一步 UI 操作抛异常，
            // 也不会出现 workRemaining <= 0 导致下一秒再次 BeginRest 的"休息死循环"。
            if (resetWork)
            {
                try { ResetWork(); } catch { }
            }

            // 走 RequestUninstall：如果用户还按着跳过键，等它的 keyup 再真正卸载
            //（否则后续 key-repeat 会漏进下层文档），2 秒兜底强制卸载。
            try { KeyboardGuard.RequestUninstall(); } catch { }
            KeyboardGuard.OnSkipKey = null;   // 卸载后再清，避免静态字段长期持有 TrayApp

            for (int i = 0; i < restForms.Count; i++)
            {
                try { restForms[i].HideRest(); } catch { }
            }

            try { tray.Icon = iconIdle; } catch { }
            try { if (cfg.SoundEnabled) Sfx.PlayEnd(); } catch { }
            try { UpdateTrayText(); } catch { }

            // 安全网：前台若被我们占了，还回休息前的那个窗口
            try
            {
                if (savedForeground != IntPtr.Zero)
                {
                    IntPtr cur = GetForegroundWindow();
                    if (cur != savedForeground)
                    {
                        uint pid;
                        GetWindowThreadProcessId(cur, out pid);
                        // 用 P/Invoke 取本进程 Id：Process.GetCurrentProcess() 每次都会
                        // 新建一个 Process 对象并持有内核句柄，这里是每次休息都会走到的路径。
                        if (pid == GetCurrentProcessId())
                            SetForegroundWindow(savedForeground);   // 只有被我们占了才还回去
                    }
                }
            }
            catch { }
            savedForeground = IntPtr.Zero;
        }

        private void IfWorkingStartRest()
        {
            if (isResting) return;
            if (paused) TogglePause();
            BeginRest();
        }

        // ---------- 窗体复用 ----------
        private static string BuildScreenSignature()
        {
            StringBuilder sb = new StringBuilder();
            Screen[] s = Screen.AllScreens;
            for (int i = 0; i < s.Length; i++)
            {
                sb.Append(s[i].DeviceName);
                sb.Append(s[i].Bounds.ToString());
                sb.Append(';');
            }
            return sb.ToString();
        }

        private void EnsureRestForms()
        {
            string sig = BuildScreenSignature();
            if (sig == screenSignature && restForms.Count > 0) return;

            // 只有在显示器配置 / 设置参数变化时才重建（正常情况整个生命周期只建一次）
            for (int i = 0; i < restForms.Count; i++)
            {
                try { restForms[i].Close(); restForms[i].Dispose(); } catch { }
            }
            restForms.Clear();

            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                // 所有屏幕都给按钮：只在主屏给按钮会让副屏内容块更矮，
                // 两块屏的垂直居中对不齐，观感很差。
                bool withButtons = true;
                RestForm f = new RestForm(screens[i], cfg.AllowSkip, cfg.PostponeMinutes, withButtons);
                f.RequestSkip += delegate { EndRest(true); };
                f.RequestPostpone += delegate
                {
                    EndRest(false);
                    workRemaining = cfg.PostponeMinutes * 60;
                    UpdateTrayText();
                };
                restForms.Add(f);
            }
            screenSignature = sig;
        }

        private void UpdateRestUI()
        {
            if (restRemaining == lastShown) return;
            lastShown = restRemaining;
            for (int i = 0; i < restForms.Count; i++)
            {
                try { restForms[i].UpdateView(restRemaining, restTotal); }
                catch { }
            }
        }

        private void UpdateTrayText()
        {
            try
            {
                string s;
                if (paused) s = "已暂停 · 右键可恢复";
                else if (isResting) s = "休息中 " + restRemaining + " 秒";
                else if (IsIdleTooLong()) s = "检测到离开，计时已暂停";
                else
                {
                    s = "工作剩余 " + (workRemaining / 60) + ":" + (workRemaining % 60).ToString("00")
                        + (cfg.PomodoroEnabled ? " · 番茄钟" : "");
                }
                tray.Text = s;
            }
            catch { }
        }

        private void TogglePause()
        {
            // 休息期间禁止暂停：否则休息倒计时被冻结，若又 AllowSkip=false（无回调 + 无按钮），
            // Esc / 空格 / 按钮全部失效，用户会被钉死在 TopMost 全屏遮罩上。
            if (isResting)
            {
                try
                {
                    tray.ShowBalloonTip(3000, "护眼小助手",
                        "休息计时不允许暂停，休息结束后可以再暂停。", ToolTipIcon.Info);
                }
                catch { }
                return;
            }

            paused = !paused;
            miPause.Text = paused ? "继续计时" : "暂停计时";
            UpdateTrayText();
        }

        // ---------- 空闲检测 ----------
        private bool IsIdleTooLong()
        {
            if (cfg.IdleMinutes <= 0) return false;
            try
            {
                LASTINPUTINFO lii = new LASTINPUTINFO();
                lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref lii)) return false;
                uint idleMs = unchecked((uint)Environment.TickCount) - lii.dwTime;
                return idleMs >= (uint)(cfg.IdleMinutes * 60 * 1000);
            }
            catch { return false; }
        }

        // ---------- 设置 ----------
        private void ShowSettings()
        {
            using (SettingsForm f = new SettingsForm(cfg))
            {
                if (f.ShowDialog() == DialogResult.OK)
                {
                    cfg.Save();
                    EnsureAutoStartRegistered(cfg.StartWithWindows);
                    miAutoStart.Checked = cfg.StartWithWindows;

                    // 参数变了，下次休息前重建窗体（按钮文案/是否可跳过会变）
                    screenSignature = "";
                    paused = false;
                    miPause.Text = "暂停计时";
                    if (!isResting) ResetWork();
                }
            }
        }

        // ---------- 开机自启 ----------
        private void SetAutoStart(bool on)
        {
            cfg.StartWithWindows = on;
            cfg.Save();
            EnsureAutoStartRegistered(on);
        }

        private static void EnsureAutoStartRegistered(bool on)
        {
            try
            {
                string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(runKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("EyeLite", "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue("EyeLite") != null) k.DeleteValue("EyeLite", false);
                }
            }
            catch { }
        }

        // ---------- 图标 ----------
        private static Icon MakeIcon(Color color)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush b = new SolidBrush(color)) g.FillEllipse(b, 1, 1, 30, 30);
                using (SolidBrush w = new SolidBrush(Color.White)) g.FillEllipse(w, 9, 12, 14, 9);
                using (SolidBrush d = new SolidBrush(color)) g.FillEllipse(d, 14, 14, 4, 4);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        // ---------- 退出 ----------
        private void ExitApp()
        {
            try { timer.Stop(); } catch { }
            KeyboardGuard.Uninstall();
            KeyboardGuard.OnSkipKey = null;
            for (int i = 0; i < restForms.Count; i++)
            {
                try { restForms[i].Close(); restForms[i].Dispose(); } catch { }
            }
            restForms.Clear();
            try { tray.Visible = false; tray.Dispose(); } catch { }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                KeyboardGuard.Uninstall();
                KeyboardGuard.OnSkipKey = null;
                for (int i = 0; i < restForms.Count; i++)
                {
                    try { restForms[i].Dispose(); } catch { }
                }
                restForms.Clear();
                if (timer != null) timer.Dispose();
                if (tray != null) tray.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- P/Invoke ----------
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        // 前台窗口的保存 / 判定 / 归还（遮罩本身是 WS_EX_NOACTIVATE，不该抢焦点）
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // 休息期间每秒重申置顶用（不是 GDI 操作，不违反"每秒零 GDI 新建"）
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                                int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
    }

    // ================================================================
    // 8. 入口
    // ================================================================
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            bool createdNew = false;
            using (System.Threading.Mutex mutex =
                   new System.Threading.Mutex(true, "EyeLite_SingleInstance_v1", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("护眼小助手已经在运行了，请看任务栏右下角托盘。",
                                    "护眼小助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try { SetProcessDPIAware(); } catch { }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Application.Run 之前 SynchronizationContext.Current 往往是 null
                // （WinForms 是在消息循环/首个控件创建时才装它）。这里是钩子回调
                // 投递 UI 工作的唯一通道，必须显式装好，否则 Post 会静默丢委托，
                // 表现为"按了 Esc 也没反应"。
                try
                {
                    if (System.Threading.SynchronizationContext.Current == null)
                    {
                        System.Threading.SynchronizationContext.SetSynchronizationContext(
                            new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    }
                    Ui.Context = System.Threading.SynchronizationContext.Current;
                }
                catch { }

                Application.Run(new TrayApp());
            }
        }
    }
}
