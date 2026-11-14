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
        // 输入框底色/描边：数值框改成无边框后靠 RoundPanel 画圆角描边，
        // 底色与卡片白底（#FFFFFF）拉开一档，让"可输入"这件事有一个安静的边界。
        public static readonly Color InputBg = Color.FromArgb(0xF7, 0xF9, 0xFC);       // #F7F9FC
        public static readonly Color InputBorder = Color.FromArgb(0xE4, 0xEA, 0xF2);   // #E4EAF2

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
            // 第 1 步：GDI 屏幕 DC + GetDeviceCaps。
            //    **不能用 Graphics.FromHwnd(IntPtr.Zero).DpiX**：GDI+ 在进程启动时把屏幕
            //    DPI 缓存进全局状态。Ui 是 beforefieldinit 类，那 9 个静态字体可能在
            //    Main 第一条语句之前就被初始化 —— 那时进程还没 SetProcessDPIAware()，
            //    GDI+ 记下的就是 96，且此后永久返回 96。而 GDI 文本（TextRenderer）
            //    用的是实时 DC，照样按 144 渲染 —— 结果是"布局按 96、文字按 144"，
            //    文字被裁掉一截（2026-11-14 实测，150% 机器）。
            //    GetDeviceCaps 不缓存，永远给实时值，且与控件是否建句柄无关。
            IntPtr screenDc = IntPtr.Zero;
            try
            {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc != IntPtr.Zero)
                {
                    int dpi = GetDeviceCaps(screenDc, LOGPIXELSX);
                    if (dpi >= 48 && dpi <= 384) return dpi / 96f;
                }
            }
            catch { }
            finally
            {
                if (screenDc != IntPtr.Zero) { try { ReleaseDC(IntPtr.Zero, screenDc); } catch { } }
            }

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

        private const int LOGPIXELSX = 88;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int index);

        /// <summary>设置面板上显示的版本号；发布时与 git tag 同步，只改这一处。</summary>
        public const string AppVersion = "v1.2.0";

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

    /// <summary>
    /// 自绘复选框：18px 圆角方块 + 文字。
    /// 目的是去掉 WinForms 主题的 3D 勾选框与方块底色，和设置页的扁平卡片对齐。
    /// 只画，不改交互：AutoCheck / 点击切换仍由 CheckBox 基类处理。
    /// </summary>
    internal class FlatCheckBox : CheckBox
    {
        // 方块固定 18px（不乘 DPI）：控件高度已由 AddCheck 按 DPI 给足，
        // 方块再随 DPI 放大会高出文字一大截，与"安静、精确"的调性相反。
        private const int BoxSize = 18;
        private const int BoxRadius = 4;
        private const int TextX = 26;       // 文字起点 = 方块 18 + 间距 8

        public FlatCheckBox()
        {
            this.SetStyle(ControlStyles.UserPaint |
                          ControlStyles.AllPaintingInWmPaint |
                          ControlStyles.OptimizedDoubleBuffer |
                          ControlStyles.ResizeRedraw, true);
            this.BackColor = Ui.CardBg;
        }

        // UserPaint 下 WinForms 不再自动重绘，状态变化必须自己触发一次
        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            this.Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            this.Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(this.BackColor);

            int boxY = (this.ClientSize.Height - BoxSize) / 2;
            if (boxY < 0) boxY = 0;
            Rectangle box = new Rectangle(0, boxY, BoxSize, BoxSize);

            // 所有 Path / Brush / Pen 都在 using 里，方法返回即释放（不新增 GDI 句柄泄漏点）
            if (this.Checked)
            {
                // 选中：深墨蓝实心 + 白色圆头对勾
                using (GraphicsPath path = Ui.RoundedRect(box, BoxRadius))
                using (SolidBrush fill = new SolidBrush(Ui.PrimaryBg))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(fill, path);
                }
                using (Pen check = new Pen(Color.White, 2f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    check.StartCap = LineCap.Round;
                    check.EndCap = LineCap.Round;
                    check.LineJoin = LineJoin.Round;
                    g.DrawLines(check, new Point[]
                    {
                        new Point(box.X + 4, box.Y + 9),
                        new Point(box.X + 7, box.Y + 13),
                        new Point(box.X + 14, box.Y + 5)
                    });
                }
            }
            else
            {
                // 未选中：白底 + 1px 浅描边（与输入框同一套线条色）
                using (GraphicsPath path = Ui.RoundedRect(box, BoxRadius))
                using (SolidBrush fill = new SolidBrush(Color.White))
                using (Pen edge = new Pen(Ui.InputBorder, 1f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(fill, path);
                    g.DrawPath(edge, path);
                }
            }

            // 文字沿用控件自身的 Font（由 AddCheck 赋 Ui.FontRowLabel），这里绝不新建 Font
            Color fore = this.Enabled ? Ui.LabelText : Ui.CaptionText;
            int textW = this.ClientSize.Width - TextX;
            if (textW < 1) textW = 1;
            Rectangle textRect = new Rectangle(TextX, 0, textW, this.ClientSize.Height);
            TextRenderer.DrawText(g, this.Text, this.Font, textRect, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                TextFormatFlags.EndEllipsis);
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

        // ---- A5 仪式音：一个短"基音"，重复次数表达紧迫度 ----
        //
        // 为什么单音而不是旋律：重复次数本身就是信息（1 声放松 / 3 声已经拖了三轮），
        // 音高固定才能让人"听一遍就认得"，不用再分神判断这是什么声音。
        // 180ms 的短音是为了能被 220ms 的间隔干净切开，不糊成一片。
        private static byte[] ritualWave = null;
        private static GCHandle ritualHandle;

        /// <summary>播一次仪式基音（D5，180ms）。合成与 pin 都只做一次，之后零分配。</summary>
        public static void PlayRitualOnce()
        {
            try
            {
                if (!ritualHandle.IsAllocated)
                {
                    if (ritualWave == null) ritualWave = BuildWave(new double[] { 587.33 }, 180, 0.18);
                    ritualHandle = GCHandle.Alloc(ritualWave, GCHandleType.Pinned);
                }
                if (!PlaySound(ritualHandle.AddrOfPinnedObject(), IntPtr.Zero,
                               SND_MEMORY | SND_ASYNC | SND_NODEFAULT))
                    System.Media.SystemSounds.Beep.Play();
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
        private static volatile bool active = false;

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
        // drainVk / drainRearms 被 timer 线程写、被钩子线程读，加 volatile 消除数据竞争。
        //（hookHandle 是 IntPtr，不能加 volatile，它只在 UI 线程与 ExitApp 里用，风险低。）
        private static volatile int drainVk = 0;
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
    // 3.6 单调时钟（契约 §4「时钟源」）
    // ================================================================
    /// <summary>
    /// 单调时钟锚点。
    ///
    /// 为什么不用 DateTime.Now：系统时间被往前调时差值会是负的，往后调则凭空多出时间。
    /// 为什么不用 Stopwatch 实例的 Start/Stop：语义易错（谁负责 Start、谁负责 Stop、
    /// 重复 Start 会抛异常），而且我们只要"两个时刻之间的差"。
    /// 为什么不用 tick 累加（旧的 workRemaining-- 写法）：Timer 迟到一次，计时就少一秒；
    /// 机器睡一觉醒来，计数器会悄悄落后或超前 —— 调研文档反模式 5 说的就是这个。
    ///
    /// 契约要求"单次 tick 内只取一次 NowMs()"：取两次会产生微小负差，钳到 0 会让秒数来回跳。
    /// </summary>
    internal static class Clock
    {
        /// <summary>把 Stopwatch 的 tick 频率换算成毫秒比例（静态只算一次）。</summary>
        private static readonly double msPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>当前单调时刻（毫秒）。只在进程内比较有效，不做绝对时间用途。</summary>
        public static long NowMs()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * msPerTick);
        }

        /// <summary>
        /// 两个单调时刻的差（毫秒），负数归零。
        /// </summary>
        public static long DeltaMs(long later, long earlier)
        {
            long d = later - earlier;
            return d < 0 ? 0 : d;
        }
    }

    // ================================================================
    // 3.5 身体活动计数来源（G14 预留接口，当前故意不做任何实现）
    // ================================================================
    /// <summary>
    /// 身体活动的计数来源。当前只有"自己数"一种实现（无实现类，走 UI 手动计数）。
    /// TODO(v1.2)：接入摄像头姿态计数（可选档，默认关，模型必须本地推理、画面不出内存）。
    ///              届时新增 CameraBodyCounter : IBodyCounter，并在 SettingsForm 里打开被禁用的开关。
    /// </summary>
    internal interface IBodyCounter
    {
        void Start();
        void Stop();
        int Reps { get; }
    }

    // ================================================================
    // 3.6 系统通知状态探测（P0-C：FullscreenDndGate）
    // ================================================================
    //
    // 契约 §10.1：FullscreenDndGate 进核心代码 —— **可配置 ≠ 可卸载**。
    // 关掉它，探测代码仍在包里，只是不再抑制（有人在投屏时就是想被弹）。
    //
    // ---- 为什么只有一部分 QUNS 状态算抑制源 ----
    //
    // 2026-11-14 本机实测（tools/p0c-quns-probe.py，连采 3 次）：
    //   HRESULT = 0，QUNS **恒为 5 = QUNS_QUIET_TIME**；
    //   注册表无 HKCU\...\FocusAssist 项 → 是 Win11 系统级免打扰常开，不是用户手动开的。
    //
    // 所以「把 QUNS_QUIET_TIME 当硬抑制源」在本机等于**EyeLite 永远不提醒**——
    // 产品直接变成坏掉的版本。它们的语义是"系统不弹通知"，
    // 而 EyeLite 是用户主动装的护眼工具，语义不同：只用来做诊断文案，不用来抑制。
    //
    // 硬抑制源（真的会被全屏 / 演示打断）：
    //   QUNS_BUSY(1) / QUNS_USING_D3D_FULL_SCREEN(2) / QUNS_PRESENTATION_MODE(3)
    //   / QUNS_RUNNING_D3D_FULL_SCREEN(4)
    internal enum QunsState
    {
        NotForcedOn = 0,
        Busy = 1,
        UsingD3DFullScreen = 2,
        PresentationMode = 3,
        RunningD3DFullScreen = 4,
        QuietTime = 5,
        BusyDnd = 6
    }

    internal static class NotificationState
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int SHQueryUserNotificationState(out uint pnqs);

        // 轮询缓存 3 秒：跃迁点最多延迟 3 秒感知全屏状态，
        // 而到点前还有预告窗口（P0-B），这个精度绰绰有余。
        private const long PollIntervalMs = 3000;
        private static readonly object gateObj = new object();
        private static QunsState cached = QunsState.NotForcedOn;
        private static long cachedAtMs = 0;
        private static bool cacheValid = false;

        // 测试钩子（自动化实测用）：环境变量 EYE_LITE_FORCE_QUNS 设为 0..6 可强制状态。
        // 未设置时走真实 API。留在发布版里无害 —— 必须显式设环境变量才会生效。
        private static readonly int forced = ReadForced();

        private static int ReadForced()
        {
            try
            {
                string s = Environment.GetEnvironmentVariable("EYE_LITE_FORCE_QUNS");
                if (string.IsNullOrEmpty(s)) return -1;
                int v;
                if (!int.TryParse(s, out v)) return -1;
                if (v < 0 || v > 6) return -1;
                return v;
            }
            catch { return -1; }
        }

        // 测试钩子之二（P0-C-8 可测）：EYE_LITE_FORCE_QUNS_FILE 指向一个文本文件，
        // **每次 Query() 都重读文件内容**，内容是 0..6 的整数时优先于 EYE_LITE_FORCE_QUNS。
        //
        // 为什么需要它：Win32 不允许一个进程改另一个进程的环境变量，
        // 所以进程起来之后没法把 QUNS 从 4（全屏）改成 0（正常），
        // "抑制解除后立刻补弹"这条用例就只能靠这个文件钩子来驱动。
        //
        // 只在初始化时读一次路径；路径为空（未设）时每次 Query 只是一次字符串长度判断，
        // 不产生任何文件 I/O —— 不设该环境变量时零影响。
        private static readonly string forcedFile = ReadForcedFile();

        private static string ReadForcedFile()
        {
            try
            {
                string s = Environment.GetEnvironmentVariable("EYE_LITE_FORCE_QUNS_FILE");
                if (string.IsNullOrEmpty(s)) return "";
                return s;
            }
            catch { return ""; }
        }

        /// <summary>读文件钩子当前的值；没设路径 / 文件不在 / 内容非法一律返回 -1（= 不生效）。</summary>
        private static int ReadForcedFromFile()
        {
            if (forcedFile.Length == 0) return -1;
            try
            {
                if (!File.Exists(forcedFile)) return -1;
                string s = File.ReadAllText(forcedFile, Encoding.UTF8).Trim();
                // 有些脚本写文件会留 UTF-8 BOM，而 Trim 不把 U+FEFF 当空白 ——
                // 不剥掉就会 TryParse 失败、钩子静默失效，测试只能看到"没反应"。
                if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1).Trim();
                if (s.Length == 0) return -1;
                int v;
                if (!int.TryParse(s, out v)) return -1;
                if (v < 0 || v > 6) return -1;
                return v;
            }
            catch { return -1; }
        }

        /// <summary>
        /// 读当前系统通知状态。失败时返回上一次的值（首次为 NotForcedOn）——
        /// **fail-open**：探测失败宁可照常提醒，也不能让用户悄悄失去所有提醒。
        /// </summary>
        public static QunsState Query()
        {
            // 文件钩子必须排在缓存前面：它是"运行中改变状态"的唯一手段，
            // 放缓存后面会让变更最多延迟 3 秒才生效。未设路径时这里只是一次长度判断。
            int fv = ReadForcedFromFile();
            if (fv >= 0) return (QunsState)fv;

            if (forced >= 0) return (QunsState)forced;

            try
            {
                lock (gateObj)
                {
                    long now = Clock.NowMs();
                    if (cacheValid && (now - cachedAtMs) < PollIntervalMs) return cached;

                    uint v = 0;
                    int hr = SHQueryUserNotificationState(out v);
                    if (hr != 0) return cached;        // 失败：保持旧值
                    cached = (QunsState)v;
                    cachedAtMs = now;
                    cacheValid = true;
                    return cached;
                }
            }
            catch { return cached; }
        }

        /// <summary>该状态是否构成"硬抑制"（全屏 / 演示中）。</summary>
        public static bool IsBlocking(QunsState s)
        {
            switch (s)
            {
                case QunsState.Busy:
                case QunsState.UsingD3DFullScreen:
                case QunsState.PresentationMode:
                case QunsState.RunningD3DFullScreen:
                    return true;
                default:
                    return false;   // QuietTime / BusyDnd 明确**不**抑制，理由见类注释
            }
        }

        /// <summary>托盘提示用的简短原因文案（NotifyIcon.Text 上限 63 字符，必须短）。</summary>
        public static string ReasonText(QunsState s)
        {
            switch (s)
            {
                case QunsState.Busy:
                case QunsState.UsingD3DFullScreen:
                case QunsState.RunningD3DFullScreen:
                    return "全屏中，提醒暂缓";
                case QunsState.PresentationMode:
                    return "演示中，提醒暂缓";
                case QunsState.QuietTime:
                case QunsState.BusyDnd:
                    return "系统免打扰中，提醒照常";
                default:
                    return "";
            }
        }
    }

    // ================================================================
    // 3.9 插件骨架（进程内，不装 DLL、不装第三方）
    // ================================================================
    //
    // 设计目标只有三条，全部围绕"常驻程序的成本"：
    //   1. **未启用 = 不存在**：不 new、不注册定时器、不装钩子。配置里关着的时候，
    //      这些插件对进程的影响必须是零字节、零句柄、零 tick。
    //   2. **启用即延迟初始化**：第一次 Get() 时才 Factory() + OnEnable()，
    //      所以启动路径上一个插件对象都不会被构造（首屏时间不受插件数量影响）。
    //   3. **关闭即释放**：SetOn(id,false) / DisposeAll() 必须走 OnDisable 并把实例丢掉，
    //      下次重新启用会重新构造 —— 不允许"关掉了但对象还挂着定时器"这种半死状态。
    //
    // 不做的事（明确记下来，避免以后被当成遗漏）：
    //   · 不加载外部 DLL（无插件目录、无 Assembly.LoadFrom）——
    //     那会把"零第三方依赖"这条硬约束撕开一个口子，也会引入一个可被替换的注入点。
    //   · 不做插件间通信/依赖排序（当前 6 个插件彼此独立）。
    internal interface IEyeLitePlugin
    {
        /// <summary>配置键后缀：对应 AppConfig.Plugin_&lt;Id&gt;。</summary>
        string Id { get; }
        string DisplayName { get; }
        string Description { get; }
        /// <summary>首次启用时调用一次。这里才允许建定时器 / 分配资源。</summary>
        void OnEnable();
        /// <summary>关闭时调用，必须释放 OnEnable 里拿到的一切。</summary>
        void OnDisable();
        bool IsEnabled { get; }
    }

    internal class PluginDescriptor
    {
        public string Id;
        public string DisplayName;
        public string Description;
        /// <summary>null = 骨架里已占位但尚未实现（Get 会返回 null，不会崩）。</summary>
        public Func<IEyeLitePlugin> Factory;
        public IEyeLitePlugin Instance;
    }

    internal static class PluginHost
    {
        private static AppConfig cfg;

        /// <summary>必须在 TrayApp 构造时先 Init，之后 IsOn / SetOn 才有配置可读。</summary>
        public static void Init(AppConfig config)
        {
            cfg = config;
        }

        public static readonly List<PluginDescriptor> All = BuildAll();

        private static List<PluginDescriptor> BuildAll()
        {
            List<PluginDescriptor> list = new List<PluginDescriptor>();

            // A5：仪式音（已实现）
            list.Add(new PluginDescriptor
            {
                Id = "Sound",
                DisplayName = "仪式音",
                Description = "预告出现时响一声「基音」，连续没休息会叠加到 2 声、3 声。",
                Factory = delegate { return new RitualSoundPlugin(); }
            });

            // 以下 3 个在 P1 批 2 落地（Factory 从 null 换成真实现）。
            list.Add(new PluginDescriptor
            {
                Id = "EdgeGlow",
                DisplayName = "屏幕边缘微光",
                Description = "休息前 60 秒在屏幕四边浮一层会呼吸的微光，比整屏压暗更轻。",
                Factory = delegate { return new EdgeGlowPlugin(); }
            });
            list.Add(new PluginDescriptor
            {
                Id = "Ritual",
                DisplayName = "休息仪式",
                Description = "休息开始时弹一个小窗：眼神操（8 秒）与 4-7-8 呼吸交替。",
                Factory = delegate { return new RitualPlugin(); }
            });
            list.Add(new PluginDescriptor
            {
                Id = "Stats",
                DisplayName = "用眼统计",
                Description = "记录完成 / 跳过 / 忽略的次数（只计数，不做健康判断）。",
                Factory = delegate { return new StatsPlugin(); }
            });

            // 以下 2 个是 P1 后续批次的位置，先占位：Factory = null，Get() 恒返回 null。
            list.Add(Placeholder("Movement", "起身计数", "统计每天的起身/活动次数。（未实现）"));
            list.Add(Placeholder("Guard", "输入守卫", "休息期间对输入做更细的过滤策略。（未实现）"));

            return list;
        }

        private static PluginDescriptor Placeholder(string id, string name, string desc)
        {
            PluginDescriptor d = new PluginDescriptor();
            d.Id = id;
            d.DisplayName = name;
            d.Description = desc;
            d.Factory = null;
            return d;
        }

        private static PluginDescriptor Find(string id)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i].Id == id) return All[i];
            }
            return null;
        }

        /// <summary>读配置里的开关。没有 cfg（忘了 Init）时按"关"处理，宁可少个功能也不要崩。</summary>
        public static bool IsOn(string id)
        {
            if (cfg == null) return false;
            return GetFlag(cfg, id);
        }

        /// <summary>
        /// 取插件实例。未启用 / 未实现一律返回 null（调用方必须判空，这是"未启用不创建对象"的落点）。
        /// 首次调用才 Factory() + OnEnable()。
        /// </summary>
        public static IEyeLitePlugin Get(string id)
        {
            PluginDescriptor d = Find(id);
            if (d == null || d.Factory == null) return null;
            if (!IsOn(id)) return null;
            if (d.Instance != null) return d.Instance;

            IEyeLitePlugin p = null;
            try
            {
                p = d.Factory();
                if (p != null) p.OnEnable();
            }
            catch
            {
                // 初始化失败就当没装：不留一个半初始化的实例在 All 里
                if (p != null) { try { p.OnDisable(); } catch { } }
                p = null;
            }
            d.Instance = p;
            return d.Instance;
        }

        public static void SetOn(string id, bool on, AppConfig c)
        {
            if (c != null) SetFlag(c, id, on);

            PluginDescriptor d = Find(id);
            if (d == null) return;

            if (!on && d.Instance != null)
            {
                try { d.Instance.OnDisable(); } catch { }
                d.Instance = null;      // 关掉就丢引用，下次启用重新构造
            }
        }

        /// <summary>退出 / 释放时调：所有已启用的插件走一遍 OnDisable 并丢引用。</summary>
        public static void DisposeAll()
        {
            for (int i = 0; i < All.Count; i++)
            {
                PluginDescriptor d = All[i];
                if (d == null || d.Instance == null) continue;
                try { d.Instance.OnDisable(); } catch { }
                d.Instance = null;
            }
        }

        // 配置读写用显式 switch 而不是反射：字段就 6 个，switch 可读、可搜索、零分配，
        // 也不会因为有人改名了字段而在运行时静默失效。
        private static bool GetFlag(AppConfig c, string id)
        {
            switch (id)
            {
                case "Sound": return c.Plugin_Sound;
                case "EdgeGlow": return c.Plugin_EdgeGlow;
                case "Ritual": return c.Plugin_Ritual;
                case "Stats": return c.Plugin_Stats;
                case "Movement": return c.Plugin_Movement;
                case "Guard": return c.Plugin_Guard;
                default: return false;
            }
        }

        private static void SetFlag(AppConfig c, string id, bool v)
        {
            switch (id)
            {
                case "Sound": c.Plugin_Sound = v; break;
                case "EdgeGlow": c.Plugin_EdgeGlow = v; break;
                case "Ritual": c.Plugin_Ritual = v; break;
                case "Stats": c.Plugin_Stats = v; break;
                case "Movement": c.Plugin_Movement = v; break;
                case "Guard": c.Plugin_Guard = v; break;
            }
        }
    }

    // ================================================================
    // 3.10 A5 仪式音插件
    // ================================================================
    //
    // 挂在 StartPreview 而不是 BeginRest：
    //   预告是"还有 30 秒"的那个时点，这时候提人才有用；等遮罩盖上来才响，
    //   人已经被打断了，声音只是二次打扰。
    //
    // 用 Timer 串行重复，而不是一次合成"含 N 声"的 WAV：
    //   N 在调用时才确定（1~3）。现合成意味着每次都要走一遍 BuildWave + pin；
    //   缓存 3 份又多两份永不释放的 pin。Timer 方案只合成 1 份、复用一个 pin，
    //   且关闭插件能立刻停 —— 这是"关闭即释放"那条硬要求在声音上的落点。
    internal class RitualSoundPlugin : IEyeLitePlugin
    {
        private const int GapMs = 220;      // 每声间隔：比 180ms 的短音长一点，听感才不糊

        private bool enabled;
        private System.Windows.Forms.Timer repeatTimer;
        private int remaining;

        public string Id { get { return "Sound"; } }
        public string DisplayName { get { return "仪式音"; } }
        public string Description { get { return "预告出现时按紧迫度响 1~3 声基音。"; } }
        public bool IsEnabled { get { return enabled; } }

        public void OnEnable()
        {
            // 这里故意不建 Timer：真正要响的时候才建（大多数周期一次都不响）
            enabled = true;
        }

        public void OnDisable()
        {
            enabled = false;
            StopRepeat();
        }

        /// <summary>
        /// 播 count 声（钳在 1~3），第一声立刻响，其余每隔 220ms 一声。
        /// 重复调用会先停掉上一轮，避免两轮叠在一起变成噪音。
        /// </summary>
        public void PlayRitual(int count)
        {
            if (!enabled) return;
            if (count < 1) count = 1;
            if (count > 3) count = 3;

            StopRepeat();

            Sfx.PlayRitualOnce();
            remaining = count - 1;
            if (remaining <= 0) return;

            repeatTimer = new System.Windows.Forms.Timer();
            repeatTimer.Interval = GapMs;
            repeatTimer.Tick += delegate { OnRepeatTick(); };
            repeatTimer.Start();
        }

        private void OnRepeatTick()
        {
            if (remaining <= 0)
            {
                StopRepeat();
                return;
            }

            remaining--;
            Sfx.PlayRitualOnce();
            if (remaining <= 0) StopRepeat();
        }

        private void StopRepeat()
        {
            if (repeatTimer != null)
            {
                try { repeatTimer.Stop(); } catch { }
                try { repeatTimer.Dispose(); } catch { }
                repeatTimer = null;
            }
            remaining = 0;
        }
    }

    // ================================================================
    // 3.11 A1 边缘辉光（插件 EdgeGlow）
    // ================================================================
    //
    // 为什么是"一个整屏 Form + 窗口 Region 只留四条窄边"（而不是四条独立窄窗、也不是全屏位图）：
    //   · 全屏位图渐变违反文件头第 5 条（3840×2160×4 ≈ 33MB/屏）；
    //   · 四条独立窄窗每屏多 3 个窗口句柄，TopMost 层级与释放都更啰嗦；
    //   · Region 只留四边 → 中间那块**根本不属于这个窗口**：既不会被画黑、也不参与命中测试。
    //     比 TransparencyKey 干净 —— TransparencyKey 与 Opacity 同用时在部分机器上会互相干扰。
    //
    // 呼吸只改整窗 Opacity（0.05 ↔ 0.35，3 秒一个来回），不重画、不缓存任何位图。
    internal class EdgeGlowForm : Form
    {
        private const int BandDefault = 6;      // 96dpi 下的边带厚度

        private int band = BandDefault;
        private float dpiScale = 1f;

        public EdgeGlowForm(Rectangle bounds)
        {
            dpiScale = Ui.DetectScale(this);

            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = bounds;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.BackColor = Color.Black;
            this.AutoScaleMode = AutoScaleMode.None;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint |
                          ControlStyles.OptimizedDoubleBuffer |
                          ControlStyles.UserPaint, true);

            band = (int)(BandDefault * dpiScale + 0.5f);
            if (band < 2) band = 2;

            // 构造期先压到 0：让 CreateParams 带上 WS_EX_LAYERED，首帧不会闪一帧满亮度
            try { this.Opacity = 0.0; }
            catch { }

            RebuildRegion();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab、不占任务栏
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT：鼠标穿透，不拦任何点击
                cp.ExStyle |= 0x00000008;   // WS_EX_TOPMOST：压在其他窗口之上
                return cp;
            }
        }

        /// <summary>整窗不透明度（呼吸用）。句柄没建好时直接忽略。</summary>
        public void SetAlpha(double a)
        {
            if (a < 0.0) a = 0.0;
            if (a > 1.0) a = 1.0;
            try
            {
                if (!this.IsHandleCreated) return;
                this.Opacity = a;
            }
            catch { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RebuildRegion();
        }

        /// <summary>窗口只占四条窄边：中间区域不属于窗口，天然透明、天然不挡鼠标。</summary>
        private void RebuildRegion()
        {
            Size s = this.ClientSize;
            if (s.Width <= 4 || s.Height <= 4) return;

            GraphicsPath path = new GraphicsPath();
            try
            {
                path.AddRectangle(new Rectangle(0, 0, s.Width, band));                  // 上
                path.AddRectangle(new Rectangle(0, s.Height - band, s.Width, band));    // 下
                path.AddRectangle(new Rectangle(0, 0, band, s.Height));                 // 左
                path.AddRectangle(new Rectangle(s.Width - band, 0, band, s.Height));    // 右

                Region old = this.Region;
                this.Region = new Region(path);
                if (old != null) old.Dispose();
            }
            finally
            {
                path.Dispose();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 不画背景：只有四条带子有内容；Region 之外的像素根本不会被合成
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Size s = this.ClientSize;

            // 四支渐变笔全部现场创建 + using 释放：不缓存 brush、不缓存位图
            DrawBand(g, new Rectangle(0, 0, s.Width, band), LinearGradientMode.Vertical, false);
            DrawBand(g, new Rectangle(0, s.Height - band, s.Width, band), LinearGradientMode.Vertical, true);
            DrawBand(g, new Rectangle(0, 0, band, s.Height), LinearGradientMode.Horizontal, false);
            DrawBand(g, new Rectangle(s.Width - band, 0, band, s.Height), LinearGradientMode.Horizontal, true);
        }

        /// <summary>
        /// 画一条"从屏幕边缘向内淡出"的带子。
        /// reverse=true 表示亮端在远端（下边/右边的亮边必须贴在屏幕边缘上）。
        /// </summary>
        private void DrawBand(Graphics g, Rectangle r, LinearGradientMode mode, bool reverse)
        {
            if (r.Width <= 0 || r.Height <= 0) return;

            Color near = Ui.Accent;
            Color far = Color.FromArgb(0, Ui.Accent);
            Color c1 = reverse ? far : near;
            Color c2 = reverse ? near : far;

            try
            {
                using (LinearGradientBrush b = new LinearGradientBrush(r, c1, c2, mode))
                {
                    g.FillRectangle(b, r);
                }
            }
            catch { }
        }
    }

    internal class EdgeGlowPlugin : IEyeLitePlugin
    {
        private const int TickMs = 100;             // 呼吸刷新间隔
        private const double PeriodMs = 3000.0;     // 一个来回 3 秒
        private const double AlphaMin = 0.05;
        private const double AlphaMax = 0.35;

        private bool enabled;
        private List<EdgeGlowForm> forms;
        private System.Windows.Forms.Timer timer;
        private double phase;                       // 0~1 呼吸相位
        private bool windowOpen;

        public string Id { get { return "EdgeGlow"; } }
        public string DisplayName { get { return "屏幕边缘微光"; } }
        public string Description { get { return "休息前 60 秒在屏幕四边浮一层会呼吸的微光。"; } }
        public bool IsEnabled { get { return enabled; } }

        public void OnEnable()
        {
            // 这里故意什么都不建：窗口和定时器都要等到"真的要亮"的那一刻
            enabled = true;
        }

        public void OnDisable()
        {
            enabled = false;
            windowOpen = false;
            DisposeForms();
        }

        /// <summary>
        /// 由 TrayApp 每 tick 调用一次。open 决定显隐；状态没变就什么都不做
        /// （避免每秒 Show/Hide 抖动 —— 那会让 DWM 反复重建合成层）。
        /// </summary>
        public void SetWindow(bool open)
        {
            if (!enabled) return;
            if (open == windowOpen) return;

            windowOpen = open;
            if (open) ShowForms();
            else HideForms();
        }

        /// <summary>立即熄灭（进休息/预告/抑制/暂停时调用，不等下一个 tick）。</summary>
        public void HideNow()
        {
            windowOpen = false;
            HideForms();
        }

        private void ShowForms()
        {
            EnsureForms();
            if (forms == null) return;

            double a = CurrentAlpha();
            for (int i = 0; i < forms.Count; i++)
            {
                EdgeGlowForm f = forms[i];
                if (f == null || f.IsDisposed) continue;
                try
                {
                    if (!f.Visible) f.Show();
                    f.SetAlpha(a);
                }
                catch { }
            }
            StartTimer();
        }

        private void HideForms()
        {
            StopTimer();
            if (forms == null) return;

            for (int i = 0; i < forms.Count; i++)
            {
                EdgeGlowForm f = forms[i];
                if (f == null || f.IsDisposed) continue;
                try
                {
                    f.SetAlpha(0.0);
                    if (f.Visible) f.Hide();
                }
                catch { }
            }
        }

        /// <summary>每屏一条。只在"真要亮"时创建 —— 未启用插件的进程里一个窗口都没有。</summary>
        private void EnsureForms()
        {
            if (forms != null) return;

            List<EdgeGlowForm> list = new List<EdgeGlowForm>();
            try
            {
                Screen[] screens = Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    list.Add(new EdgeGlowForm(screens[i].Bounds));
                }
            }
            catch { }

            forms = list;
        }

        /// <summary>余弦呼吸：0→1→0 平滑，三角波太机械。</summary>
        private double CurrentAlpha()
        {
            double k = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * phase);
            return AlphaMin + (AlphaMax - AlphaMin) * k;
        }

        private void StartTimer()
        {
            if (timer == null)
            {
                timer = new System.Windows.Forms.Timer();
                timer.Interval = TickMs;
                timer.Tick += delegate { OnBreathTick(); };
            }
            if (!timer.Enabled) timer.Start();
        }

        private void StopTimer()
        {
            if (timer != null)
            {
                try { timer.Stop(); } catch { }
            }
        }

        private void OnBreathTick()
        {
            if (!enabled || !windowOpen)
            {
                StopTimer();
                return;
            }

            phase += TickMs / PeriodMs;
            if (phase >= 1.0) phase -= 1.0;

            double a = CurrentAlpha();
            if (forms == null) return;
            for (int i = 0; i < forms.Count; i++)
            {
                EdgeGlowForm f = forms[i];
                if (f == null || f.IsDisposed) continue;
                f.SetAlpha(a);
            }
        }

        private void DisposeForms()
        {
            if (timer != null)
            {
                try { timer.Stop(); } catch { }
                try { timer.Dispose(); } catch { }
                timer = null;
            }

            if (forms == null) return;
            for (int i = 0; i < forms.Count; i++)
            {
                EdgeGlowForm f = forms[i];
                if (f == null) continue;
                try { f.SetAlpha(0.0); } catch { }
                try { f.Hide(); } catch { }
                try { f.Close(); } catch { }
                try { f.Dispose(); } catch { }
            }
            forms.Clear();
            forms = null;
        }
    }

    // ================================================================
    // 3.12 B1 眼神操 + B2 呼吸圆（插件 Ritual）
    // ================================================================
    //
    // 一个窗口类 + 一个 mode 字段：B1（眼神操 8 秒）与 B2（4-7-8 呼吸）版式几乎一样，
    // 拆成两个类会复制一堆居中/配色/定时器代码，还得维护两份"别抢焦点"的窗口风格。
    //
    // 每次休息新建、休息结束 Dispose（而不是 Show/Hide 复用）：
    //   这个窗口一局只活 8~19 秒，造型又依赖"这一次的 mode"，复用反而要写重置逻辑。
    //   一次休息一个新窗口 = 一次小分配，比留三个状态位安全。
    //
    // 眼睛用 GraphicsPath 现画，不载图片：零资源、任意 DPI 都清晰。
    internal class RitualForm : Form
    {
        private const int TickMs = 16;          // 约 60fps

        private const int EyeSteps = 4;
        private const int EyeStepMs = 2000;     // 每步 2 秒 → 共 8 秒
        private const int BreathInMs = 4000;
        private const int BreathHoldMs = 7000;
        private const int BreathOutMs = 8000;
        private const int BreathCycleMs = BreathInMs + BreathHoldMs + BreathOutMs;  // 19 秒

        private int mode;                       // 0 = 眼神操(B1)，1 = 呼吸圆(B2)
        private System.Windows.Forms.Timer anim;
        private int elapsedMs;
        private float dpiScale = 1f;

        public RitualForm(int mode)
        {
            dpiScale = Ui.DetectScale(this);
            this.mode = mode;

            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.BackColor = Ui.GradTop;
            this.AutoScaleMode = AutoScaleMode.None;
            this.DoubleBuffered = true;
            this.Font = Ui.FontUi;

            int w = Sc(320);
            int h = Sc(360);
            this.Size = new Size(w, h);
            CenterOnPrimary(w, h);

            try { this.Opacity = 0.96; }
            catch { }

            anim = new System.Windows.Forms.Timer();
            anim.Interval = TickMs;
            anim.Tick += delegate { OnAnimTick(); };
            anim.Start();
        }

        private int Sc(int px)
        {
            return (int)(px * dpiScale + 0.5f);
        }

        /// <summary>居中到主屏工作区（不跟随鼠标：休息时人的注意力应该回到屏幕中间）。</summary>
        private void CenterOnPrimary(int w, int h)
        {
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                this.Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
            }
            catch { }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点（不打断输入法组词）
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab、不占任务栏
                return cp;
            }
        }

        private void OnAnimTick()
        {
            try
            {
                elapsedMs += TickMs;

                int total = mode == 0 ? EyeStepMs * EyeSteps : BreathCycleMs;
                if (elapsedMs >= total)
                {
                    // 走完自动关闭（不再开下一轮：这是一次性的仪式）
                    StopAnim();
                    try { this.Close(); } catch { }
                    return;
                }
                this.Invalidate();
            }
            catch { }
        }

        private void StopAnim()
        {
            if (anim != null)
            {
                try { anim.Stop(); } catch { }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (anim != null)
                {
                    try { anim.Stop(); } catch { }
                    try { anim.Dispose(); } catch { }
                    anim = null;
                }
            }
            base.Dispose(disposing);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            try { e.Graphics.Clear(Ui.GradTop); }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            try { g.SmoothingMode = SmoothingMode.AntiAlias; }
            catch { }

            int w = this.ClientSize.Width;
            int h = this.ClientSize.Height;

            // 眉标 + 步骤/节拍文字都用 TextRenderer 画（与程序其它地方同一套渲染口径）
            string title = mode == 0 ? "眼神操" : "4-7-8 呼吸";
            TextRenderer.DrawText(g, title, Ui.FontEyebrow,
                new Rectangle(0, Sc(16), w, Sc(20)), Ui.EyebrowText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

            if (mode == 0) PaintEyeExercise(g, w, h);
            else PaintBreathing(g, w, h);
        }

        // ---------- B1：8 秒眼神操（4 步 × 2 秒）----------
        private void PaintEyeExercise(Graphics g, int w, int h)
        {
            int step = elapsedMs / EyeStepMs;
            if (step < 0) step = 0;
            if (step > EyeSteps - 1) step = EyeSteps - 1;
            int inStep = elapsedMs - step * EyeStepMs;      // 0..1999

            int cx = w / 2;
            int cy = Sc(180);
            int ew = Sc(200);      // 眼形宽
            int eh = Sc(64);       // 眼形高（半高 Sc(32)）

            // 眨眼步：用一段"闭-开"的时长包络把眼形压扁
            double squash = 1.0;
            if (step == 1)
            {
                // 每 500ms 眨一次，闭合占 140ms
                int blinkPhase = inStep % 500;
                if (blinkPhase < 140) squash = 0.08;
            }
            else if (step == 3)
            {
                // 闭眼：整步都是闭合状态，最后 300ms 慢慢睁开一点
                squash = inStep < EyeStepMs - 300 ? 0.06 : 0.5;
            }

            int halfH = (int)(eh / 2 * squash);
            if (halfH < Sc(2)) halfH = Sc(2);

            // 杏仁形：上下两条贝塞尔曲线闭合
            GraphicsPath eye = new GraphicsPath();
            try
            {
                eye.AddBezier(cx - ew / 2, cy,
                              cx - ew / 4, cy - halfH * 2,
                              cx + ew / 4, cy - halfH * 2,
                              cx + ew / 2, cy);
                eye.AddBezier(cx + ew / 2, cy,
                              cx + ew / 4, cy + halfH * 2,
                              cx - ew / 4, cy + halfH * 2,
                              cx - ew / 2, cy);
                eye.CloseFigure();

                using (Pen pen = new Pen(Ui.TimeText, Math.Max(1f, Sc(2))))
                {
                    g.DrawPath(pen, eye);
                }

                // 瞳孔：按步骤移动（眨眼步不用特殊处理 —— halfH 已经被压扁，瞳孔跟着变小）
                int px = cx;
                int py = cy;
                int pr = Math.Max(Sc(3), halfH / 2);

                if (step == 0)
                {
                    // 左右看：一个来回
                    double t = inStep / (double)EyeStepMs;          // 0..1
                    px = cx + (int)(Math.Sin(t * 2.0 * Math.PI) * (ew / 2 - pr * 1.6));
                }
                else if (step == 2)
                {
                    // 转眼球：画小圆
                    double t = inStep / (double)EyeStepMs;
                    px = cx + (int)(Math.Cos(t * 2.0 * Math.PI) * (ew / 5));
                    py = cy + (int)(Math.Sin(t * 2.0 * Math.PI) * (eh / 6));
                }

                if (pr < Sc(2)) pr = Sc(2);

                using (SolidBrush b = new SolidBrush(Ui.Accent))
                {
                    g.FillEllipse(b, px - pr, py - pr, pr * 2, pr * 2);
                }
            }
            finally
            {
                eye.Dispose();
            }

            // 步骤说明 + 进度点
            string caption;
            if (step == 0) caption = "左右看";
            else if (step == 1) caption = "快速眨眼";
            else if (step == 2) caption = "转眼球";
            else caption = "闭眼";

            TextRenderer.DrawText(g, caption, Ui.FontRestEyebrow,
                new Rectangle(0, Sc(268), w, Sc(28)), Ui.TimeText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, (step + 1) + " / " + EyeSteps, Ui.FontEyebrow,
                new Rectangle(0, Sc(300), w, Sc(18)), Ui.RingLabel,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

            // 底部进度条：8 秒还剩多久一眼可见
            int barW = w - Sc(64);
            int barY = Sc(330);
            int barH = Math.Max(2, Sc(3));
            using (SolidBrush track = new SolidBrush(Ui.RingTrack))
            {
                g.FillRectangle(track, Sc(32), barY, barW, barH);
            }
            int done = (int)(barW * (elapsedMs / (double)(EyeStepMs * EyeSteps)));
            if (done > 0)
            {
                using (SolidBrush fg = new SolidBrush(Ui.Accent))
                {
                    g.FillRectangle(fg, Sc(32), barY, done, barH);
                }
            }
        }

        // ---------- B2：4-7-8 呼吸（吸 4s / 屏 7s / 呼 8s）----------
        private void PaintBreathing(Graphics g, int w, int h)
        {
            int cx = w / 2;
            int cy = Sc(180);

            int rMin = Sc(52);
            int rMax = Sc(104);
            double k;               // 0 = 最小圆，1 = 最大圆
            string caption;
            int leftSec;

            if (elapsedMs < BreathInMs)
            {
                double t = elapsedMs / (double)BreathInMs;
                k = EaseInOut(t);                       // 吸气：慢慢鼓起来
                caption = "吸气";
                leftSec = (BreathInMs - elapsedMs + 999) / 1000;
            }
            else if (elapsedMs < BreathInMs + BreathHoldMs)
            {
                k = 1.0;                                // 屏息：保持最大
                caption = "屏住";
                leftSec = (BreathInMs + BreathHoldMs - elapsedMs + 999) / 1000;
            }
            else
            {
                double t = (elapsedMs - BreathInMs - BreathHoldMs) / (double)BreathOutMs;
                k = 1.0 - EaseInOut(t);                 // 呼气：慢慢收回
                caption = "呼气";
                leftSec = (BreathCycleMs - elapsedMs + 999) / 1000;
            }

            if (k < 0.0) k = 0.0;
            if (k > 1.0) k = 1.0;

            int r = rMin + (int)((rMax - rMin) * k);

            // 外圈轨道（常驻）+ 内圈实心（随呼吸缩放）
            using (Pen track = new Pen(Ui.RingTrack, Math.Max(1f, Sc(2))))
            {
                g.DrawEllipse(track, cx - rMax, cy - rMax, rMax * 2, rMax * 2);
            }
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(70, Ui.Accent)))
            {
                g.FillEllipse(fill, cx - r, cy - r, r * 2, r * 2);
            }
            using (Pen edge = new Pen(Ui.Accent, Math.Max(1f, Sc(2))))
            {
                g.DrawEllipse(edge, cx - r, cy - r, r * 2, r * 2);
            }

            TextRenderer.DrawText(g, caption, Ui.FontRestEyebrow,
                new Rectangle(0, Sc(268), w, Sc(28)), Ui.TimeText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, leftSec + " 秒", Ui.FontEyebrow,
                new Rectangle(0, Sc(300), w, Sc(18)), Ui.RingLabel,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }

        /// <summary>ease-in-out：0→1 用半个余弦，两端速度为 0，呼吸感才对。</summary>
        private static double EaseInOut(double t)
        {
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            return 0.5 - 0.5 * Math.Cos(Math.PI * t);
        }
    }

    internal class RitualPlugin : IEyeLitePlugin
    {
        private bool enabled;
        private RitualForm form;
        private int sessions;       // 每轮休息 +1，用来交替 B1 / B2

        public string Id { get { return "Ritual"; } }
        public string DisplayName { get { return "休息仪式"; } }
        public string Description { get { return "休息开始时弹眼神操 / 4-7-8 呼吸，两者交替。"; } }
        public bool IsEnabled { get { return enabled; } }

        public void OnEnable()
        {
            enabled = true;
        }

        public void OnDisable()
        {
            enabled = false;
            Hide();
        }

        /// <summary>
        /// 休息开始时调：第 1 次眼操、第 2 次呼吸、循环。
        /// 没有 UI 配置，先用这个固定顺序（设置页以后再加"只要眼操 / 只要呼吸 / 交替"）。
        /// </summary>
        public void ShowForRest()
        {
            if (!enabled) return;

            Hide();                     // 上一轮没关干净就扔掉，绝不留两个窗

            int mode = sessions % 2;
            sessions++;

            try
            {
                form = new RitualForm(mode);
                form.Show();
            }
            catch
            {
                // 仪式窗出问题绝不能影响休息本身
                form = null;
            }
        }

        /// <summary>休息结束时调（EndRest）：关闭并释放。</summary>
        public void Hide()
        {
            if (form == null) return;

            RitualForm f = form;
            form = null;

            try { if (!f.IsDisposed) f.Close(); } catch { }
            try { f.Dispose(); } catch { }
        }

        /// <summary>
        /// 小窗句柄（没有窗口时返回 Zero）。TrayApp 每秒用它把仪式窗重新抬到遮罩之上：
        /// 遮罩那边每秒都会 SetWindowPos 重申 TopMost，会把置顶组的最前面抢回去。
        /// </summary>
        public IntPtr WindowHandle
        {
            get
            {
                RitualForm f = form;
                if (f == null || f.IsDisposed || !f.IsHandleCreated) return IntPtr.Zero;
                return f.Handle;
            }
        }
    }

    // ================================================================
    // 3.13 F1 三态统计（插件 Stats）
    // ================================================================
    //
    // 这个插件本身几乎不做事：真正的计数点在 TrayApp 的状态机里（那里才分得清
    // "坐满 / 跳过 / 被让路"，插件拿不到这些状态）。
    // 它存在的意义是：① 让"是否启用"这件事和其它插件同一套机制（未启用 → Get 返回 null →
    // 调用方连计数都不执行）；② 计数落盘、后续按天聚合都归它管。
    internal class StatsPlugin : IEyeLitePlugin
    {
        private bool enabled;

        public string Id { get { return "Stats"; } }
        public string DisplayName { get { return "用眼统计"; } }
        public string Description { get { return "记录完成 / 跳过 / 忽略的次数（只计数）。"; } }
        public bool IsEnabled { get { return enabled; } }

        public void OnEnable()
        {
            enabled = true;
        }

        public void OnDisable()
        {
            enabled = false;
        }

        // 三个计数入口。cfg 由调用方传入（插件不持有全局配置，避免两处状态）
        public void BumpDone(AppConfig cfg)
        {
            if (!enabled || cfg == null) return;
            cfg.StatDone++;
            Persist(cfg);
        }

        public void BumpSkip(AppConfig cfg)
        {
            if (!enabled || cfg == null) return;
            cfg.StatSkip++;
            Persist(cfg);
        }

        public void BumpIgnore(AppConfig cfg)
        {
            if (!enabled || cfg == null) return;
            cfg.StatIgnore++;
            Persist(cfg);
        }

        /// <summary>
        /// 每次计数立刻写盘：这三个数一天只变几次，写盘成本可以忽略，
        /// 但"被任务管理器强杀"时不会丢 —— 统计最忌讳的就是悄悄少记。
        /// </summary>
        private static void Persist(AppConfig cfg)
        {
            try { cfg.Save(); } catch { }
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

        // P0-E：严格模式**默认关闭**。
        //
        // 为什么默认关：严格模式在休息期间会吞掉**整个键盘**，用户一旦触发休息，
        // 正在打的字就全部丢失；更糟的是如果触发休息时物理键正卡住（Ctrl 之类），
        // 钩子的 drain 机制会持续吞这个键的 repeat，用户感觉是"Ctrl 一直卡住"。
        // 这类"锁键盘"代价远大于"少一次休息提醒"的收益，所以默认档必须是安全的：
        // 只吞 Esc / 空格作为跳过出口，其余键照常输入。
        //
        // 注意：这里只改**默认值**。用户显式写过 StrictMode=1 的旧配置依然生效
        // （Load 里 "v == 1" 的语义不变）—— 不擅自推翻用户自己的选择。
        public bool StrictMode = false;
        public bool StartWithWindows = false;

        // 到点前多少秒出预告横幅（0 = 不预告，直接全屏遮罩）。
        // 竞品 憩目 Qimu 用的是 30 秒；这是 P0-B「30 秒预告 + 三出口」的参数。
        public int PreviewSeconds = 30;

        // A2 渐进式降饱和：休息前 60 秒把屏幕整体压暗的**最大强度**（百分比）。
        // 45 = 最暗时整窗 alpha 0.45；0 = 关闭该功能（一个 DimForm 都不创建）。
        // 上限 70 是为了别把人真正压暗到看不清内容 —— 渐暗是"温和提示"，不是遮罩。
        public int DimAlphaMax = 45;

        // 久坐/活动是**第二套时钟**，与护眼节奏独立：
        // 护眼（WorkMinutes/RestSeconds）管"看远处 20 秒"，久坐管"45 分钟起身 2 分钟"。
        public bool SedentaryEnabled = true;
        public int SedentaryMinutes = 45;
        public int SedentarySeconds = 120;

        // G14 预留：摄像头姿态计数（v1.2）。当前无实现类，开关在设置页被禁用。
        public bool CameraCountEnabled = false;

        // P0-C：系统通知状态闸门（FullscreenDndGate）。
        // 契约 §10.1「可配置 ≠ 可卸载」：代码恒在包里，这个开关只决定**要不要抑制**。
        // 默认开 —— 全屏/演示时弹遮罩是最典型的"错误时机打扰"（调研文档第一段的用户原话）。
        public bool SuppressOnFullscreen = true;

        // ---- C3：前台进程白名单（核心功能，不是插件）----
        //
        // 与 P0-C 的 QUNS gate 互补：QUNS 只认"全屏/演示"，
        // 而窗口化看片、PotPlayer 非全屏、OBS 预览窗口这些都查不到 QUNS。
        // 那种时候弹遮罩同样是"错误时机的打扰"，所以要再看一眼前台进程名。
        //
        // 默认开；命中白名单后最多让路 10 分钟（见 TrayApp.DeferredByActivity），
        // 超过就强制提醒 —— 白名单不能变成"永不提醒"的后门。
        public bool ForegroundGuardEnabled = true;
        public string ForegroundWhitelist = "vlc.exe;potplayermini64.exe;wmplayer.exe;mpc-hc64.exe;obs64.exe";

        // ---- C4：打字段检测（核心）----
        // 到点那一刻如果用户正在连续敲键（idle < 300ms），先让 5 秒，
        // 累计最多 60 秒。数据来源是 RawIdleMs()，与空闲计时同一口径。
        public bool TypingGuardEnabled = true;

        // ---- G3：休息卡三选一（默认关，避免改变现有版式）----
        // 开启后遮罩上是「跳过 / 坐一分钟 / 推迟」三个按钮，两个旧按钮按比例缩小。
        // 默认关 = 一个像素都不变。
        public bool SittingOptionEnabled = false;

        // ---- P1 插件开关（全部默认 false：默认即纯核心，不启用就不创建任何对象）----
        public bool Plugin_Sound = false;
        public bool Plugin_EdgeGlow = false;
        public bool Plugin_Ritual = false;
        public bool Plugin_Stats = false;
        public bool Plugin_Movement = false;
        public bool Plugin_Guard = false;

        // ---- F1 三态统计（Plugin_Stats 启用后才有意义，但字段恒在：读写不做条件分支）----
        //
        // 三态的定义（产品口径，不要在别处重新解释）：
        //   完成 = 坐满了这一次休息（自然到期），或按了「坐一分钟」
        //   跳过 = 用户主动点跳过（预告横幅里、遮罩上，都算）
        //   忽略 = 到点了，但既没坐满也没点跳过，而是被让路/被抑制拖过 5 分钟
        //
        // 只是计数，不做任何健康判断 —— 界面上必须有一句这样的话，
        // 否则"完成 3 次"很容易被读成"你今天很健康"。
        public int StatDone = 0;
        public int StatSkip = 0;
        public int StatIgnore = 0;

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
                        case "PreviewSeconds": c.PreviewSeconds = Clamp(v, 30, 0, 300); break;
                        case "DimAlphaMax": c.DimAlphaMax = Clamp(v, 45, 0, 70); break;
                        case "SedentaryEnabled": c.SedentaryEnabled = v == "1"; break;
                        case "SedentaryMinutes": c.SedentaryMinutes = Clamp(v, 45, 15, 180); break;
                        case "SedentarySeconds": c.SedentarySeconds = Clamp(v, 120, 30, 300); break;
                        case "CameraCountEnabled": c.CameraCountEnabled = v == "1"; break;
                        case "SuppressOnFullscreen": c.SuppressOnFullscreen = v == "1"; break;
                        case "ForegroundGuardEnabled": c.ForegroundGuardEnabled = v == "1"; break;
                        // 白名单是字符串，不过 Clamp（它不是数值）；条目里的空白留到解析时再 Trim
                        case "ForegroundWhitelist": c.ForegroundWhitelist = v; break;
                        case "TypingGuardEnabled": c.TypingGuardEnabled = v == "1"; break;
                        case "SittingOptionEnabled": c.SittingOptionEnabled = v == "1"; break;
                        case "Plugin_Sound": c.Plugin_Sound = v == "1"; break;
                        case "Plugin_EdgeGlow": c.Plugin_EdgeGlow = v == "1"; break;
                        case "Plugin_Ritual": c.Plugin_Ritual = v == "1"; break;
                        case "Plugin_Stats": c.Plugin_Stats = v == "1"; break;
                        case "Plugin_Movement": c.Plugin_Movement = v == "1"; break;
                        case "Plugin_Guard": c.Plugin_Guard = v == "1"; break;
                        case "StatDone": c.StatDone = Clamp(v, 0, 0, 100000000); break;
                        case "StatSkip": c.StatSkip = Clamp(v, 0, 0, 100000000); break;
                        case "StatIgnore": c.StatIgnore = Clamp(v, 0, 0, 100000000); break;
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
                sb.AppendLine("PreviewSeconds=" + PreviewSeconds);
                sb.AppendLine("DimAlphaMax=" + DimAlphaMax);
                sb.AppendLine("SedentaryEnabled=" + (SedentaryEnabled ? "1" : "0"));
                sb.AppendLine("SedentaryMinutes=" + SedentaryMinutes);
                sb.AppendLine("SedentarySeconds=" + SedentarySeconds);
                sb.AppendLine("CameraCountEnabled=" + (CameraCountEnabled ? "1" : "0"));
                sb.AppendLine("SuppressOnFullscreen=" + (SuppressOnFullscreen ? "1" : "0"));
                sb.AppendLine("ForegroundGuardEnabled=" + (ForegroundGuardEnabled ? "1" : "0"));
                sb.AppendLine("ForegroundWhitelist=" + ForegroundWhitelist);
                sb.AppendLine("TypingGuardEnabled=" + (TypingGuardEnabled ? "1" : "0"));
                sb.AppendLine("SittingOptionEnabled=" + (SittingOptionEnabled ? "1" : "0"));
                sb.AppendLine("Plugin_Sound=" + (Plugin_Sound ? "1" : "0"));
                sb.AppendLine("Plugin_EdgeGlow=" + (Plugin_EdgeGlow ? "1" : "0"));
                sb.AppendLine("Plugin_Ritual=" + (Plugin_Ritual ? "1" : "0"));
                sb.AppendLine("Plugin_Stats=" + (Plugin_Stats ? "1" : "0"));
                sb.AppendLine("Plugin_Movement=" + (Plugin_Movement ? "1" : "0"));
                sb.AppendLine("Plugin_Guard=" + (Plugin_Guard ? "1" : "0"));
                sb.AppendLine("StatDone=" + StatDone);
                sb.AppendLine("StatSkip=" + StatSkip);
                sb.AppendLine("StatIgnore=" + StatIgnore);
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
        private bool withSitting;      // G3：第三个出口「坐一分钟」（默认关）

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
        /// <summary>G3 第三个出口「坐一分钟」：结束本轮，但计为完成。</summary>
        public event EventHandler RequestSitting;

        public RestForm(Screen screen, bool allowSkip, int postponeMinutes, bool withButtons,
                        bool withSittingOption)
        {
            // 必须在算任何版式之前取到 DPI 因子（此时窗体还没有句柄）
            dpiScale = Ui.DetectScale(this);

            this.allowSkip = allowSkip;
            this.showButtons = withButtons;
            this.withSitting = withSittingOption;

            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = screen.Bounds;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.BackColor = Ui.GradTop;
            this.Opacity = TargetOpacity;
            // 这里原来有 this.KeyPreview = true —— 随 KeyDown 处理器一起删掉了：
            // 窗体永不成前台（WS_EX_NOACTIVATE），也没有键处理器，KeyPreview 无任何行为。
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
                // 描边宽度随 DPI 缩放，否则 150% 下 1px 细得像没画
                int bw = Math.Max(1, Sc(1));

                // G3：三出口只是"多一个按钮 + 重算几何"，描边色、事件、字体全部复用同一套。
                // 关闭时（withSitting=false）几何与改动前逐像素一致，见下面的 else 分支。
                int skipW, skipX, postW, postX, sitW, sitX;
                if (withSitting)
                {
                    // 三个等宽按钮均分：Sc(120) × 3 + 两道 Sc(12) 间距 = Sc(384)，半宽 Sc(192)
                    skipW = Sc(120);
                    sitW = Sc(120);
                    postW = Sc(120);
                    int g = Sc(12);
                    skipX = w / 2 - Sc(192);
                    sitX = skipX + skipW + g;
                    postX = sitX + sitW + g;
                }
                else
                {
                    // 两枚按钮整体水平居中：总宽 Sc(176) + Sc(16) + Sc(150) = Sc(342)，半宽 Sc(171)。
                    //   跳过：w/2 - Sc(171)，占 [-171, +5]
                    //   推迟：w/2 + Sc(21)，占 [+21, +171]（5 + 16 = 21）
                    // 之前写的是 -Sc(184) / +Sc(8)，组中心落在 -13px 上，肉眼可见偏左。
                    skipW = Sc(176);
                    skipX = w / 2 - Sc(171);
                    sitW = 0;
                    sitX = 0;
                    postW = Sc(150);
                    postX = w / 2 + Sc(21);
                }

                // 三出口时文案压短一个括号：「跳过 (Esc/空格)」在 Sc(120) 里会被省略号截断，
                // 而 Esc/空格 这条提示比括号重要，所以去掉括号保提示。
                btnSkip = new RoundButton(withSitting ? "跳过 Esc/空格" : "跳过 (Esc/空格)", Sc(21),
                                          Ui.DarkBtnBg, Ui.DarkBtnHover,
                                          Ui.SkipText, Ui.Accent, bw);
                btnSkip.Font = Ui.FontRowLabel;
                btnSkip.SetBounds(skipX, yBtn, skipW, btnH);
                btnSkip.Click += delegate
                {
                    if (RequestSkip != null) RequestSkip(this, EventArgs.Empty);
                };
                this.Controls.Add(btnSkip);

                // 「坐一分钟」放在中间：它不是"跳过"的近义词，而是"我坐了，算完成"，
                // 视觉上用强调色描边与"跳过"区分（填充仍与其它按钮一致，不抢主次）。
                if (withSitting)
                {
                    RoundButton btnSit = new RoundButton("坐一分钟", Sc(21),
                                                         Ui.DarkBtnBg, Ui.DarkBtnHover,
                                                         Ui.SkipText, Ui.Accent, bw);
                    btnSit.Font = Ui.FontRowLabel;
                    btnSit.SetBounds(sitX, yBtn, sitW, btnH);
                    btnSit.Click += delegate
                    {
                        if (RequestSitting != null) RequestSitting(this, EventArgs.Empty);
                    };
                    this.Controls.Add(btnSit);
                }

                RoundButton btnPostpone = new RoundButton("推迟 " + postponeMinutes + " 分钟", Sc(21),
                                                          Ui.DarkBtnBg, Ui.DarkBtnHover,
                                                          Ui.DarkBtnText, Ui.DarkBtnBorder, bw);
                btnPostpone.Font = Ui.FontRowLabel;
                btnPostpone.SetBounds(postX, yBtn, postW, btnH);
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

    /// <summary>
    /// 到点前的**小横幅预告**（P0-B「30 秒预告 + 三出口」）。
    ///
    /// 三条铁律，任何一条都不能破：
    ///   1. **绝不抢焦点**（WS_EX_NOACTIVATE + ShowWithoutActivation）——
    ///      抢焦点会打断用户正在进行的输入法组词（未上屏内容丢失）。
    ///   2. **绝不装键盘钩子** —— 预告期间用户的键盘完全不受影响。
    ///      这是"预告不打扰"的前提，也是它与"严格模式"解耦的地方：
    ///      严格模式的吞键只发生在真正的休息遮罩期间，预告期一律零键盘干预。
    ///   3. **只给鼠标出口**（延后 / 跳过 / 立即休息），不做键盘快捷键 ——
    ///      键盘快捷键必然要在钩子里判键，又会把"影响输入"的风险引回来。
    ///
    /// 位置：跟随鼠标所在的那块屏幕（用户正在看的那块），右下角、任务栏之上。
    /// 样式：沿用遮罩的深色配色，让它一眼能认出是 EyeLite，但体量小得多。
    /// </summary>
    internal class PreviewForm : Form
    {
        private Label lblEyebrow;      // 「休息提醒」
        private Label lblBody;         // 一句话说明
        private Label lblCount;        // 剩余秒数（大号等宽数字）
        private Label lblCountUnit;    // 「秒」
        private RoundButton btnPostpone, btnSkip, btnNow;

        private int lastCount = -1;
        private string lastBody = "";
        private GraphicsPath shapePath;
        private Size shapeSize = Size.Empty;

        private float dpiScale = 1f;

        private int Sc(int px)
        {
            return (int)(px * dpiScale + 0.5f);
        }

        public event EventHandler RequestPostpone;
        public event EventHandler RequestSkip;
        public event EventHandler RequestNow;

        public PreviewForm()
        {
            dpiScale = Ui.DetectScale(this);

            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.BackColor = Ui.GradTop;
            this.AutoScaleMode = AutoScaleMode.None;
            this.DoubleBuffered = true;
            this.Font = Ui.FontUi;

            int w = Sc(400);
            int h = Sc(136);

            // ---- 眉标 ----
            lblEyebrow = new Label();
            lblEyebrow.Text = "休息提醒";
            lblEyebrow.ForeColor = Ui.EyebrowText;
            lblEyebrow.Font = Ui.FontRestEyebrow;
            lblEyebrow.TextAlign = ContentAlignment.MiddleLeft;
            lblEyebrow.AutoSize = false;
            lblEyebrow.BackColor = Color.Transparent;
            lblEyebrow.SetBounds(Sc(20), Sc(14), Sc(200), Sc(24));
            this.Controls.Add(lblEyebrow);

            // ---- 倒计时数字（大号）+ 单位 ----
            lblCount = new Label();
            lblCount.ForeColor = Ui.TimeText;
            lblCount.Font = Ui.GetTimeFont(Sc(38));
            lblCount.TextAlign = ContentAlignment.MiddleRight;
            lblCount.AutoSize = false;
            lblCount.BackColor = Color.Transparent;
            lblCount.SetBounds(w - Sc(96), Sc(12), Sc(56), Sc(44));
            this.Controls.Add(lblCount);

            lblCountUnit = new Label();
            lblCountUnit.Text = "秒";
            lblCountUnit.ForeColor = Ui.RingLabel;
            lblCountUnit.Font = Ui.FontTip;
            lblCountUnit.TextAlign = ContentAlignment.MiddleLeft;
            lblCountUnit.AutoSize = false;
            lblCountUnit.BackColor = Color.Transparent;
            lblCountUnit.SetBounds(w - Sc(38), Sc(24), Sc(24), Sc(20));
            this.Controls.Add(lblCountUnit);

            // ---- 说明文案 ----
            lblBody = new Label();
            lblBody.ForeColor = Ui.HintText;
            lblBody.Font = Ui.FontHint;
            lblBody.TextAlign = ContentAlignment.MiddleLeft;
            lblBody.AutoSize = false;
            lblBody.BackColor = Color.Transparent;
            lblBody.SetBounds(Sc(20), Sc(44), w - Sc(120), Sc(30));
            this.Controls.Add(lblBody);

            // ---- 三出口按钮 ----
            int bw = Math.Max(1, Sc(1));
            int by = Sc(92);
            int bh = Sc(34);

            btnPostpone = new RoundButton("延后", Sc(15),
                                          Ui.DarkBtnBg, Ui.DarkBtnHover,
                                          Ui.DarkBtnText, Ui.DarkBtnBorder, bw);
            btnPostpone.Font = Ui.FontRowLabel;
            btnPostpone.SetBounds(Sc(20), by, Sc(104), bh);
            btnPostpone.Click += delegate
            {
                if (RequestPostpone != null) RequestPostpone(this, EventArgs.Empty);
            };
            this.Controls.Add(btnPostpone);

            btnSkip = new RoundButton("跳过", Sc(15),
                                      Ui.DarkBtnBg, Ui.DarkBtnHover,
                                      Ui.DarkBtnText, Ui.DarkBtnBorder, bw);
            btnSkip.Font = Ui.FontRowLabel;
            btnSkip.SetBounds(Sc(134), by, Sc(84), bh);
            btnSkip.Click += delegate
            {
                if (RequestSkip != null) RequestSkip(this, EventArgs.Empty);
            };
            this.Controls.Add(btnSkip);

            // 「立即休息」是主动作：描边用强调色，视觉上区别于两个次要出口
            btnNow = new RoundButton("立即休息", Sc(15),
                                     Ui.DarkBtnBg, Ui.DarkBtnHover,
                                     Ui.SkipText, Ui.Accent, bw);
            btnNow.Font = Ui.FontRowLabel;
            btnNow.SetBounds(Sc(228), by, Sc(152), bh);
            btnNow.Click += delegate
            {
                if (RequestNow != null) RequestNow(this, EventArgs.Empty);
            };
            this.Controls.Add(btnNow);

            this.Size = new Size(w, h);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab、不占任务栏
                return cp;
            }
        }

        // ---- 圆角：Region 裁形状（与 RoundPanel 同一手法）----
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RebuildShape();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RebuildShape();
        }

        private void RebuildShape()
        {
            Size s = this.ClientSize;
            if (s.Width <= 4 || s.Height <= 4) return;
            if (shapePath != null && shapeSize.Width == s.Width && shapeSize.Height == s.Height) return;

            int r = Sc(12);
            int maxR = Math.Min(s.Width, s.Height) / 2 - 1;
            if (r > maxR) r = maxR;
            if (r < 2) r = 2;

            if (shapePath != null) { shapePath.Dispose(); shapePath = null; }
            shapeSize = s;
            shapePath = Ui.RoundedRect(new Rectangle(0, 0, s.Width - 1, s.Height - 1), r);

            Region old = this.Region;
            this.Region = new Region(shapePath);
            if (old != null) old.Dispose();
        }

        /// <summary>把横幅移到指定屏幕的右下角（任务栏之上）。</summary>
        public void MoveToScreen(Screen screen)
        {
            try
            {
                Rectangle wa = screen.WorkingArea;
                int x = wa.Right - this.Width - Sc(24);
                int y = wa.Bottom - this.Height - Sc(16);
                if (x < wa.Left + Sc(8)) x = wa.Left + Sc(8);
                if (y < wa.Top + Sc(8)) y = wa.Top + Sc(8);
                this.Location = new Point(x, y);
            }
            catch { }
        }

        /// <summary>更新倒计时与文案。值没变就完全不重绘（每秒调用一次，零分配）。</summary>
        public void SetCountdown(int seconds, string body)
        {
            if (seconds < 0) seconds = 0;
            if (seconds == lastCount && body == lastBody) return;
            lastCount = seconds;
            lastBody = body;
            if (lblCount != null) lblCount.Text = seconds.ToString();
            if (lblBody != null) lblBody.Text = body;
        }

        /// <summary>设置三个出口的文案（推迟按钮要显示具体分钟数）。</summary>
        public void SetPostponeText(string text)
        {
            if (btnPostpone != null && btnPostpone.Text != text)
            {
                btnPostpone.Text = text;
                btnPostpone.Invalidate();
            }
        }

        public void ShowPreview()
        {
            try
            {
                this.Opacity = 0.96;
                this.Visible = true;
                this.TopMost = true;
            }
            catch
            {
                try { this.Visible = true; } catch { }
            }
        }

        public void HidePreview()
        {
            try { this.Visible = false; } catch { }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            try { g.Clear(Ui.GradTop); }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (shapePath != null) { shapePath.Dispose(); shapePath = null; }
                Region reg = this.Region;
                base.Dispose(disposing);
                if (reg != null) reg.Dispose();
                return;
            }
            base.Dispose(disposing);
        }
    }

    // ================================================================
    // 5.5 渐进降饱和遮罩（A2：休息前 60 秒整体渐暗）
    // ================================================================
    //
    // 为什么用整窗 Opacity 而不是"像素级变暗"：
    //   像素级必须先把屏幕抓成位图，3840x2160x4 ≈ 33MB，且每次半透明合成还要再一份，
    //   直接违反文件头第 5 条硬约束（绝不缓存全屏位图）。
    //   整窗 Opacity 走 DWM 合成，进程内零额外内存。
    //
    // 三条安全要求（缺一条就会变成"打扰"而不是"提示"）：
    //   · 永不成前台：WS_EX_NOACTIVATE，ShowWithoutActivation
    //   · 不接收键鼠：WS_EX_TRANSPARENT，鼠标穿透到下面的程序
    //   · 不进 Alt+Tab / 不占任务栏：WS_EX_TOOLWINDOW + ShowInTaskbar=false
    // 另外任何键鼠输入都要由调用方立刻隐藏（见 TrayApp.DimTick）——
    // 一个"变暗后还撤销不掉"的窗口对正在打字的人来说是无法接受的。
    internal class DimForm : Form
    {
        public DimForm(Rectangle bounds)
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = bounds;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.BackColor = Color.Black;
            this.AutoScaleMode = AutoScaleMode.None;
            // 构造期就把 Opacity 压到 0：
            //   · 让 CreateParams 带上 WS_EX_LAYERED（首帧不会闪一帧全黑）
            //   · Opacity 只能在句柄创建后才有意义，所以调用方 Show() 之后会再设一次真实值
            try { this.Opacity = 0.0; }
            catch { }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab、不占任务栏
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT：鼠标穿透，不拦截任何点击
                return cp;
            }
        }

        /// <summary>
        /// 安全地设强度：alpha 一律钳进 [0, 1]，并吞掉"句柄还没建好"这类异常。
        /// 调用方（TrayApp / 设置页预览）都不希望一个 UI 细节打断主流程。
        /// </summary>
        public void SetAlpha(double alpha)
        {
            if (alpha < 0.0) alpha = 0.0;
            if (alpha > 1.0) alpha = 1.0;
            try
            {
                if (!this.IsHandleCreated) return;   // 没句柄就先不动，等 Show 之后再设
                this.Opacity = alpha;
            }
            catch { }
        }
    }

    // ================================================================
    // 6. 设置窗体（浅色卡片式）
    // ================================================================
    internal class SettingsForm : Form
    {
        private NumericUpDown numWork, numRest, numPomoWork, numPomoRest,
                              numSedMinutes, numSedSeconds, numPostpone, numIdle, numPreview, numDim;
        private CheckBox chkPomodoro, chkSedentary, chkAllowSkip, chkSound, chkStrict, chkAutoStart,
                          chkSuppressFullscreen;

        // ---- A2「预览渐暗」：3 秒淡入 + 1 秒保持 + 1 秒淡出，用完立刻 Dispose ----
        // 只为这几秒创建 DimForm，绝不常驻；关掉对话框（Dispose）也会一起收干净。
        private System.Windows.Forms.Timer dimPreviewTimer;
        private List<DimForm> dimPreviewForms;
        private int dimPreviewStep;
        private float dimPreviewMax;            // 本次预览的目标 alpha（0~1）

        // 版式常量（以下全部是 96dpi 下的设计值，运行时一律经 Sc() 乘 DPI 因子）
        // 设计基准客户区 520 x 1018：
        //   88(页头) + 210(节奏，4 行 + 预览按钮) + 16 + 144(番茄钟) + 16 + 144(久坐与活动) + 16
        //   + 304(行为，8 行) + 24 + 38(按钮) + 18(底部留白) = 1018
        //
        // 这个数字只是"内容的自然高度"，不是硬指标：窗口放不下时由 AutoScroll 兜住，
        // 所以再加行只要同步改这里 + 末尾的 AutoScrollMinSize，不会出现"按钮被切掉"。
        private const int FormW = 520;
        private const int FormH = 1018;
        private const int CardLeft = 24;
        private const int CardWidth = 472;      // 520 - 24 * 2
        private const int CardPad = 22;         // 卡片左右内边距
        private const int CardTitleTop = 14;
        private const int FirstRowTop = 38;
        private const int RowHeight = 32;       // 所有行（复选 / 数值）统一 32
        private const int CheckHeight = 26;
        private const int NumWidth = 96;
        private const int NumHeight = 30;
        private const int Gap = 16;

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

            if (clientH > maxH) clientH = maxH;

            this.ClientSize = new Size(clientW, clientH);

            // AutoScroll 常开（不再只在"窗口被钳高"时才开）。
            // 卡片还会继续长（A2 的「渐暗强度」行 + 「预览渐暗」按钮就是这么加上来的），
            // 靠滚动兜住内容，而不是靠调大 FormH 硬撑 —— 硬撑的下一步一定是溢出。
            // 放得下时 AutoScrollMinSize 小于客户区，滚动条根本不出现，完全无感；
            // 真正的滚动范围在构造函数末尾按 btnY 算出来再设（那时才知道内容多高）。
            this.AutoScroll = true;

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
            // 高度按字体实测：19pt 粗体的行高随 DPI 变化，写死 28px 会裁掉字形下缘
            // （2026-11-14 实测，150% 机器上被裁掉约一成）。
            int titleH = TextRenderer.MeasureText(lblTitle.Text, lblTitle.Font).Height;
            lblTitle.SetBounds(Sc(CardLeft), Sc(48), Sc(340), titleH + Sc(4));
            this.Controls.Add(lblTitle);

            // 右侧版本号：包进一个浅色 chip，与标题同一视觉行。
            // chip 右缘仍贴 Sc(CardLeft) + Sc(CardWidth)，label 在 chip 内右对齐、左右各留 Sc(8)。
            RoundPanel chipVersion = new RoundPanel(Sc(8),
                                                    Color.FromArgb(0xEE, 0xF2, 0xF8),
                                                    Color.FromArgb(0xE3, 0xE9, 0xF2));
            int chipW = Sc(64);
            int chipH = Sc(22);
            // 原 label 在 Sc(52) 处高 Sc(18)，中心 61；chip 高 22，上移 2px 保持同一视觉行
            chipVersion.SetBounds(Sc(CardLeft) + Sc(CardWidth) - chipW, Sc(50), chipW, chipH);
            this.Controls.Add(chipVersion);

            Label lblVersion = new Label();
            lblVersion.Text = Ui.AppVersion;
            lblVersion.Font = Ui.FontEyebrow;
            lblVersion.ForeColor = Ui.CaptionText;
            lblVersion.BackColor = Color.FromArgb(0xEE, 0xF2, 0xF8);   // chip 底色，不用透明（见 AddCard 注释）
            lblVersion.AutoSize = false;
            lblVersion.TextAlign = ContentAlignment.MiddleRight;
            lblVersion.SetBounds(Sc(8), 0, chipW - Sc(16), chipH);
            chipVersion.Controls.Add(lblVersion);

            // ---------- 卡片 1：节奏（4 行 + 预览按钮）----------
            // 卡片高度：FirstRowTop(38) + 4 行 * 32 + 按钮行 30 + 底部留白 ≈ 210
            int y = Sc(88);
            RoundPanel card1 = AddCard(y, Sc(210), "节奏");
            numWork = AddRow(card1, Sc(FirstRowTop), "工作时长（分钟）", 1, 240, cfg.WorkMinutes);
            numRest = AddRow(card1, Sc(FirstRowTop) + Sc(RowHeight), "休息时长（秒）", 5, 1800, cfg.RestSeconds);
            // 预告窗口（P0-B）：0 = 不预告，直接到点全屏遮罩。
            // 放在「节奏」卡而不是「行为」卡，因为它改的是提醒**时机**而不是行为约束。
            numPreview = AddRow(card1, Sc(FirstRowTop) + Sc(RowHeight) * 2, "提前预告（秒，0=不预告）", 0, 300, cfg.PreviewSeconds);
            // A2 渐进降饱和：0 = 关闭（一个窗口都不创建），上限 70 见 AppConfig.DimAlphaMax 的注释。
            numDim = AddRow(card1, Sc(FirstRowTop) + Sc(RowHeight) * 3, "休息前渐暗强度（%，0=关闭）", 0, 70, cfg.DimAlphaMax);

            // 现场感受强度：3 秒淡入 → 保持 1 秒 → 1 秒淡出。
            // 光看"45%"这个数字没人能判断合不合适，必须让人当场看一遍。
            RoundButton btnDimPreview = new RoundButton("预览渐暗", Sc(8),
                                                       Ui.CardBg, Ui.SecondaryHover,
                                                       Ui.LabelText, Ui.SecondaryBorder, Math.Max(1, Sc(1)));
            btnDimPreview.Font = Ui.FontRowLabel;
            btnDimPreview.SetBounds(Sc(CardPad), Sc(FirstRowTop) + Sc(RowHeight) * 4, Sc(120), Sc(30));
            btnDimPreview.Click += delegate { StartDimPreview(); };
            card1.Controls.Add(btnDimPreview);

            y += Sc(210) + Sc(Gap);

            // ---------- 卡片 2：番茄钟（3 行）----------
            RoundPanel card2 = AddCard(y, Sc(144), "番茄钟");
            chkPomodoro = AddCheck(card2, Sc(FirstRowTop), "启用番茄钟模式", cfg.PomodoroEnabled);
            numPomoWork = AddRow(card2, Sc(FirstRowTop) + Sc(RowHeight), "番茄工作（分钟）", 1, 240, cfg.PomodoroWork);
            numPomoRest = AddRow(card2, Sc(FirstRowTop) + Sc(RowHeight) * 2, "番茄休息（分钟）", 1, 120, cfg.PomodoroRest);
            y += Sc(144) + Sc(Gap);

            // ---------- 卡片 3：久坐与活动（3 行）----------
            // 第二套时钟：与护眼节奏独立，久坐到点触发的是"活动休息"（正文案见 TrayApp.BeginRest）
            RoundPanel cardSed = AddCard(y, Sc(144), "久坐与活动");
            chkSedentary = AddCheck(cardSed, Sc(FirstRowTop), "启用久坐提醒（每小时起身活动）", cfg.SedentaryEnabled);
            numSedMinutes = AddRow(cardSed, Sc(FirstRowTop) + Sc(RowHeight), "久坐间隔（分钟）", 15, 180, cfg.SedentaryMinutes);
            numSedSeconds = AddRow(cardSed, Sc(FirstRowTop) + Sc(RowHeight) * 2, "活动时长（秒）", 30, 300, cfg.SedentarySeconds);
            y += Sc(144) + Sc(Gap);

            // ---------- 卡片 4：行为（8 行）----------
            RoundPanel card3 = AddCard(y, Sc(304), "行为");
            chkAllowSkip = AddCheck(card3, Sc(FirstRowTop), "允许跳过 / 推迟休息", cfg.AllowSkip);
            numPostpone = AddRow(card3, Sc(FirstRowTop) + Sc(RowHeight), "推迟时长（分钟）", 1, 60, cfg.PostponeMinutes);
            numIdle = AddRow(card3, Sc(FirstRowTop) + Sc(RowHeight) * 2, "离开多少分钟后暂停计时", 0, 120, cfg.IdleMinutes);
            chkSound = AddCheck(card3, Sc(FirstRowTop) + Sc(RowHeight) * 3, "开始 / 结束都播放提示音", cfg.SoundEnabled);
            chkStrict = AddCheck(card3, Sc(FirstRowTop) + Sc(RowHeight) * 4,
                                 "严格模式：休息期间锁住键盘，Esc / 空格退出（会中断输入）", cfg.StrictMode);
            // P0-C：系统状态闸门（FullscreenDndGate）。
            // 文案里写清"只拦全屏 / 演示"：Win11 的系统免打扰（QUNS_QUIET_TIME）
            // 在很多机器上是常开的，把它也算进去会让本工具等于永不提醒。
            chkSuppressFullscreen = AddCheck(card3, Sc(FirstRowTop) + Sc(RowHeight) * 5,
                                 "全屏 / 演示时暂缓提醒（最多暂缓 30 分钟）", cfg.SuppressOnFullscreen);
            chkAutoStart = AddCheck(card3, Sc(FirstRowTop) + Sc(RowHeight) * 6,
                                    "开机自动启动", cfg.StartWithWindows);
            // G14 预留：只放一个禁用的开关占位，**不写回配置**（cfg.CameraCountEnabled 保持原值），
            // 避免用户以为勾上就生效。v1.2 有了 CameraBodyCounter 再解禁并接 IBodyCounter。
            // 用局部变量而不是字段：没有读取方，留字段只会多一个"永远用不到"的状态位。
            CheckBox chkCamera = AddCheck(card3, Sc(FirstRowTop) + Sc(RowHeight) * 7, "摄像头计数（暂未开放）", false);
            chkCamera.Enabled = false;

            // ---------- 卡片 5：统计（只在 Plugin_Stats 启用时出现）----------
            // 关闭时**整张卡都不创建**：默认界面必须与之前逐像素一致，
            // 所以 btnY 的算法分成两支，else 分支就是原来那一行，一个字都没改。
            int btnY;
            if (cfg.Plugin_Stats)
            {
                int statTop = y + Sc(304) + Sc(Gap);
                RoundPanel cardStat = AddCard(statTop, Sc(168), "统计");

                AddInfoRow(cardStat, Sc(FirstRowTop), "完成 " + cfg.StatDone + " 次", Ui.LabelText);
                AddInfoRow(cardStat, Sc(FirstRowTop) + Sc(RowHeight) * 1, "跳过 " + cfg.StatSkip + " 次", Ui.LabelText);
                AddInfoRow(cardStat, Sc(FirstRowTop) + Sc(RowHeight) * 2, "忽略 " + cfg.StatIgnore + " 次", Ui.LabelText);
                // 这句不是免责声明套话：三态计数非常容易被读成"用眼健康评分"，
                // 必须明确这不是健康判断，否则就是在制造焦虑。
                AddInfoRow(cardStat, Sc(FirstRowTop) + Sc(RowHeight) * 3,
                           "这不是健康诊断，只记录发生过的次数", Ui.CaptionText);

                btnY = statTop + Sc(168) + Sc(24);
            }
            else
            {
                // ---------- 底部按钮：最后一排卡片底部 + Sc(24) ----------
                btnY = y + Sc(304) + Sc(24);
            }

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

            // 真正的滚动范围在这里才定得下来：内容总高 = 保存按钮底边 + 底部留白。
            // 宽度传 0 = 水平范围跟随客户区（见构造函数开头 AutoScroll 处的原因）。
            this.AutoScrollMinSize = new Size(0, btnY + Sc(38) + Sc(18));
        }

        // ================================================================
        // A2「预览渐暗」：让用户当场看一遍强度，而不是对着一个百分数猜
        // ================================================================
        //
        // 节奏：3 秒淡入 → 保持 1 秒 → 1 秒淡出（50ms 一档）。
        // 只用整窗 Opacity，不做像素级处理；每块屏幕一个 DimForm，用完立刻 Dispose。

        private const int DimPreviewInterval = 50;      // 定时器间隔（毫秒）
        private const int DimPreviewFadeInSteps = 60;   // 3 秒淡入
        private const int DimPreviewHoldSteps = 20;     // 1 秒保持
        private const int DimPreviewFadeOutSteps = 20;  // 1 秒淡出

        private void StartDimPreview()
        {
            StopDimPreview();

            // 0 = 关闭该功能：不弹任何窗口（与运行时行为一致，不给用户"其实生效了"的错觉）
            int pct = (int)numDim.Value;
            if (pct <= 0) return;

            dimPreviewMax = (float)(pct / 100.0);

            EnsureDimPreviewForms();
            if (dimPreviewForms == null || dimPreviewForms.Count == 0) return;

            dimPreviewStep = 0;
            dimPreviewTimer = new System.Windows.Forms.Timer();
            dimPreviewTimer.Interval = DimPreviewInterval;
            dimPreviewTimer.Tick += delegate { DimPreviewTick(); };
            dimPreviewTimer.Start();
        }

        /// <summary>每块屏幕一个渐暗窗口；只在预览期间存在，结束即 Dispose。</summary>
        private void EnsureDimPreviewForms()
        {
            if (dimPreviewForms != null) return;

            dimPreviewForms = new List<DimForm>();
            try
            {
                Screen[] screens = Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    DimForm f = new DimForm(screens[i].Bounds);
                    f.Show();
                    f.SetAlpha(0.0);      // Show 之后句柄一定在，这里把 0 值真正写进去
                    dimPreviewForms.Add(f);
                }
            }
            catch { }
        }

        private void DimPreviewTick()
        {
            dimPreviewStep++;

            double alpha;
            if (dimPreviewStep <= DimPreviewFadeInSteps)
            {
                alpha = dimPreviewMax * dimPreviewStep / DimPreviewFadeInSteps;
            }
            else if (dimPreviewStep <= DimPreviewFadeInSteps + DimPreviewHoldSteps)
            {
                alpha = dimPreviewMax;
            }
            else if (dimPreviewStep <= DimPreviewFadeInSteps + DimPreviewHoldSteps + DimPreviewFadeOutSteps)
            {
                int left = DimPreviewFadeInSteps + DimPreviewHoldSteps + DimPreviewFadeOutSteps - dimPreviewStep;
                alpha = dimPreviewMax * left / DimPreviewFadeOutSteps;
            }
            else
            {
                StopDimPreview();
                return;
            }

            if (dimPreviewForms == null) return;
            for (int i = 0; i < dimPreviewForms.Count; i++)
            {
                DimForm f = dimPreviewForms[i];
                if (f == null || f.IsDisposed) continue;
                f.SetAlpha(alpha);
            }
        }

        /// <summary>停掉预览并把窗口收干净（重复调用安全）。</summary>
        private void StopDimPreview()
        {
            if (dimPreviewTimer != null)
            {
                try { dimPreviewTimer.Stop(); } catch { }
                try { dimPreviewTimer.Dispose(); } catch { }
                dimPreviewTimer = null;
            }

            if (dimPreviewForms != null)
            {
                for (int i = 0; i < dimPreviewForms.Count; i++)
                {
                    DimForm f = dimPreviewForms[i];
                    if (f == null) continue;
                    try { f.SetAlpha(0.0); } catch { }
                    try { f.Hide(); } catch { }
                    try { f.Close(); } catch { }
                    try { f.Dispose(); } catch { }
                }
                dimPreviewForms.Clear();
                dimPreviewForms = null;
            }

            dimPreviewStep = 0;
        }

        protected override void Dispose(bool disposing)
        {
            // 对话框被直接关掉（Esc / 取消）时，预览窗口也必须一起收掉 ——
            // 否则屏幕会一直暗着，而且没人能再把它关掉。
            if (disposing) StopDimPreview();
            base.Dispose(disposing);
        }

        /// <summary>
        /// 严格模式的说明文字随「允许跳过」联动。
        /// 构造期间控件可能还没建好，必须判空。
        ///
        /// 文案原则（P0-E）：必须让用户**一眼明白后果**。
        /// "屏蔽整个键盘"听起来像个开关描述，"锁住键盘…会中断输入"才说清了代价 ——
        /// 用户反馈过"Ctrl 一直卡住"，那就别让任何人稀里糊涂地打开它。
        /// </summary>
        private void UpdateStrictText()
        {
            if (chkStrict == null || chkAllowSkip == null) return;

            chkStrict.Text = chkAllowSkip.Checked
                ? "严格模式：休息期间锁住键盘，Esc / 空格退出（会中断输入）"
                : "严格模式：休息期间只拦系统键，键盘仍可用（不影响输入）";
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

            // 数值框去 3D 边框：WinForms 默认的 FixedSingle + 系统主题边框是设置页里
            // 唯一还带立体感的东西。改成 BorderStyle.None + 圆角容器描边。
            //
            // 容器右缘 = 原来数字框的右缘（右对齐位置一点没动），数字框作为子控件内嵌：
            //   水平各留 Sc(6)（容器宽 = NumWidth + Sc(12)）
            //   垂直不留 6：行槽只有 Sc(32)，容器做到 Sc(38) 会压到相邻行的文字，
            //   所以容器高度取满 RowHeight，数字框在其中垂直居中（96dpi 下上下各 1px）。
            int numRight = Sc(CardWidth) - Sc(CardPad) - 1;
            int boxW = Sc(NumWidth) + Sc(12);
            int boxH = Sc(RowHeight);

            RoundPanel box = new RoundPanel(Sc(10), Ui.InputBg, Ui.InputBorder);
            box.SetBounds(numRight - boxW, top, boxW, boxH);
            card.Controls.Add(box);

            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = val;
            n.Font = Ui.FontRowLabel;
            n.BorderStyle = BorderStyle.None;
            n.TextAlign = HorizontalAlignment.Right;
            n.BackColor = Ui.InputBg;
            n.AutoSize = false;
            int numH = Sc(NumHeight);
            int numY = top + (boxH - numH) / 2;
            n.SetBounds(numRight - boxW + Sc(6), numY, Sc(NumWidth), numH);
            box.Controls.Add(n);
            return n;
        }

        private CheckBox AddCheck(RoundPanel card, int top, string text, bool val)
        {
            CheckBox c = new FlatCheckBox();     // 自绘：去掉系统主题的 3D 勾选框
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

        /// <summary>
        /// 只读信息行（统计卡用）。行槽、字号、缩进都与 AddRow 一致，
        /// 所以统计卡和上面的设置卡在视觉上是同一条网格。
        /// </summary>
        private void AddInfoRow(RoundPanel card, int top, string text, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Ui.FontRowLabel;
            l.ForeColor = color;
            l.BackColor = Ui.CardBg;
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.SetBounds(Sc(CardPad), top + Sc(4), Sc(CardWidth) - Sc(CardPad) * 2, Sc(24));
            card.Controls.Add(l);
        }

        private void ApplyTo(AppConfig cfg)
        {
            cfg.WorkMinutes = (int)numWork.Value;
            cfg.RestSeconds = (int)numRest.Value;
            cfg.PomodoroEnabled = chkPomodoro.Checked;
            cfg.PomodoroWork = (int)numPomoWork.Value;
            cfg.PomodoroRest = (int)numPomoRest.Value;
            cfg.SedentaryEnabled = chkSedentary.Checked;
            cfg.SedentaryMinutes = (int)numSedMinutes.Value;
            cfg.SedentarySeconds = (int)numSedSeconds.Value;
            cfg.AllowSkip = chkAllowSkip.Checked;
            cfg.PostponeMinutes = (int)numPostpone.Value;
            cfg.IdleMinutes = (int)numIdle.Value;
            cfg.PreviewSeconds = (int)numPreview.Value;
            cfg.DimAlphaMax = (int)numDim.Value;
            cfg.SuppressOnFullscreen = chkSuppressFullscreen.Checked;
            cfg.SoundEnabled = chkSound.Checked;
            cfg.StrictMode = chkStrict.Checked;
            cfg.StartWithWindows = chkAutoStart.Checked;
            // chkCamera 故意不写回：占位开关（Enabled=false），写回只会造出"配置说开了、
            // 实际没实现"的假状态。
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

        // ---- 计时状态（契约 §4：单调锚点，Timer 只负责重算 + 重绘）----
        //
        // 三套时钟的形态**不一样**，因为它们的"冻结"需求不一样：
        //
        //   工作/久坐：accum 形态（活跃时长累加）
        //     为什么不用"到期时刻锚点"：那样一暂停或一空闲，锚点还在往前走，
        //     恢复的瞬间就会判定到期 —— 旧的 tick 累加写法靠"暂停时直接 return"
        //     天然冻结了计时，换成锚点必须显式处理，否则是行为回归。
        //     accum 形态把"该不该累加"显式写出来：暂停 / 空闲 / 休息中都不累加。
        //
        //   休息：restDueMs 绝对锚点
        //     休息倒计时**必须一直走**（契约 §5：休息中途人走开了，那是"休息"不是"空闲"），
        //     结束时刻是绝对的，不需要冻结语义。
        //
        // 剩余秒数全部由 accum / 锚点**算出来**，不存"计数器"。
        // 旧写法 workRemaining-- 是 tick 累加：Timer 迟到一次就少一秒，
        // 机器睡一觉醒来计数器会悄悄落后 —— 调研文档反模式 5 说的就是这个。
        private long workAccumMs = 0;        // 护眼/番茄钟：已累计的活跃毫秒
        private long sedAccumMs = 0;         // 久坐：已累计的活跃毫秒
        private long workLimitMs = 0;        // 护眼：当前到期阈值（推迟时会被延长）
        private long sedLimitMs = 0;         // 久坐：当前到期阈值（推迟时会被延长）
        private long lastAnchorMs = 0;       // 上次 tick 的单调时刻，用于算 dt
        private DateTime lastTickUtc = DateTime.UtcNow;  // 上次 tick 的墙钟时刻，用于休眠补洞
        private long idleGapSeconds = 0;     // 累计补记的空闲秒数（休眠/卡顿期间人不在）
        private long restDueMs = 0;          // 当前这次休息的结束时刻（绝对锚点）
        private int restTotalSeconds = 0;    // 本次休息总时长（秒），给 UI 用
        private int restRemaining = 0;        // 本次休息剩余秒数（每次 tick 重算后给 UI 用）
        private int workRemaining = 0;        // 工作剩余秒数（每次 tick 重算后给 UI 用）
        private int sedRemaining = 0;         // 久坐剩余秒数（每次 tick 重算后给 UI 用）
        private int lastShown = -1;

        // ---- 预告状态（P0-B）----
        // 到点前 PreviewSeconds 秒，横幅出现，给三个出口：延后 / 跳过 / 立即休息。
        // 预告期间**不装键盘钩子、不吞任何按键**，鼠标点按钮才生效。
        private bool isPreviewing = false;
        private bool previewIsBody = false;     // 预告的是"活动一下"还是普通护眼休息
        private int previewRemaining = 0;
        private PreviewForm previewForm;

        // 休息开始时的前台窗口；休息结束若发现前台被我们占了就还回去（安全网）
        private IntPtr savedForeground = IntPtr.Zero;

        // ---- P0-C：系统通知状态闸门（FullscreenDndGate）----
        //
        // 契约 §9：gate **只在"即将开始"这个跃迁点求值**，休息已开始后绝不评估。
        // 契约 §10.1：gate 进核心，可开关不可卸载（cfg.SuppressOnFullscreen）。
        //
        // 抑制期间累加器**继续走**：被抑制的这段时间不该从账本里消失，
        // 解除抑制后到点立刻提醒（该补的护眼时长不会丢）。
        private bool isSuppressed = false;      // 当前是否被全屏/演示抑制
        private long suppressedSinceMs = 0;     // 本次抑制开始时刻
        private long suppressNoticeAtMs = 0;    // 下次弹「已暂缓」横幅的时刻
        private bool suppressForced = false;    // 本轮连续抑制中是否已强制放行过
        private QunsState currentQuns = QunsState.NotForcedOn;

        // ---- C3/C4：到点前的"临门一脚"让路（核心功能）----
        //
        // 两套让路各自计时、互不覆盖：最终截止时刻取**较大**的那个，
        // 因为"正在看片"和"正在打字"是完全可能同时成立的。
        // 每个 since 字段在对应条件消失时归零，所以"累计上限"是按**连续命中**算的。
        private long deferUntilMs = 0;      // 当前让路的截止时刻（0 = 没在让）
        private long c3DeferSinceMs = 0;    // 前台白名单连续命中的起点（10 分钟上限用）
        private long c4DeferSinceMs = 0;    // 连续打字的起点（60 秒上限用）

        // A5：连续"没真正休息"的周期数（跳过 / 推迟 / 被让路都算）
        // 用来编码仪式音的紧迫度，见 RitualRepeatCount()。
        private int cyclesWithoutRest = 0;

        /// <summary>已完成休息次数（自然结束 + 「坐一分钟」）。F1 统计的数据源，暂未展示。</summary>
        public int CompletedRests = 0;

        // ---- F1「忽略」计数的状态机 ----
        // 忽略 = 到点了，但既没坐满也没点跳过，而是被让路 / 被抑制拖过 5 分钟。
        // 必须只记一次：所以要有"本轮已计"的标记，否则每秒都会 +1。
        private long ignoreSinceMs = 0;         // 这次"到点但被拖着"的起点（0 = 没在等）
        private bool ignoreCounted = false;     // 本轮已经记过一次"忽略"

        private List<RestForm> restForms = new List<RestForm>();
        private bool restFormsWarmed = false;   // 是否已在第一个 tick 预热过遮罩窗体
        private string screenSignature = "";
        private Icon iconIdle;
        private Icon iconRest;

        // ---- A3：托盘图标进度环 ----
        //
        // 只在"剩余整分钟数"变化时重绘：每秒换一个 HICON 会让 GDI 句柄涨得比
        // 任何其他路径都快（NotifyIcon 每次换图都要惊动 shell）。
        private int lastIconMinute = -1;

        // 当前这一帧的进度环 Icon。它由 MakeProgressIcon 内部 Clone 出来，
        // **自己拥有 HICON**，所以换新图时必须 Dispose 旧的（否则就是纯泄漏）。
        private Icon lastProgressIcon = null;

        // ---- A2：渐进式降饱和（休息前 60 秒整体渐暗）----
        //
        // 延迟创建：DimAlphaMax == 0（功能关闭）时**一个 DimForm 都不建**。
        private List<DimForm> dimForms;
        private bool dimGaveUp = false;         // 本轮已因键鼠输入放弃，不再出现
        private int dimLastStep = -1;           // 上次应用的档位（-1 = 本轮还没应用过）
        private double dimLastAlpha = -1.0;     // 上次真正写进 Opacity 的值（-1 = 尚未应用）
        private long dimLastIdleMs = -1;        // 上次采样到的 idle 毫秒（用于发现"有新输入"）

        // ---- 测试钩子：抑制上限 ----
        //
        // 环境变量 EYE_LITE_SUPPRESS_MAX_MS 可改写"抑制最多持续多久"，
        // 默认 30 分钟。只在类型初始化时读一次，不读时零影响（与 EYE_LITE_FORCE_QUNS 同一风格）。
        private static readonly long MaxSuppressMs = ReadMaxSuppressMs();

        private static long ReadMaxSuppressMs()
        {
            const long Default = 30L * 60L * 1000L;
            try
            {
                string s = Environment.GetEnvironmentVariable("EYE_LITE_SUPPRESS_MAX_MS");
                if (string.IsNullOrEmpty(s)) return Default;
                long v;
                if (!long.TryParse(s, out v)) return Default;
                if (v <= 0) return Default;
                return v;
            }
            catch { return Default; }
        }

        // ---- 测试钩子：伪造空闲时长 ----
        //
        // 环境变量 EYE_LITE_FAKE_IDLE_MS 设成非负整数后，空闲检测直接返回该值，
        // 让自动化测试能确定性地模拟"用户已离开 N 毫秒"。未设置时走 GetLastInputInfo。
        //
        // 另有一个**运行期可改**的版本 EYE_LITE_FAKE_IDLE_FILE（指向一个文本文件）：
        // 每次取空闲时长都重读文件，用来验证 P0-D-2「离开超过整个休息时长 → 重置周期」
        // 这条 —— 它必须做到"先让空闲很大（触发重置）、再把空闲清零（模拟人回来了）"，
        // 而环境变量在进程启动后就改不了了（Win32 不允许改别的进程的环境变量）。
        private static readonly long FakeIdleMs = ReadFakeIdleMs();
        private static readonly string FakeIdleFile = ReadFakeIdleFile();

        private static long ReadFakeIdleMs()
        {
            try
            {
                string s = Environment.GetEnvironmentVariable("EYE_LITE_FAKE_IDLE_MS");
                if (string.IsNullOrEmpty(s)) return -1;
                long v;
                if (!long.TryParse(s, out v)) return -1;
                if (v < 0) return -1;
                return v;
            }
            catch { return -1; }
        }

        private static string ReadFakeIdleFile()
        {
            try
            {
                string s = Environment.GetEnvironmentVariable("EYE_LITE_FAKE_IDLE_FILE");
                if (string.IsNullOrEmpty(s)) return "";
                return s;
            }
            catch { return ""; }
        }

        /// <summary>读文件钩子当前的空闲毫秒；没设路径 / 文件不在 / 内容非法一律返回 -1（= 不生效）。</summary>
        private static long ReadFakeIdleFromFile()
        {
            if (FakeIdleFile.Length == 0) return -1;
            try
            {
                if (!File.Exists(FakeIdleFile)) return -1;
                string s = File.ReadAllText(FakeIdleFile, Encoding.UTF8).Trim();
                // 脚本写文件可能留 UTF-8 BOM，而 Trim 不把 U+FEFF 当空白。
                if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1).Trim();
                if (s.Length == 0) return -1;
                long v;
                if (!long.TryParse(s, out v)) return -1;
                if (v < 0) return -1;
                return v;
            }
            catch { return -1; }
        }

        public TrayApp()
        {
            cfg = AppConfig.Load();

            // 插件宿主必须先拿到配置：IsOn / Get 都靠它。
            // 这里只存引用，**不会**构造任何插件对象（延迟初始化在 Get 里）。
            PluginHost.Init(cfg);

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

            // 托盘里也能直接切番茄钟：设置页要多点三次才能到，而这是高频开关。
            // 只改配置 + 重建窗体签名，不碰计时状态机（休息中不动，工作剩余时长在
            // ResetWork 里按新的 WorkTotalSeconds 重算）。
            ToolStripMenuItem miPomodoro = new ToolStripMenuItem("番茄钟模式");
            miPomodoro.CheckOnClick = true;
            miPomodoro.Checked = cfg.PomodoroEnabled;
            miPomodoro.Click += delegate
            {
                cfg.PomodoroEnabled = miPomodoro.Checked;
                cfg.Save();
                screenSignature = "";      // 参数变了，下次休息重建窗体（换配色）
                if (!isResting) ResetWork();
                UpdateTrayText();
            };
            menu.Items.Add(miPomodoro);

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

        /// <summary>
        /// 重置两套工作时钟（护眼 / 久坐），累加器归零、阈值重设。
        ///
        /// 契约 §3：跳过 = 满周期重新计时（activeSinceBreak = 0）。
        /// 两套时钟必须**一起**重置：休息结束时如果只重置护眼、久坐累加器还停在过去，
        /// 下一秒就会立刻再次触发久坐提醒。
        /// </summary>
        private void ResetWork()
        {
            workAccumMs = 0;
            sedAccumMs = 0;
            workLimitMs = (long)WorkTotalSeconds * 1000L;
            sedLimitMs = cfg.SedentaryEnabled ? (long)cfg.SedentaryMinutes * 60L * 1000L : 0;
            lastAnchorMs = Clock.NowMs();
            ResetDimRound();       // 新一轮计时：渐暗重新可以出现（上一轮放弃过也不影响这轮）
            ResetDefer();          // C3/C4 让路状态跟着本轮一起归零
            RefreshRemaining();
            UpdateTrayText();
        }

        /// <summary>
        /// 把 A2 渐暗的状态位归零（新一轮计时开始）。
        /// 只归零状态，不建窗口 —— 窗口仍然是"第一次真的要暗时才建"。
        /// </summary>
        private void ResetDimRound()
        {
            dimGaveUp = false;
            dimLastStep = -1;
            dimLastIdleMs = -1;
            HideDim();
        }

        /// <summary>
        /// 按当前累加器重算三个"剩余秒数"（只给 UI 用，不参与计时判定）。
        /// </summary>
        private void RefreshRemaining()
        {
            workRemaining = workLimitMs > 0 ? SecondsLeft(workLimitMs - workAccumMs) : 0;
            sedRemaining = cfg.SedentaryEnabled && sedLimitMs > 0
                ? SecondsLeft(sedLimitMs - sedAccumMs)
                : 0;
            if (isResting) restRemaining = SecondsLeft(restDueMs - Clock.NowMs());

            // 所有会改变剩余时间的路径都会走到这里（首轮 / 每小时 tick / 推迟 / 跳过），
            // 所以进度环只挂在这一个点上，不用在每个分支各写一遍。
            UpdateTrayProgressIcon();
        }

        /// <summary>剩余毫秒转剩余秒（向上取整：还有 1.2 秒时显示 2 秒，用户看到 0 秒就该弹了）。</summary>
        private static int SecondsLeft(long msLeft)
        {
            if (msLeft <= 0) return 0;
            return (int)((msLeft + 999) / 1000);
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

                // 单次 tick 内只取一次 NowMs()（契约 §4）：取两次会产生微小负差，
                // 钳到 0 会让秒数来回跳。
                long now = Clock.NowMs();
                long dt = Clock.DeltaMs(now, lastAnchorMs);
                long wallDelta = (long)(DateTime.UtcNow - lastTickUtc).TotalSeconds;
                lastTickUtc = DateTime.UtcNow;
                lastAnchorMs = now;

                // ---- 休眠补洞（契约 §5）----
                //
                // 机器睡了一觉，dt 可能是几小时。那段时间人不在屏幕前，
                // **绝不能**加进"活跃时长"——否则第二天显示"连续用眼 8 小时"。
                //
                // 双时钟差两条规则（不依赖"QPC 是否跨 S3 停走"这个未验证的系统行为）：
                //   情形一：wallDelta - qpcDelta > 60 → 真休眠（QPC 停走）→ 补 Idle
                //   情形二：qpcDelta > 60          → 进程卡顿/挂起（QPC 继续走）→ 补 Idle
                // 两种硬件行为都能覆盖，不需要事先验证。
                long qpcDeltaSec = dt / 1000;
                bool sleptAway = (wallDelta - qpcDeltaSec > 60) || (qpcDeltaSec > 60);
                if (sleptAway)
                {
                    // 补记的时段一律归 Idle（Paused 语义），不补进活跃时长。
                    // 单次补记封顶 24 小时（契约 §5 兜底第 1 条）。
                    long gap = wallDelta > 0 ? wallDelta : qpcDeltaSec;
                    if (gap > 86400) gap = 86400;
                    idleGapSeconds += gap;
                    // 锚点重置，让下一 tick 从 0 开始累加，不把这段补洞时长算进活跃时长。
                    dt = 0;
                }

                if (isResting)
                {
                    // 休息结束判定看绝对锚点，不看"减一"：Timer 迟到或系统卡顿时，
                    // 休息时间照样是真实流逝的那几秒。
                    // 休息期间**不累加**工作时钟（契约 §5：休息段单独成桶）。
                    if (now >= restDueMs)
                    {
                        // 自然坐满 = 真正的休息：完成数 +1，A5 的"连续没休息"清零
                        CompletedRests++;
                        cyclesWithoutRest = 0;
                        CountStatDone();        // F1：完成 +1
                        EndRest(true);
                        return;
                    }
                    RefreshRemaining();
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

                    // B1/B2 仪式小窗必须压在这层遮罩之上。
                    // 顺序是关键：上面那次 SetWindowPos 会把每块遮罩依次送到置顶组的最前面，
                    // 所以仪式窗必须在**之后**再抬一次，否则它会在 1 秒内被遮罩盖掉。
                    try { BringRitualToTop(); } catch { }
                }
                else
                {
                    // 到点判定看**累加器**，不做 tick 累加（契约 §4 / 调研反模式 5）。
                    // 累加器只在"真的在工作"时前进：暂停 / 空闲 / 休息中都不加。
                    //
                    // dt 的口径：now - lastAnchorMs。Timer 迟到时 dt 会大于 1 秒，
                    // 这正是我们想要的——真实流逝了多少就加多少，不会因为迟到而少算。
                    // 但机器睡了一觉醒来，dt 会是几小时：那段时间人不在屏幕前，
                    // 不该算进"活跃时长"。所以上面已经把休眠段扣掉了（dt = 0）。
                    bool idle = IsIdleTooLong();

                    // ---- D2：空闲超过整个休息时长 → 当作已经休息过了，直接重置周期 ----
                    // 调研文档 D2 的原话："idle 超过整个休息时长 → 当作已休息，直接重置周期"。
                    // 不这么做会怎样：用户开完 40 分钟会（空闲 40 分钟 > 休息 20 秒），
                    // 回来时累加器已经满 → 立刻弹一次休息。但人**已经休息了 40 分钟**，
                    // 这一弹纯属打扰，还会让人"刚坐下就被弹"。
                    if (idle && IdleSeconds() >= RestTotalSeconds)
                    {
                        // 离开的时间已经超过一整个休息时长 —— 等同于休息过了，
                        // A5 的"连续没休息"也要清零（否则回来第一次预告就会莫名响 3 声）。
                        cyclesWithoutRest = 0;
                        ResetWork();
                        UpdateTrayText();
                        return;
                    }

                    if (!paused && !idle)
                    {
                        workAccumMs += dt;
                        if (cfg.SedentaryEnabled) sedAccumMs += dt;
                    }

                    // 久坐优先：久坐到点时护眼可能还剩一半，
                    // 此时必须出"活动一下"，而不是把久坐额度悄悄吞掉。
                    bool sedDue = cfg.SedentaryEnabled && sedLimitMs > 0 && sedAccumMs >= sedLimitMs;
                    bool workDue = workLimitMs > 0 && workAccumMs >= workLimitMs;

                    // ---- P0-C：系统通知状态闸门（FullscreenDndGate）----
                    //
                    // 契约 §9：gate 只在"即将开始休息"这个跃迁点求值。
                    // 注意分支顺序：**必须先判 isResting / isPreviewing 再判到点**，
                    // 休息已开始后 gate 状态怎么变都不许撤遮罩、都不许延长休息。
                    //
                    // 这里求值的位置在"到点判定之前"：抑制期间累加器继续走，
                    // 但**不弹预告也不弹遮罩**（预览也要一起压住，否则横幅会闪一下）。
                    bool blocked = false;
                    if (cfg.SuppressOnFullscreen)
                    {
                        currentQuns = NotificationState.Query();
                        blocked = NotificationState.IsBlocking(currentQuns);
                    }
                    else
                    {
                        currentQuns = QunsState.NotForcedOn;
                    }

                    if (blocked)
                    {
                        // 被抑制：保持原有计时状态，只是不提醒。
                        // 首次进入抑制时收掉预告横幅（不能预告着然后又不提醒）。
                        if (!isSuppressed)
                        {
                            isSuppressed = true;
                            suppressedSinceMs = now;
                            suppressNoticeAtMs = now;
                            suppressForced = false;
                            if (isPreviewing) EndPreview();
                        }

                        // A2 渐暗层必须在这里撤：下面马上就要 return，
                        // 而渐暗是在正常流程里求值的 —— 不撤就会一直挂在屏幕上，
                        // 压在别人的全屏内容上面（本来"全屏不打扰"就是这个 gate 的意义）。
                        HideDim();

                        // A1 边缘辉光同理：被抑制时不能有任何我们自己的光盖在别人全屏上
                        HideEdgeGlow();

                        // F1：被抑制导致"到点没弹"也算"忽略"的一条来源（连续 5 分钟才记一次）
                        UpdateIgnoreStat(now, sedDue || workDue, true);

                        // 未到上限、且本轮还没强制放行过 → 继续压住。
                        // 注意这里的 return：抑制期间的"不提醒"是靠它实现的。
                        if (!suppressForced && (now - suppressedSinceMs) < MaxSuppressMs)
                        {
                            HandleSuppressedTick(now, sedDue || workDue);
                            RefreshRemaining();
                            UpdateTrayText();
                            return;
                        }

                        // 首次到达上限 → 放行一次（写进日志式的气泡，让用户知道为什么突然弹了）。
                        if (!suppressForced)
                        {
                            suppressForced = true;
                            try
                            {
                                tray.ShowBalloonTip(4000, "护眼小助手",
                                    "全屏 / 演示状态持续过久，已停止暂缓提醒。", ToolTipIcon.Info);
                            }
                            catch { }
                        }

                        // 关键：**不要 return**，也**不要**把 isSuppressed 置 false。
                        // 置 false 会让下一个 tick 的 "if (!isSuppressed)" 又成立，
                        // 于是重新开始计时、再压 30 分钟 —— 那正是"只弹气泡、遮罩永不出现"的旧 bug。
                        // 落到下面的正常流程，到点就直接 BeginRest（契约 §9 的分支顺序仍然成立：
                        // 休息一旦开始就走 isResting 分支，gate 不再求值）。
                    }

                    if (isSuppressed && !blocked)
                    {
                        // 抑制真正解除（QUNS 回到非抑制值）：
                        // 不需要任何"补弹"，因为累加器一直在走，
                        // 下面正常的到点判定会立刻决定要不要弹。
                        // suppressForced 一并复位 —— 下一轮连续抑制重新给足 30 分钟。
                        isSuppressed = false;
                        suppressedSinceMs = 0;
                        suppressForced = false;
                    }

                    // ---- 预热遮罩窗体 ----
                //
                // EnsureRestForms() 原本只在 BeginRest 里调用 —— 也就是**该弹遮罩的那一刻**
                // 才开始建全屏窗体（实测一次性分配 26 个 GDI 对象 + 字体/布局构造）。
                // 代价直接落在这个功能的"准时性"上：实测到点时刻出现过 63.53s / 64.48s
                // （期望 60s ± 3s），而前两轮只要构造快一点就是 60.6s。
                //
                // 这里在第一个 tick 就建好，之后 BeginRest 里那次调用会因为
                // "签名未变且 restForms.Count > 0" 直接返回，不再产生任何分配。
                // 代价是启动基线 GDI 从 21 涨到 47（这是一次性分配，不是泄漏：
                // 实测 185s 内稳定在 47，不再增长）。
                if (!restFormsWarmed)
                {
                    restFormsWarmed = true;
                    try { EnsureRestForms(); } catch { }
                }

                // ---- A2：休息前 60 秒的渐进降饱和（每 tick 一次）----
                    // 求值点必须**在 gate 之后**：放在 tick 顶部的话，用户在上一次 tick
                    // 与本次 tick 之间切进全屏/演示，这一步会先把屏幕压暗，
                    // 下一个 tick 才被 gate 撤掉 —— 表现为"切全屏时闪一下变暗"。
                    // 放在这里时 blocked 已经算完：被压住的路径都 return 或 HideDim 过。
                    // DimTick 内部仍保留 resting / previewing / suppressed / paused 的判据做兜底。
                    try { DimTick(); } catch { }

                    // ---- A1：边缘辉光（插件 EdgeGlow，与渐暗同一时机）----
                    // 未启用时 PluginHost.Get 返回 null → 这里什么都不做，
                    // 连窗口都不会被创建（"未启用零对象"的落点之一）。
                    try { RefreshEdgeGlow(); } catch { }

                    // ---- C3 / C4：到点前的"临门一脚"让路 ----
                    // 每 tick 只算一次（DeferredByActivity 内部会推进"连续命中"的计时）。
                    // 放在 gate 之后：被抑制的路径已经 return 过，没必要白跑一次进程查询。
                    bool deferred = DeferredByActivity(now);

                    // F1：「到点了却被让路」要累计到"忽略"（被抑制的那条路径在前面单独记）。
                    // 放在预告分支之前：预告里到点又被让路的提前 return 也被这句覆盖。
                    UpdateIgnoreStat(now, sedDue || workDue, deferred);

                    // ---- 预告窗口（P0-B）----
                    // 到点前 PreviewSeconds 秒，横幅出现给三出口。
                    // 判定用**剩余时间**而不是绝对时刻：剩余时间进入 [0, 预览窗口] 即进预告。
                    if (isPreviewing)
                    {
                        // 预告期间又到点了（预览窗口内正好到点）→ 直接进休息
                        if (sedDue || workDue)
                        {
                            EndPreview();
                            if (deferred)
                            {
                                // 正在看片 / 正在打字：先把横幅收掉，别挂着；
                                // 让路结束后（deferred 变 false）下面会自己重新进预告或直接开休息。
                                RefreshRemaining();
                                UpdateTrayText();
                                return;
                            }
                            BeginRest(sedDue);
                            return;
                        }
                        // 人走开了 / 暂停了 → 收起横幅，别在没人时挂着
                        if (paused || idle)
                        {
                            EndPreview();
                            RefreshRemaining();
                            UpdateTrayText();
                            return;
                        }
                        UpdatePreviewCountdown();
                        RefreshRemaining();
                        UpdateTrayText();
                        return;
                    }

                    if (sedDue && !deferred) { BeginRest(true); return; }
                    if (workDue && !deferred) { BeginRest(false); return; }

                    // 进预告窗口了吗？
                    // deferred 期间不许再进预告：否则"弹出→到点被让路收掉→下一 tick 又弹"
                    // 会变成每秒闪一次的横幅。
                    if (!paused && !idle && !deferred && cfg.PreviewSeconds > 0)
                    {
                        long win = (long)cfg.PreviewSeconds * 1000L;
                        long workLeft = workLimitMs - workAccumMs;
                        long sedLeft = sedLimitMs - sedAccumMs;

                        bool workWin = workLimitMs > 0 && workLeft <= win;
                        bool sedWin = cfg.SedentaryEnabled && sedLimitMs > 0 && sedLeft <= win;

                        // 两套时钟同时进窗口时，选**更早到点**的那个先预告
                        if (workWin && sedWin)
                        {
                            if (sedLeft <= workLeft) StartPreview(true);
                            else StartPreview(false);
                        }
                        else if (sedWin) StartPreview(true);
                        else if (workWin) StartPreview(false);

                        if (isPreviewing)
                        {
                            RefreshRemaining();
                            UpdateTrayText();
                            return;
                        }
                    }

                    RefreshRemaining();
                    UpdateTrayText();
                }
            }
            catch
            {
                // 自愈：显示器热插拔 / GDI 资源耗尽等异常不能冒泡到 ThreadException，
                // 也不能把用户留在"钩子在跑、遮罩没出来"的状态里。
                try { isResting = false; } catch { }
                try { KeyboardGuard.OnSkipKey = null; } catch { }
                // 自愈场景必须立即脱钩，**不要改成 RequestUninstall()** ——
                // 已经出异常了，再等 keyup 只会把风险窗口拉长到 2 秒。
                try { KeyboardGuard.Uninstall(); } catch { }
                try
                {
                    for (int i = 0; i < restForms.Count; i++)
                    {
                        try { restForms[i].HideRest(); } catch { }
                    }
                }
                catch { }
                try { HideDim(); } catch { }   // A2：自愈时也必须把渐暗层撤掉，否则它会一直盖在屏幕上
                try { tray.Icon = iconIdle; } catch { }
                try { ResetWork(); } catch { }
                // 图标已换成静态眼睛，把"上次绘制的整分钟"也清掉，
                // 否则下一个 tick 若剩余分钟数没变就不会重画进度环，托盘会一直停在眼睛上。
                lastIconMinute = -1;
            }
        }

                /// <summary>
        /// 被系统状态抑制期间的处理（每 tick 调一次）。
        ///
        /// 三条原则：
        ///   1. **不打扰**：不弹预告、不弹遮罩、不出声、不装钩子。
        ///   2. **不丢账**：累加器继续走（在调用方已经累加过了），解除后立刻能到点。
        ///   3. **不让用户以为程序坏了**：最多每 5 分钟给一条托盘气泡，
        ///      说明"因全屏/演示暂缓"，并告知到点已过。
        ///      气泡是 NotifyIcon.ShowBalloonTip —— 不抢焦点、不抢输入。
        ///
        /// 注意：**"到上限强制放行"不在这里**。它必须由调用方（OnTick 的 blocked 分支）
        /// 判断，因为放行的语义是"本 tick 不 return、落到正常到点判定"，
        /// 在方法内部 return 是什么都做不到的 —— 那正是旧 bug 的成因。
        /// 本方法只剩下"每 5 分钟一条告知气泡"这一件事。
        /// </summary>
        private void HandleSuppressedTick(long now, bool dueNow)
        {
            // 到点了但被压住 → 给用户一条明确告知（最多每 5 分钟一条）
            if (dueNow && now - suppressNoticeAtMs >= 5L * 60L * 1000L)
            {
                suppressNoticeAtMs = now;
                string reason = NotificationState.ReasonText(currentQuns);
                try
                {
                    tray.ShowBalloonTip(4000, "护眼小助手",
                        reason + "，到了休息时间，解除后立即提醒。", ToolTipIcon.Info);
                }
                catch { }
            }
        }

        // ================================================================
        // C3 / C4：到点前的"临门一脚"让路
        // ================================================================
        //
        // 这两条和 P0-C 的 QUNS gate 是同一类需求（别在错误时机弹），但覆盖另一批场景：
        //   C3 前台白名单：窗口化看片、PotPlayer 非全屏、OBS 预览 —— QUNS 全都查不到，
        //                  可弹全屏遮罩同样是把人从剧情里硬拽出来。
        //   C4 打字段：用户正在连续敲键（idle < 300ms），这一刻打断的代价是"思路断了"，
        //              晚 5 秒再提醒的代价只是"晚 5 秒"。
        //
        // 两者都不许变成"永不提醒"的后门：C3 连续命中封顶 10 分钟、C4 封顶 60 秒，
        // 到点还没让开就照常弹。

        /// <summary>
        /// 返回 true 表示"这次先不弹"。副作用：更新 deferUntilMs 与两个 since 时间戳。
        /// **每个 tick 只该调一次**（它内部会推进"连续命中"的计时）。
        /// </summary>
        private bool DeferredByActivity(long now)
        {
            // 只有"真的快到了"才值得查一次前台进程（Process 查询 + 一次 P/Invoke 都不便宜）。
            // 离到点还早就直接返回 false，并把连续命中计时清零 —— 否则用户看两小时电影
            // 会白跑七千次进程查询。
            if (!IsNearDue())
            {
                ResetDefer();
                return false;
            }

            long until = 0;

            // ---- C3：前台进程白名单（一次让 60 秒，连续命中累计最多 10 分钟）----
            //
            // c3DeferSinceMs 记的是**第一次命中白名单**的时刻，0 表示"当前没在命中"。
            // 封顶必须用它算，绝不能用 now 反复重置 —— 否则每 tick 都"重新计时"，
            // 10 分钟上限永远到不了，白名单就成了"永不提醒"的后门。
            if (cfg.ForegroundGuardEnabled && IsForegroundWhitelisted())
            {
                if (c3DeferSinceMs == 0) c3DeferSinceMs = now;
                long capEnd = c3DeferSinceMs + 10L * 60L * 1000L;
                long want = now + 60L * 1000L;
                until = want < capEnd ? want : capEnd;
            }
            else
            {
                c3DeferSinceMs = 0;     // 条件一断（离开白名单进程）就重新开始算"连续"
            }

            // ---- C4：正在连续打字（一次让 5 秒，连续命中累计最多 60 秒）----
            if (cfg.TypingGuardEnabled && RawIdleMs() < 300)
            {
                if (c4DeferSinceMs == 0) c4DeferSinceMs = now;
                long capEnd = c4DeferSinceMs + 60L * 1000L;
                long want = now + 5L * 1000L;
                long u2 = want < capEnd ? want : capEnd;
                // 取较大的那个：看片和打字完全可能同时成立，不能让后算的覆盖先算的
                if (u2 > until) until = u2;
            }
            else
            {
                c4DeferSinceMs = 0;
            }

            deferUntilMs = until;
            return deferUntilMs > now;
        }

        /// <summary>
        /// 是否进入"该考虑让路"的时间窗：到点，或已经进了预告窗口。
        /// PreviewSeconds=0 时窗口宽度是 0，只有真正到点才返回 true。
        /// </summary>
        private bool IsNearDue()
        {
            long win = (long)cfg.PreviewSeconds * 1000L;
            if (workLimitMs > 0 && workLimitMs - workAccumMs <= win) return true;
            if (cfg.SedentaryEnabled && sedLimitMs > 0 && sedLimitMs - sedAccumMs <= win) return true;
            return false;
        }

        /// <summary>
        /// 当前前台窗口的进程名是否在白名单里。
        ///
        /// 口径（与测试侧约定一致，改任何一条都会让 C3 用例假 FAIL）：
        ///   · 白名单 = 分号分隔的 **basename，含扩展名**（vlc.exe）
        ///   · 取进程名 = GetProcessById(pid).ProcessName + ".exe"
        ///   · 匹配 = 大小写不敏感（OrdinalIgnoreCase），两侧都 Trim
        ///   · **空字符串 = 白名单为空 = 永不命中**（不是"全部命中"）
        ///   · 取进程名失败 / 进程已退出 / 任何异常 → 视为**不在白名单**（fail-open：
        ///     探测不到就照常提醒，绝不能因为一个查询异常让人永远收不到提醒）
        /// </summary>
        private bool IsForegroundWhitelisted()
        {
            string list = cfg.ForegroundWhitelist;
            // 空 = 永不命中。测试的对照组就靠这个（白名单为空必须照常提醒）
            if (list == null || list.Length == 0) return false;

            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return false;

                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return false;
                if (pid == GetCurrentProcessId()) return false;   // 自己永远不在白名单里

                string name;
                // using 是硬要求：Process 持有进程句柄，不 Dispose 就是"每秒一个句柄"的泄漏
                using (System.Diagnostics.Process p =
                       System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    name = p.ProcessName + ".exe";
                }
                if (name == null || name.Length == 0) return false;

                return MatchWhitelist(list, name);
            }
            catch { return false; }
        }

        /// <summary>
        /// 白名单匹配：分号分隔、逐项 Trim、大小写不敏感（OrdinalIgnoreCase）。
        /// 单独成方法只是为了能一眼看清"匹配只有这一条规则"。
        /// </summary>
        private static bool MatchWhitelist(string list, string exeName)
        {
            string target = exeName == null ? "" : exeName.Trim();
            if (target.Length == 0) return false;

            string[] parts = list.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim();
                if (s.Length == 0) continue;
                if (string.Equals(s, target, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>让路状态归零：一轮真正结束（开始休息 / 跳过 / 推迟 / 重置周期）时调。</summary>
        private void ResetDefer()
        {
            deferUntilMs = 0;
            c3DeferSinceMs = 0;
            c4DeferSinceMs = 0;
        }

        /// <summary>
        /// A5 仪式音的重复次数 = 紧迫度。
        ///
        /// 1 + min(2, 距上次真正完成休息已经过去的完整周期数)，封顶 3 声。
        /// 周期数取两个来源里较大的那个：
        ///   · cyclesWithoutRest —— 跳过 / 推迟 都会 +1（真正的"拖了几轮"）
        ///   · workAccumMs / workLimitMs —— 过点后还在被让路时 ≥1（"这轮已经超时了"）
        /// 只用后者是不够的：跳过会把 workAccumMs 清零，光看它永远算出 0 周期。
        /// </summary>
        private int RitualRepeatCount()
        {
            int cycles = cyclesWithoutRest;

            if (workLimitMs > 0)
            {
                long overdue = workAccumMs / workLimitMs;
                if (overdue > cycles)
                {
                    if (overdue > 2) cycles = 2;
                    else cycles = (int)overdue;
                }
            }

            if (cycles < 0) cycles = 0;
            if (cycles > 2) cycles = 2;
            return 1 + cycles;
        }

        // ================================================================
        // P1 批 2：三个可视化/统计插件的接线（插件本身在自己的类里，这里只做"何时调用"）
        // ================================================================
        //
        // 所有入口都是**先 Get 再判空**：插件没启用时 Get 返回 null，
        // 于是这里连一个对象都不会被创建、一个 Timer 都不会被注册。

        /// <summary>
        /// A1 EdgeGlow 的显示窗口：与 A2 渐暗同一时机 —— 剩余 0~60 秒，
        /// 且不在休息 / 预告 / 抑制 / 暂停 / 长空闲中。
        /// 被 C3/C4 让路时 left ≤ 0，自然也落在窗口外（不会一直亮着）。
        /// </summary>
        private bool GlowWindowOpen()
        {
            if (isResting || isPreviewing || isSuppressed || paused) return false;
            if (IsIdleTooLong()) return false;

            long left, total;
            if (!PickMostUrgentClock(out left, out total)) return false;
            return left > 0 && left <= 60000;
        }

        private void RefreshEdgeGlow()
        {
            EdgeGlowPlugin p = PluginHost.Get("EdgeGlow") as EdgeGlowPlugin;
            if (p == null) return;
            p.SetWindow(GlowWindowOpen());
        }

        private void HideEdgeGlow()
        {
            EdgeGlowPlugin p = PluginHost.Get("EdgeGlow") as EdgeGlowPlugin;
            if (p != null) p.HideNow();
        }

        private void ShowRitualForRest()
        {
            RitualPlugin p = PluginHost.Get("Ritual") as RitualPlugin;
            if (p != null) p.ShowForRest();
        }

        private void HideRitual()
        {
            RitualPlugin p = PluginHost.Get("Ritual") as RitualPlugin;
            if (p != null) p.Hide();
        }

        /// <summary>
        /// 把仪式小窗重新抬到置顶组最前面（休息期间每秒一次）。
        /// 遮罩那边每秒都会重申 TopMost，不抬这一下小窗就会被遮罩盖掉。
        /// </summary>
        private void BringRitualToTop()
        {
            RitualPlugin p = PluginHost.Get("Ritual") as RitualPlugin;
            if (p == null) return;

            IntPtr h = p.WindowHandle;
            if (h == IntPtr.Zero) return;

            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        // ---- F1 计数入口：Get 为 null（插件没启用）时整段都不执行 ----

        private void CountStatDone()
        {
            StatsPlugin p = PluginHost.Get("Stats") as StatsPlugin;
            if (p != null) p.BumpDone(cfg);
        }

        private void CountStatSkip()
        {
            StatsPlugin p = PluginHost.Get("Stats") as StatsPlugin;
            if (p != null) p.BumpSkip(cfg);
        }

        private void CountStatIgnore()
        {
            StatsPlugin p = PluginHost.Get("Stats") as StatsPlugin;
            if (p != null) p.BumpIgnore(cfg);
        }

        /// <summary>
        /// F1「忽略」判定：到点了、但既没坐满也没点跳过（被 C3/C4 让路或被抑制），
        /// **连续**拖过 5 分钟就记一次，同一轮只记一次。
        ///
        /// 每个 tick 只能调一次（它内部推进 ignoreSinceMs）。
        /// 一旦不再"到点且被拖"（弹了遮罩 / 让路结束 / 用户跳过），计时立刻归零 ——
        /// 所以"拖 4 分钟 → 正常弹 → 又拖 4 分钟"不会误记成一次忽略。
        /// </summary>
        private void UpdateIgnoreStat(long now, bool due, bool held)
        {
            if (!due || !held)
            {
                ignoreSinceMs = 0;
                ignoreCounted = false;
                return;
            }

            if (ignoreSinceMs == 0)
            {
                ignoreSinceMs = now;
                ignoreCounted = false;
                return;
            }

            if (!ignoreCounted && (now - ignoreSinceMs) >= 5L * 60L * 1000L)
            {
                ignoreCounted = true;
                CountStatIgnore();
            }
        }

        // ================================================================
        // 预告横幅（P0-B「30 秒预告 + 三出口」）
        // ================================================================
        //
        // 语义定义（三个出口各自对应什么）：
        //   延后      → 与休息遮罩上的「推迟 N 分钟」一致：本轮不结束，阈值后移
        //   跳过      → 本轮作废，满周期重新计时（契约 §3：跳过 = 满周期重新计时）
        //   立即休息  → 现在就开始这一轮休息
        //
        // 预告期的三条硬约束：
        //   · 不装键盘钩子（KeyboardGuard 在这里完全不参与）
        //   · 不抢焦点（WS_EX_NOACTIVATE）
        //   · 只有鼠标出口，没有键盘快捷键
        // 这三条保证"预告"对用户输入零影响 —— 用户要求的"不要再出现 Ctrl 卡住"
        // 在预告期是结构性保证，不依赖任何运行时判断。
        // ================================================================

        private void StartPreview(bool bodyBreak)
        {
            if (isPreviewing) return;

            try
            {
                if (previewForm == null)
                {
                    previewForm = new PreviewForm();
                    previewForm.RequestPostpone += delegate
                    {
                        // 延后 = 与休息遮罩上的推迟同义：阈值后移，累加器不清零
                        if (!isPreviewing) return;
                        EndPreview();
                        PostponeWork();
                    };
                    previewForm.RequestSkip += delegate
                    {
                        // 跳过 = 本轮作废，满周期重新计时
                        if (!isPreviewing) return;
                        EndPreview();
                        cyclesWithoutRest++;      // A5：又一轮没真正休息，下次仪式音加一声
                        CountStatSkip();         // F1：跳过 +1
                        ResetWork();
                        UpdateTrayText();
                    };
                    previewForm.RequestNow += delegate
                    {
                        // 立即休息 = 现在就开始
                        if (!isPreviewing) return;
                        bool body = previewIsBody;
                        EndPreview();
                        BeginRest(body);
                    };
                }

                isPreviewing = true;
                previewIsBody = bodyBreak;

                // A2：进预告就把渐暗层撤掉。横幅本身就是"要休息了"的强信号，
                // 再叠一层变暗既看不清横幅，也像是出了故障。
                HideDim();

                // A1：边缘辉光同样撤掉（预告已经接管了"该休息了"这件事）
                HideEdgeGlow();

                // 先把倒计时算出来再 SetCountdown：StartPreview 里不能拿上一轮的
                // previewRemaining 显示，否则横幅第一帧会闪一个旧值。
                long leftMs = bodyBreak ? (sedLimitMs - sedAccumMs) : (workLimitMs - workAccumMs);
                if (leftMs < 0) leftMs = 0;
                previewRemaining = (int)((leftMs + 999) / 1000);

                // 横幅落在鼠标所在的那块屏幕（用户正在看的那块）
                Screen target = null;
                try
                {
                    Point cur = Cursor.Position;
                    target = Screen.FromPoint(cur);
                }
                catch { }
                if (target == null)
                {
                    try { target = Screen.PrimaryScreen; } catch { }
                }
                if (target != null) previewForm.MoveToScreen(target);

                previewForm.SetPostponeText("延后 " + cfg.PostponeMinutes + " 分");
                string bodyText = bodyBreak
                    ? "该起身活动一下了"
                    : (cfg.PomodoroEnabled ? "这一轮番茄钟快结束了" : "快到休息时间了");
                previewForm.SetCountdown(previewRemaining, bodyText);
                previewForm.ShowPreview();

                // ---- A5 仪式音（插件 Sound）----
                // 在"预告刚出现"这一刻响，而不是等遮罩盖上来才响：
                // 那 30 秒是唯一还来得及让人主动停手的窗口。
                // 未启用时 PluginHost.Get 返回 null —— 不创建对象、不发声，零影响。
                RitualSoundPlugin ritual = PluginHost.Get("Sound") as RitualSoundPlugin;
                if (ritual != null) ritual.PlayRitual(RitualRepeatCount());
            }
            catch
            {
                // 预告横幅出问题绝不能影响主流程：直接放弃预告，照常到点弹遮罩
                isPreviewing = false;
            }
        }

        private void EndPreview()
        {
            if (!isPreviewing) return;
            isPreviewing = false;
            previewRemaining = 0;
            try { if (previewForm != null) previewForm.HidePreview(); } catch { }
        }

        /// <summary>每秒刷新横幅上的倒计时（值没变不重绘）。</summary>
        private void UpdatePreviewCountdown()
        {
            if (!isPreviewing) return;

            long left;
            string body;
            if (previewIsBody)
            {
                left = sedLimitMs - sedAccumMs;
                body = "该起身活动一下了";
            }
            else
            {
                left = workLimitMs - workAccumMs;
                body = cfg.PomodoroEnabled ? "这一轮番茄钟快结束了" : "快到休息时间了";
            }
            if (left < 0) left = 0;

            int sec = (int)((left + 999) / 1000);
            if (sec != previewRemaining)
            {
                previewRemaining = sec;
                try
                {
                    if (previewForm != null)
                        previewForm.SetCountdown(sec, body);
                }
                catch { }
            }
        }

        /// <summary>
        /// 推迟当前这一轮：阈值后移，累加器不清零（契约 §3「推迟 = 本轮不结束，只改到期时刻」）。
        /// 休息遮罩上的「推迟」按钮与预告横幅上的「延后」按钮共用这一段逻辑。
        /// </summary>
        private void PostponeWork()
        {
            long now = Clock.NowMs();
            long dt = Clock.DeltaMs(now, lastAnchorMs);
            lastAnchorMs = now;

            workLimitMs += (long)cfg.PostponeMinutes * 60L * 1000L;
            if (cfg.SedentaryEnabled && sedLimitMs > 0)
            {
                sedLimitMs = Math.Max(sedLimitMs, (long)cfg.SedentaryMinutes * 60L * 1000L);
                sedLimitMs += (long)cfg.PostponeMinutes * 60L * 1000L;
            }
            ResetDimRound();     // 延后 = 本轮重新开始计时，渐暗可以再出现一次
            ResetDefer();        // C3/C4：额度重新给（延后本身就是"再给我 5 分钟"）
            cyclesWithoutRest++; // A5：延后也算"这一轮没真正休息"，下次仪式音加一声
            RefreshRemaining();
            UpdateTrayText();
        }

        /// <summary>
        /// 开始一次休息。
        /// bodyBreak=true 表示这是久坐触发的「活动一下」，时长取 SedentarySeconds；
        /// false 表示护眼/番茄钟休息，时长取 RestTotalSeconds。
        /// 两条路走的是**同一个**状态机与遮罩，只是时长、文案与配色不同。
        /// </summary>
        private void BeginRest(bool bodyBreak)
        {
            // 预告横幅若还挂着必须先收起：两套 UI 同时可见会互相遮挡。
            // （正常路径上预告到点已经在 OnTick 里 EndPreview 过，这里是兜底。）
            EndPreview();

            // A2：渐暗层必须立刻撤掉 —— 它和遮罩叠在一起会让遮罩显得"发灰"，
            // 而且遮罩本来就是主角，渐暗只是到点前的铺垫。
            HideDim();

            // A1：边缘辉光同理，遮罩期间不能再有我们自己的光
            HideEdgeGlow();

            // C3/C4：休息真的开始了，让路状态归零（下一轮重新给足额度，
            // 否则"上轮被让了 9 分钟"会吃掉下一轮的预算）
            ResetDefer();

            // F1：这一轮已经弹出来了，"到点却被拖着"的计时结束（下一轮重新算）
            ignoreSinceMs = 0;
            ignoreCounted = false;

            // 记下当时的前台窗口，休息结束时若前台被我们占了就还回去。
            // 遮罩是 WS_EX_NOACTIVATE，理论上不会抢，这是点击按钮等情况下的保险。
            try { savedForeground = GetForegroundWindow(); }
            catch { savedForeground = IntPtr.Zero; }

            isResting = true;
            // 手动"立即休息"是用户主动行为，必须覆盖任何抑制状态。
            // 抑制状态在这里清掉，休息期间不再评估 gate（契约 §9）。
            isSuppressed = false;
            suppressedSinceMs = 0;
            suppressForced = false;   // 强制放行标记随抑制态一起归零，不留给下一轮
            restTotalSeconds = bodyBreak ? cfg.SedentarySeconds : RestTotalSeconds;
            restDueMs = Clock.NowMs() + (long)restTotalSeconds * 1000L;
            restRemaining = restTotalSeconds;
            lastShown = -1;

            EnsureRestForms();

            // 文案三选一：活动休息 > 番茄钟休息 > 普通护眼。
            // 活动休息**不要求坐满时长、也不判断是否在工位** —— 坐着舒展同样算数。
            string eyebrow;
            string hint;
            if (bodyBreak) { eyebrow = "活动一下"; hint = "起身走两步，或者坐着舒展一下都行"; }
            else if (cfg.PomodoroEnabled) { eyebrow = "番茄钟休息"; hint = "这一轮专注结束了，起身活动一下"; }
            else { eyebrow = "休息一下"; hint = "让眼睛看看 6 米外的东西，放松睫状肌"; }

            bool pomo = bodyBreak ? false : cfg.PomodoroEnabled;   // 身体休息不用番茄钟配色

            for (int i = 0; i < restForms.Count; i++)
            {
                RestForm f = restForms[i];
                f.SetPomodoro(pomo);          // 必须在 SetHint / ShowRest 之前（换配色与副提示）
                f.SetHint(eyebrow, hint);
                f.ShowRest();
            }

            tray.Icon = iconRest;
            if (cfg.SoundEnabled) Sfx.PlayStart();

            // B1/B2 休息仪式（插件 Ritual）：遮罩已经摆好之后再弹小窗，
            // 否则小窗会被随后 Show 出来的全屏遮罩盖住。
            // 未启用时 Get 返回 null → 不建窗口。
            //
            // **只在久坐/活动休息（bodyBreak）时弹**：
            //   普通护眼休息的目的是"让眼睛离开屏幕"，在屏幕正中弹一个会动的窗口
            //   等于把视线钉回屏幕上 —— 目的相反，不是文案不一致。
            //   将来若要"护眼休息也能做眼操"，应该是用户在遮罩上主动点一下，而不是自动弹。
            if (bodyBreak)
            {
                try { ShowRitualForRest(); } catch { }
            }

            // 跳过键由全局钩子兜底（遮罩不抢焦点，KeyDown 永远收不到键）。
            // AllowSkip=false 时不挂回调 —— 否则"禁止跳过"会被这两个键绕过。
            if (cfg.AllowSkip)
            {
                // F1：键盘 Esc / 空格跳过与点「跳过」按钮是同一个动作，必须一起计数。
                // 计数放在 isResting 判断之后：钩子可能重复投递（key repeat），
                // 只让第一次算数（与 EndRest 自己的重入守卫同一口径）。
                KeyboardGuard.OnSkipKey = delegate
                {
                    if (!isResting) return;
                    CountStatSkip();
                    EndRest(true);
                };
            }
            else KeyboardGuard.OnSkipKey = null;

            // 钩子有三个维度：跳过键（Esc/空格）、屏蔽系统键、全吞键盘。
            //
            //   AllowSkip | StrictMode | 结果
            //   ----------+------------+----------------------------------------------
            //   true      | true       | 全吞键盘，Esc/空格可跳过
            //   true      | false      | 只吞 Esc/空格（用于跳过），其余键照常输入  ← P0-E 默认档
            //   false     | true       | 只吞 Win/Tab/Alt+F4/Ctrl+Esc，其余照常
            //   false     | false      | 不装钩子，零开销
            //
            // blockAll 只在"有跳过出口"（AllowSkip=true）时才开：
            // AllowSkip=false 时若也全吞键盘，用户既不能跳过也不能打字，
            // 只剩 Ctrl+Alt+Del 一条路，太危险。
            //
            // P0-E：StrictMode 默认改为 false，所以默认组合是
            // 「AllowSkip=true + StrictMode=false」→ blockAll=false，
            // 钩子只吞 Esc/空格作为跳过出口，**其余按键一律照常送达用户程序**。
            // 这是"不影响用户输入"这条硬要求的落点：默认档下不存在任何全吞路径。
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

            // C3/C4：遮罩一关，让路状态就必须归零。
            // 跳过（resetWork=true）本来会经 ResetWork 清掉，但推迟（resetWork=false）不会，
            // 所以在这里统一清一次 —— 上一轮残留的 deferUntilMs 会把下一轮也推迟。
            ResetDefer();

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

            // B1/B2 休息仪式：休息一结束就关窗并释放（它是一次性的，不复用）
            try { HideRitual(); } catch { }

            for (int i = 0; i < restForms.Count; i++)
            {
                try { restForms[i].HideRest(); } catch { }
            }

            try { tray.Icon = iconIdle; } catch { }
            // A3：收遮罩后立刻把图标切回进度环。只塞 iconIdle 的话，托盘会一直停在
            // 静态眼睛上，直到"剩余整分钟数"下次变化才被重画覆盖（最多能停 1 分钟）。
            lastIconMinute = -1;
            try { RefreshRemaining(); } catch { }
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
            BeginRest(false);          // 手动「立即休息」= 护眼/番茄钟休息，不是活动休息
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
                RestForm f = new RestForm(screens[i], cfg.AllowSkip, cfg.PostponeMinutes, withButtons,
                                          cfg.SittingOptionEnabled);
                f.RequestSkip += delegate
                {
                    if (!isResting) return;   // 防重复计数（连点两下按钮）
                    cyclesWithoutRest++;      // A5：遮罩上点跳过也算"这一轮没真休息"
                    CountStatSkip();          // F1：跳过 +1
                    EndRest(true);
                };
                f.RequestPostpone += delegate
                {
                    EndRest(false);
                    // 推迟 = 本轮**不结束**，只改到期时刻（契约 §3），计数不清零。
                    // 与预告横幅上的「延后」共用同一段逻辑，避免两处语义分叉。
                    PostponeWork();
                };
                // G3「坐一分钟」：本轮**立即结束**（等价于跳过的那套收尾：重置计时 + 卸载钩子），
                // 但**计入完成数** —— 用户确实坐了，只是没坐满，这不是"没休息"。
                f.RequestSitting += delegate
                {
                    if (!isResting) return;   // 防重复计数
                    CompletedRests++;
                    CountStatDone();          // F1：坐一分钟也计为完成
                    EndRest(true);
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
                try { restForms[i].UpdateView(restRemaining, restTotalSeconds); }
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
                else if (isPreviewing) s = "即将休息 " + previewRemaining + " 秒";
                else if (isSuppressed && !suppressForced)
                {
                    // 抑制期间必须说清楚"为什么没提醒"，否则用户以为程序坏了。
                    // 文案区分全屏与演示（NotifyIcon.Text 上限 63 字符，务必短）。
                    // suppressForced 为真时抑制已经放行（只是 QUNS 还没回到非抑制值），
                    // 此时再说"暂缓"就是撒谎 —— 让它落到下面的正常剩余时间文案。
                    string reason = NotificationState.ReasonText(currentQuns);
                    s = reason.Length > 0 ? reason : "提醒暂缓中";
                }
                else if (IsIdleTooLong()) s = "检测到离开，计时已暂停";
                else
                {
                    // 番茄钟标记放在最前面（一眼就能看出当前用的是哪套节奏），
                    // 久坐剩余挂在后面（活动 " + mm:ss），格式与护眼剩余一致。
                    s = (cfg.PomodoroEnabled ? "番茄钟 · " : "")
                        + "工作剩余 " + (workRemaining / 60) + ":" + (workRemaining % 60).ToString("00")
                        + (cfg.SedentaryEnabled
                            ? " · 活动 " + (sedRemaining / 60) + ":" + (sedRemaining % 60).ToString("00")
                            : "");
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
            // 暂停/恢复时必须重置锚点，否则恢复后的第一个 tick 会把**暂停期间的时长**
            // 一次性加进累加器（dt = now - lastAnchorMs，暂停了 10 分钟就多算 10 分钟）。
            // 累加器形态下"不累加"只挡住了本次累加，没挡住 dt 本身在积累。
            lastAnchorMs = Clock.NowMs();
            RefreshRemaining();
            UpdateTrayText();
        }

        // ---------- 空闲检测 ----------
        //
        // 返回"已空闲的秒数"（不是布尔），因为 D2 需要区分两种空闲：
        //   · 空闲 < IdleMinutes     → 只是暂停计时（人短暂离开）
        //   · 空闲 > 整个休息时长   → 当作**已经休息过了**，直接重置周期
        // 第二个需求必须有实际秒数才能判断，只返回布尔就做不到。
        private int IdleSeconds()
        {
            return (int)(RawIdleMs() / 1000);
        }

        /// <summary>
        /// 距上次键鼠输入的毫秒数。
        ///
        /// 测试钩子：环境变量 EYE_LITE_FAKE_IDLE_MS 存在且能解析成非负整数时**直接返回该值**，
        /// 让自动化测试可以确定性地模拟"用户已离开 N 毫秒"，不必真的去动键鼠。
        /// 未设置时走 GetLastInputInfo（失败返回 0 = 当成"人就在" —— fail-open，
        /// 宁可多提醒一次，也不能因为探测失败让计时永远停着）。
        ///
        /// A2 渐暗判断"有没有新输入"也走这里，口径与空闲计时完全一致。
        /// </summary>
        private static long RawIdleMs()
        {
            // 文件钩子优先：它能在进程运行中改变，用来验证"离开后又回来"这类时序。
            long fromFile = ReadFakeIdleFromFile();
            if (fromFile >= 0) return fromFile;

            if (FakeIdleMs >= 0) return FakeIdleMs;

            try
            {
                LASTINPUTINFO lii = new LASTINPUTINFO();
                lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref lii)) return 0;
                uint idleMs = unchecked((uint)Environment.TickCount) - lii.dwTime;
                return (long)idleMs;
            }
            catch { return 0; }
        }

        /// <summary>空闲是否已超过阈值（cfg.IdleMinutes &lt;= 0 时永不为真，等于关掉该检测）。</summary>
        private bool IsIdleTooLong()
        {
            if (cfg.IdleMinutes <= 0) return false;
            return IdleSeconds() >= cfg.IdleMinutes * 60;
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

        /// <summary>
        /// A3：托盘图标进度环。底盘是暗灰整圈，进度从 12 点方向顺时针扫过
        /// progress * 360 度，颜色由调用方按剩余比例给（蓝 / 黄 / 橙）。
        ///
        /// 中心**保持透明**：任务栏底色有深有浅，填了色块反而更糊；
        /// 环本身在小尺寸下辨识度已经够了。
        ///
        /// GDI 句柄纪律：Icon.FromHandle 不拥有句柄，每次重绘前必须 DestroyIcon 上一帧，
        /// 否则句柄会随分钟数一路涨上去（任务栏图标不像普通控件，泄漏是永久的）。
        /// </summary>
        private static Icon MakeProgressIcon(float progress, Color fill)
        {
            if (progress < 0f) progress = 0f;
            if (progress > 1f) progress = 1f;

            Bitmap bmp = new Bitmap(32, 32);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    // 3px 笔宽画在 28x28 的内接矩形上：描边实际占 0.5~31.5px，正好不切边
                    Rectangle ringRect = new Rectangle(2, 2, 28, 28);
                    using (Pen track = new Pen(Color.FromArgb(70, 70, 70), 3f))
                    {
                        g.DrawArc(track, ringRect, 0f, 360f);
                    }
                    if (progress > 0.002f)      // 剩余比例极小时不画，避免 DrawArc 出毛刺
                    {
                        using (Pen p = new Pen(fill, 3f))
                        {
                            g.DrawArc(p, ringRect, -90f, progress * 360f);
                        }
                    }
                }

                // 用 Clone 拿一个"自己拥有句柄"的 Icon 再返回。
                //
                // 为什么不能直接 `return Icon.FromHandle(h)`：
                //   FromHandle 返回的 Icon **不拥有** 那个 HICON，Dispose 也不会释放它；
                //   而 NotifyIcon 每次赋值都会向 shell 重新注册图标。
                //   原写法靠 lastIconHandle 手动 DestroyIcon 记账，实测 180s 泄漏
                //   +22 个 GDI 对象（38 → 60）。改成"Clone 一份 + 立刻销毁临时句柄"，
                //   句柄所有权交给返回的 Icon 自己，调用方 Dispose 即可回收。
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(h);
                }
            }
            finally
            {
                // GetHicon 返回的是位图的一份独立副本，拿完句柄就能放掉位图：
                // 不这么做，每分钟一次重绘就是一分钟一张 32x32 位图的泄漏。
                bmp.Dispose();
            }
        }

        /// <summary>
        /// 选出"最紧迫的那条时钟"的剩余毫秒与总时长。
        /// 护眼与久坐是两套独立时钟，同时在跑时按**剩余比例**取更小的那条 ——
        /// 按绝对毫秒比会把 45 分钟的久坐永远判成"更远"，等于只看护眼。
        /// 没有可用的时钟时返回 false。
        /// </summary>
        private bool PickMostUrgentClock(out long leftMs, out long totalMs)
        {
            leftMs = 0;
            totalMs = 0;

            bool hasWork = workLimitMs > 0;
            bool hasSed = cfg.SedentaryEnabled && sedLimitMs > 0;
            if (!hasWork && !hasSed) return false;

            long workLeft = hasWork ? workLimitMs - workAccumMs : 0;
            long sedLeft = hasSed ? sedLimitMs - sedAccumMs : 0;

            bool useSed;
            if (hasWork && hasSed)
            {
                double wr = (double)workLeft / (double)workLimitMs;
                double sr = (double)sedLeft / (double)sedLimitMs;
                useSed = sr < wr;
            }
            else
            {
                useSed = hasSed;
            }

            if (useSed) { leftMs = sedLeft; totalMs = sedLimitMs; }
            else { leftMs = workLeft; totalMs = workLimitMs; }
            return totalMs > 0;
        }

        /// <summary>
        /// A3：按剩余比例换色重绘托盘图标。
        ///
        /// 重绘时机被"剩余整分钟数"卡住：只有 m 变化才画。
        /// 每秒重绘会让 GDI 句柄与 shell 通知都变成每秒一次，对常驻程序是纯浪费 ——
        /// 进度环的粒度本来就是分钟级，1 分钟一格肉眼看不出跳。
        /// </summary>
        private void UpdateTrayProgressIcon()
        {
            // 休息期间保持 iconRest（见 BeginRest），不画进度环
            if (isResting) return;

            long left, total;
            if (!PickMostUrgentClock(out left, out total)) return;
            if (left < 0) left = 0;
            if (left > total) left = total;

            int m = (int)(left / 60000L);
            if (m == lastIconMinute) return;
            lastIconMinute = m;

            float progress = (float)((double)left / (double)total);

            Color fill;
            if (progress > 0.5f) fill = Color.FromArgb(64, 132, 220);            // 蓝：还宽裕
            else if (progress > 0.2f) fill = Color.FromArgb(224, 178, 60);       // 黄：过半了
            else fill = Color.FromArgb(232, 122, 48);                            // 橙：快到了

            Icon prev = lastProgressIcon;
            try
            {
                Icon ic = MakeProgressIcon(progress, fill);
                lastProgressIcon = ic;
                tray.Icon = ic;      // NotifyIcon 会给 shell 一份副本，之后释放本对象是安全的
            }
            catch { }
            finally
            {
                // 旧帧必须在这里释放：Clone 出来的 Icon 拥有自己的 HICON，
                // 不 Dispose 就是每分钟一个、永不回收的 GDI 泄漏。
                if (prev != null)
                {
                    try { prev.Dispose(); } catch { }
                }
            }
        }

        // ---------- A2：渐进式降饱和 ----------
        //
        // 语义：离休息不到 60 秒开始，把屏幕整体（每块屏一层）从 0 线性压到
        // cfg.DimAlphaMax/100，500ms 一档；任何键鼠输入立刻撤销并放弃本轮。
        //
        // 为什么"输入就放弃本轮"而不是"输入就延后"：
        //   人正在打字时屏幕突然变暗是干扰；而一旦被打断就重新计时的做法，
        //   会在"边打字边等下一条消息"的人身上反复闪，比不暗更烦。
        //   所以：一次放弃 = 本轮安静到底，下一轮重新开始。

        /// <summary>
        /// 每 tick 调一次。所有"不该暗"的情况都收敛到 HideDim()，只有同时满足
        /// 剩余 0~60 秒 / 非休息 / 非预告 / 非抑制 / 非暂停 / 未放弃 / 强度 > 0 时才真的显示。
        /// </summary>
        private void DimTick()
        {
            if (cfg.DimAlphaMax <= 0 || dimGaveUp)
            {
                HideDim();
                return;
            }

            if (isResting || isPreviewing || isSuppressed || paused)
            {
                HideDim();
                dimLastStep = -1;
                return;
            }

            long left, total;
            if (!PickMostUrgentClock(out left, out total) || left <= 0 || left > 60000)
            {
                // 还没进 60 秒窗口（或已经到点）：撤掉并把档位清掉，
                // 下一轮进窗口时从最浅的一档重新淡入。
                HideDim();
                dimLastStep = -1;
                return;
            }

            // 人走开了就别再压暗 —— 屏幕上没人看，留着暗层只会在回来时吓一跳。
            if (IsIdleTooLong())
            {
                HideDim();
                dimLastStep = -1;
                return;
            }

            // ---- 键鼠输入 -> 撤销本轮 ----
            // idle 毫秒在无人操作时单调递增，一旦比上次采样小，就说明这段时间里有输入。
            // 用"变小"而不是"小于阈值"：阈值会把"一直在动"判成没动，也得照顾 tick 迟到。
            long idleMs = RawIdleMs();
            if (dimLastIdleMs >= 0 && idleMs < dimLastIdleMs)
            {
                HideDim();
                dimGaveUp = true;
                dimLastStep = -1;
                return;
            }
            dimLastIdleMs = idleMs;

            // 两次 Opacity 写入之间的最小间隔。
            //
            // 为什么从 500ms 放宽到 1000ms、并且再加一道"变化小于 1% 就不写"：
            //   Opacity 每写一次，DWM 就要把**整块屏幕**重新合成一次。
            //   双屏 2560x1440 下每秒 2 次写入，实测把 tick 拖到过 63.53s（期望 60s，超出 ±3s 容差）。
            //   这不是"抖动"，是 UI 线程被合成阻塞。
            //   放宽后 60 秒窗口内最多约 45 次写入，视觉上仍是连续变暗。
            int step = (int)((60000L - left) / 1000L);
            if (step == dimLastStep) return;
            dimLastStep = step;

            double alpha = (60000.0 - (double)left) / 60000.0 * ((double)cfg.DimAlphaMax / 100.0);
            if (alpha < 0.0) alpha = 0.0;
            if (dimLastAlpha >= 0.0 && Math.Abs(alpha - dimLastAlpha) < 0.01) return;
            dimLastAlpha = alpha;

            EnsureDimForms();
            if (dimForms == null) return;

            for (int i = 0; i < dimForms.Count; i++)
            {
                DimForm f = dimForms[i];
                if (f == null || f.IsDisposed) continue;
                try
                {
                    // Opacity 必须在句柄存在之后才生效，所以先 Show 再设值。
                    // 构造期已经把 Opacity 压到 0，第一帧不会闪全黑。
                    if (!f.Visible) f.Show();
                    f.SetAlpha(alpha);
                }
                catch { }
            }
        }

        /// <summary>撤掉渐暗层（重复调用安全，没有窗口时什么都不做）。</summary>
        private void HideDim()
        {
            dimLastIdleMs = -1;
            // 强度记账一并归零：否则"撤掉后重新进窗口"时会因为 alpha 没变而不再写入，
            // 表现为压暗层再也不出现。所有"不该暗"的分支都会走到这里，所以放这一处即可。
            dimLastAlpha = -1.0;
            if (dimForms == null) return;

            for (int i = 0; i < dimForms.Count; i++)
            {
                DimForm f = dimForms[i];
                if (f == null || f.IsDisposed) continue;
                try
                {
                    f.SetAlpha(0.0);
                    if (f.Visible) f.Hide();
                }
                catch { }
            }
        }

        /// <summary>
        /// 懒创建：DimAlphaMax == 0 时永远走不到这里，所以关闭功能时一个对象都不建。
        /// 每块屏幕一层，Bounds 就是该屏的物理范围（含任务栏区域 —— 只压工作区会留一条亮边）。
        /// </summary>
        private void EnsureDimForms()
        {
            if (dimForms != null) return;

            List<DimForm> list = new List<DimForm>();
            try
            {
                Screen[] screens = Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    list.Add(new DimForm(screens[i].Bounds));
                }
            }
            catch { }

            dimForms = list;
        }

        /// <summary>释放所有渐暗窗口（退出 / 重建时调用）。</summary>
        private void DisposeDimForms()
        {
            if (dimForms == null) return;

            for (int i = 0; i < dimForms.Count; i++)
            {
                DimForm f = dimForms[i];
                if (f == null) continue;
                try { f.SetAlpha(0.0); } catch { }
                try { f.Hide(); } catch { }
                try { f.Close(); } catch { }
                try { f.Dispose(); } catch { }
            }
            dimForms.Clear();
            dimForms = null;
            dimLastStep = -1;
            dimLastIdleMs = -1;
            dimLastAlpha = -1.0;
        }

        // ---------- 退出 ----------
        private void ExitApp()
        {
            try { timer.Stop(); } catch { }
            KeyboardGuard.Uninstall();
            KeyboardGuard.OnSkipKey = null;
            EndPreview();
            DisposeDimForms();          // A2：渐暗层必须先撤，否则会留在屏幕上
            try { if (previewForm != null) { previewForm.Close(); previewForm.Dispose(); previewForm = null; } } catch { }
            for (int i = 0; i < restForms.Count; i++)
            {
                try { restForms[i].Close(); restForms[i].Dispose(); } catch { }
            }
            restForms.Clear();
            // P1：已启用的插件必须走一遍 OnDisable（停 Timer / 释放 GDI），
            // 否则退出瞬间还在响的仪式音会跟着窗口一起"半死"。
            try { PluginHost.DisposeAll(); } catch { }
            DestroyLastIconHandle();    // A3：进度环的 HICON 是我们自己建的，必须自己销毁
            try { tray.Visible = false; tray.Dispose(); } catch { }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                KeyboardGuard.Uninstall();
                KeyboardGuard.OnSkipKey = null;
                EndPreview();
                DisposeDimForms();
                try { if (previewForm != null) { previewForm.Dispose(); previewForm = null; } } catch { }
                for (int i = 0; i < restForms.Count; i++)
                {
                    try { restForms[i].Dispose(); } catch { }
                }
                restForms.Clear();
                try { PluginHost.DisposeAll(); } catch { }
                DestroyLastIconHandle();
                if (timer != null) timer.Dispose();
                if (tray != null) tray.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// 销毁进度环当前这一帧的 Icon。
        /// 方法名沿用旧名（调用点无需改），实现改为释放那个**拥有句柄**的 Icon 对象。
        /// </summary>
        private void DestroyLastIconHandle()
        {
            try
            {
                if (lastProgressIcon != null)
                {
                    lastProgressIcon.Dispose();
                    lastProgressIcon = null;
                }
            }
            catch { }
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

        // A3：Icon.FromHandle 拿到的是"不归托管代码管"的 HICON，换图前必须自己销毁。
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

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
            // 必须是第一条语句：晚于任何 System.Drawing 初始化，GDI+ 会永久缓存误判的 DPI
            try { SetProcessDPIAware(); } catch { }

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
