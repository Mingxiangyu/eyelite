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

        // ---- 共享载体绘制（A1 边缘微光 / A2 压暗层共用一套"光"的观感）----
        //
        /// <summary>
        /// 画一条"从屏幕边缘向内淡出"的渐变带。
        ///
        /// 为什么是三档 blend 而不是两档（实色 → 全透明）：
        ///   两档线性渐变在**中段**就衰减掉一半亮度，视觉上会出现一条能看出来的硬边，
        ///   像"画了个框"而不像光。三档把亮度压在外侧、留一段长尾缓慢消失，
        ///   观感才是"屏幕边缘在发光"。
        ///
        /// 纪律：只做整条矩形填充，**不建位图、不缓存画刷**（契约 §10.5）。
        /// </summary>
        internal static void DrawFadeBand(Graphics g, Rectangle r, Color color,
                                          LinearGradientMode mode, bool reverse)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            try
            {
                // 采样 10 个点，按 (1-t)^2 衰减，**内缘精确为 0**。
                //
                // 为什么不是三档（满 / 34% / 0）：
                //   三档在外侧留了一段"平台"，视觉上就是"一条实心边 + 突然消失"，
                //   用户反馈"前两档没感觉"。真正的羽化要求亮度在整条带子上连续下降，
                //   并且在靠近屏幕内侧时彻底消失，不能有一条看得见的终止线。
                const int Stops = 10;
                Color[] cols = new Color[Stops];
                float[] pos = new float[Stops];
                for (int i = 0; i < Stops; i++)
                {
                    float t = i / (float)(Stops - 1);          // 0 = 外缘，1 = 内缘
                    double a = (1.0 - t) * (1.0 - t);          // 平方衰减，末端自然收敛
                    int alpha = (int)(color.A * a + 0.5);
                    cols[i] = Color.FromArgb(alpha, color);
                    pos[i] = t;
                }
                if (reverse) Array.Reverse(cols);              // 只翻颜色，位置仍严格 0→1

                using (LinearGradientBrush br = new LinearGradientBrush(r, cols[0], cols[Stops - 1], mode))
                {
                    ColorBlend blend = new ColorBlend(Stops);
                    blend.Colors = cols;
                    blend.Positions = pos;
                    br.InterpolationColors = blend;
                    g.FillRectangle(br, r);
                }
            }
            catch { }
        }

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
        public const string AppVersion = "v1.6.1";

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
        // 不设 readonly：输入框容器要靠换边框色表达"当前被聚焦"，
        // 而输入框本身是 BorderStyle.None，没有别的地方能承载焦点态。
        private Pen borderPen;
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

        /// <summary>
        /// 运行时换边框色（输入框聚焦高亮）。语义与 RoundButton.SetBorderColor 一致：
        /// 颜色没变就直接返回，避免每次 Enter/Leave 都新建一个 Pen。
        /// </summary>
        public void SetBorderColor(Color c)
        {
            if (borderPen == null || borderPen.Color == c) return;
            Pen old = borderPen;
            borderPen = new Pen(c, old.Width);
            old.Dispose();
            this.Invalidate();
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
            // 禁用态不该有 hover 反馈：否则鼠标划过仍然变色，看起来像可用、点了却没反应
            if (!this.Enabled) return;
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
            if (!this.Enabled) return;
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

            // 禁用态必须**比可用态更淡**，不能更浓。
            // 早先用 ControlPaint.Dark(fore, 0.45f) 只对"深底浅字"成立（压暗=靠向背景）；
            // 设置页是"浅底深字"，Dark 反而拉高对比度 —— 实测禁用态文字 (28,32,38)
            // 比可用态 (74,85,104) 还黑，等于把禁用画成了强调。
            // 改成"向背景色混合 70%"：深底往暗里褪、浅底往白里褪，同一个公式两头都对。
            Color fore = this.ForeColor;
            if (!this.Enabled)
            {
                Color bg = this.BackColor;
                fore = Color.FromArgb(
                    (fore.R * 30 + bg.R * 70) / 100,
                    (fore.G * 30 + bg.G * 70) / 100,
                    (fore.B * 30 + bg.B * 70) / 100);
            }

            TextRenderer.DrawText(g, this.Text, this.Font, this.ClientRectangle, fore,
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

    /// <summary>
    /// 会议检测（C2）：判断"现在是否真的在开会"。
    ///
    /// 判据是**进程白名单 + 该进程有可见窗口 + 窗口标题命中关键词**，三条同时成立。
    ///   · 为什么不能只看进程：装了 Zoom/Teams 的人常年开着客户端，只看进程就等于"永不提醒"，
    ///     那正是 §10.1 说的"坏掉的版本"。
    ///   · 为什么要看标题：客户端主窗口（聊天/日程）和会议窗口标题不同，前者不该抑制。
    ///
    /// fail-open：任何一步取不到（进程已退出、权限不足、API 失败）一律当作"不在会中"。
    /// 宁可多提醒一次，也不能悄悄失去全部提醒。
    /// </summary>
    internal static class MeetingDetector
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        // 取进程名**不用** System.Diagnostics.Process：实测只要碰它，
        // 进程私有内存从 17.8MB 涨到 25.0MB（+7.2MB，GDI 不变）。
        // 为一个"只在到点前才需要"的判断付 7MB 不划算，改用纯 P/Invoke。
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags,
                                                             StringBuilder exeName, ref uint size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        // 测试设施：EYE_LITE_FAKE_MEETING=1 恒真、=0 恒假；未设 = -1（走真实检测）。
        // 不设时只是一次静态字段读取，行为与没有这个钩子完全一致。
        private static readonly int fake = ReadFake();

        private static int ReadFake()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("EYE_LITE_FAKE_MEETING");
                if (v == "1") return 1;
                if (v == "0") return 0;
            }
            catch { }
            return -1;
        }

        public static bool IsInMeeting(AppConfig cfg)
        {
            if (cfg == null || !cfg.MeetingGuardEnabled) return false;
            if (fake >= 0) return fake == 1;

            string[] procs = SplitList(cfg.MeetingProcesses);
            string[] keys = SplitList(cfg.MeetingTitleKeywords);
            if (procs.Length == 0) return false;

            bool hit = false;
            try
            {
                EnumWindowsProc cb = delegate (IntPtr h, IntPtr l)
                {
                    if (hit) return false;                 // 找到一个就收工
                    try
                    {
                        if (!IsWindowVisible(h)) return true;
                        if (GetWindowTextLength(h) <= 0) return true;

                        uint pid;
                        GetWindowThreadProcessId(h, out pid);
                        if (pid == 0) return true;

                        string name = ProcessNameOf(pid);
                        if (name.Length == 0 || !InList(procs, name)) return true;

                        if (keys.Length > 0)
                        {
                            StringBuilder sb = new StringBuilder(512);
                            GetWindowText(h, sb, sb.Capacity);
                            if (!ContainsAny(sb.ToString(), keys)) return true;
                        }

                        hit = true;
                        return true;
                    }
                    catch { return true; }
                };
                EnumWindows(cb, IntPtr.Zero);
            }
            catch { return false; }
            return hit;
        }

        private static string ProcessNameOf(uint pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return "";

                StringBuilder sb = new StringBuilder(600);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";

                string full = sb.ToString();
                // (char)92 = 反斜杠。直接写字符字面量在多层转义里容易被打穿，用码点更稳
                int cut = full.LastIndexOfAny(new char[] { (char)92, (char)47 });
                if (cut >= 0) full = full.Substring(cut + 1);
                return full;
            }
            catch { return ""; }
            finally
            {
                if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { } }
            }
        }

        private static string[] SplitList(string s)
        {
            if (s == null || s.Length == 0) return new string[0];
            string[] raw = s.Split(';');
            List<string> keep = new List<string>();
            for (int i = 0; i < raw.Length; i++)
            {
                string one = raw[i];
                if (one == null) continue;
                one = one.Trim();
                if (one.Length > 0) keep.Add(one);
            }
            return keep.ToArray();
        }

        private static bool InList(string[] list, string name)
        {
            for (int i = 0; i < list.Length; i++)
            {
                if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool ContainsAny(string text, string[] keys)
        {
            if (text == null || text.Length == 0) return false;
            for (int i = 0; i < keys.Length; i++)
            {
                if (text.IndexOf(keys[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
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

            // Movement 在 P1 批 2 后半落地（Factory 从 null 换成真实现）。
            list.Add(new PluginDescriptor
            {
                Id = "Movement",
                DisplayName = "起身活动演示",
                Description = "久坐/活动休息时弹火柴人动作演示，每个动作约 20~40 秒。",
                Factory = delegate { return new MovementPlugin(); }
            });

            // Guard 在 P1 最后一批落地（Factory 从 null 换成真实现）——
            // 到此 6 个插件的 Factory 全部非 null，Placeholder() 成为"给下一批用的模板"。
            list.Add(new PluginDescriptor
            {
                Id = "Guard",
                DisplayName = "跳过配额",
                Description = "限制每天能跳过几次休息，用完后「跳过」置灰（推迟/坐一分钟仍可用）。",
                Factory = delegate { return new GuardPlugin(); }
            });

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
        private const int BandDefault = 52;     // 96dpi 下的边带厚度（26 仍偏窄，再翻一倍）

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

            Ui.DrawFadeBand(g, r, Ui.Accent, mode, reverse);
        }
    }

    internal class EdgeGlowPlugin : IEyeLitePlugin
    {
        private const int TickMs = 100;             // 呼吸刷新间隔
        private const double PeriodMs = 3000.0;     // 呼吸一个来回 3 秒（慢，负责"有生命感"）
        private const double AlphaMin = 0.08;
        private const double AlphaMax = 0.34;
        // 闪烁：叠加一个 1Hz 的正弦（PeriodMs 的三分之一，即 1 秒一个来回），幅度小但看得见。
        // 为什么不用随机闪烁：100ms 刷新下随机会变成"跳变"，看着像接触不良；
        // 固定频率的叠加才是"呼吸 + 微闪"，而且可预测、不刺眼。
        private const double ShimmerDepth = 0.12;
        private const double ShimmerRatio = 3.0;

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
            // 呼吸（3 秒一周期）
            double k = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * phase);
            double a = AlphaMin + (AlphaMax - AlphaMin) * k;
            // 微闪（1 秒一周期）：只叠加、不替换，所以"慢呼吸"的骨架还在
            double shimmer = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * phase * ShimmerRatio);
            a += ShimmerDepth * shimmer;
            if (a > 0.62) a = 0.62;     // 上限：再高就不是"微光"而是"泛白"
            return a;
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
            AddToday(cfg);
            Persist(cfg);
        }

        // ---------- 最近 7 天（迷你图的数据层）----------
        //
        // 只存"每天完成了几次"。跳过/忽略不画图：一张图里塞三种状态谁都读不出来，
        // 而"完成了多少"才是用户唯一会拿来看趋势的量。
        private static void AddToday(AppConfig cfg)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            long todayCount = 0;
            List<string> keep = new List<string>();

            if (cfg.StatHistory != null && cfg.StatHistory.Length > 0)
            {
                string[] parts = cfg.StatHistory.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    string one = parts[i];
                    if (one == null) continue;
                    one = one.Trim();
                    int c = one.IndexOf(':');
                    if (c <= 0) continue;
                    string d = one.Substring(0, c);
                    long n = 0;
                    try { n = long.Parse(one.Substring(c + 1)); } catch { n = 0; }
                    if (d == today) { todayCount = n; continue; }   // 今天由下面重算
                    keep.Add(d + ":" + n.ToString());
                }
            }

            keep.Add(today + ":" + (todayCount + 1).ToString());
            keep.Sort(StringComparer.Ordinal);                      // ISO 日期天然可比
            while (keep.Count > 7) keep.RemoveAt(0);
            cfg.StatHistory = string.Join(";", keep.ToArray());
        }

        /// <summary>
        /// 最近 7 天的完成次数，转成 7 个字符的迷你柱（缺的日期按 0 算）。
        /// 用字符而不是画图：数据只有 7 个点，画一张图要新控件、要重算卡片版式，
        /// 而 sparkline 一行字就能看出趋势，改版面的风险却是实在的。
        /// </summary>
        public static string Sparkline(AppConfig cfg)
        {
            const string Levels = "·▁▂▃▄▅▆▇█";
            long[] counts = new long[7];
            if (cfg != null && cfg.StatHistory != null && cfg.StatHistory.Length > 0)
            {
                string[] parts = cfg.StatHistory.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    string one = parts[i];
                    if (one == null) continue;
                    one = one.Trim();
                    int c = one.IndexOf(':');
                    if (c <= 0) continue;
                    try
                    {
                        DateTime d = DateTime.ParseExact(one.Substring(0, c), "yyyy-MM-dd", null);
                        int slot = 6 - (int)(DateTime.Today - d.Date).TotalDays;
                        if (slot < 0 || slot > 6) continue;
                        counts[slot] = long.Parse(one.Substring(c + 1));
                    }
                    catch { }
                }
            }

            long max = 0;
            for (int i = 0; i < 7; i++) if (counts[i] > max) max = counts[i];

            char[] outChars = new char[7];
            for (int i = 0; i < 7; i++)
            {
                if (counts[i] <= 0 || max <= 0) { outChars[i] = Levels[0]; continue; }
                // 1..8 档：最小值固定占 1 档（"有值就看得见"），最大值占满 8 档
                int idx = 1 + (int)((counts[i] * 7L) / max);
                if (idx < 1) idx = 1;
                if (idx > 8) idx = 8;
                outChars[i] = Levels[idx];
            }
            return new string(outChars);
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
    // 3.14 G1 + G2 + G11 起身活动演示（插件 Movement）
    // ================================================================
    //
    // 三段结构，刻意拆开：
    //   MoveItem    —— 纯数据（动作名 / 时长 / 要点 / 禁忌关键词），G1 的动作库就是它的数组
    //   MovePose    —— 一帧姿势（10 个标量），G2 的关键帧插值就是在它上面做线性插值
    //   MoveForm    —— 演示窗（720×640、16ms 帧、火柴人矢量绘制、底部动作名/剩余/进度）
    //
    // 为什么火柴人用"标量姿势 + 前向运动学"而不是逐帧画死点位：
    //   逐帧画死 = 换一个动作就要重画一套坐标，且不同 DPI 下全要重算；
    //   存角度、运行时算点 = 关键帧只有 4~6 条/动作，任意尺寸下比例都对，插值也只是 10 个数做 lerp。
    //
    // G11 的禁忌过滤在**插件里**、不在窗口里：被排除的动作压根不进窗口的动作列表，
    // 所以窗口既不可能"画到一半发现该跳过"，也不需要在界面上解释为什么少了几个动作。

    /// <summary>G1 动作库的一条数据。Forbidden 是"命中就跳过本动作"的关键词。</summary>
    internal sealed class MoveItem
    {
        public string Name;         // 「深蹲 10 次」
        public int Seconds;         // 名义时长（会被剩余休息时间截断）
        public int CycleSeconds;    // 一次循环的秒数（深蹲 2 秒一次 × 10 次 = 20 秒）
        public string Tips;         // 一句话要点（画在窗口底部）
        public string[] Forbidden;  // 禁忌关键词（空数组 = 任何情况都不排除）
        public MovePose[] Frames;   // 一个循环内的关键帧（T 从 0 到 1）

        /// <summary>
        /// 开源素材的键（对应 assets/moves/&lt;key&gt;/ 与嵌入资源 EyeLite.res.&lt;key&gt;.N）。
        /// **null = 没有素材，走自绘火柴人**。素材只有 3 个动作有（深蹲 / 扩胸 / 原地踏步），
        /// 其余动作上游没有合适条目（见 assets/moves/manifest.json 的 note），继续用自绘。
        /// </summary>
        public string AssetKey;

        public MoveItem(string name, int seconds, int cycleSeconds, string tips,
                        string[] forbidden, MovePose[] frames, string assetKey)
        {
            this.Name = name;
            this.Seconds = seconds;
            this.CycleSeconds = cycleSeconds < 1 ? 1 : cycleSeconds;
            this.Tips = tips;
            this.Forbidden = forbidden == null ? new string[0] : forbidden;
            this.Frames = frames;
            this.AssetKey = assetKey;
        }
    }

    /// <summary>
    /// 一帧姿势。角度单位是度，方向约定：**0 = 竖直向下，正 = 顺时针（往屏幕右侧）**。
    /// 这样"手臂自然下垂"就是 0，"举过头顶"接近 ±180，胸腿的弯曲都能用正负表达。
    /// </summary>
    internal struct MovePose
    {
        public double T;            // 归一化时间 0~1（关键帧本身用，插值时不用）
        public double Torso;        // 躯干前倾（正 = 前倾）
        public double ArmL, ArmR;   // 上臂（相对躯干，正 = 抬向该侧/前）
        public double ForeL, ForeR; // 前臂（相对上臂，正 = 向体前弯）
        public double Crouch;       // 0~1 下蹲量
        public double StepL, StepR; // 0~1 抬腿量（原地踏步用）
        public double HeadSway;     // -1~1 头左右偏

        public MovePose(double t, double torso,
                        double armL, double armR, double foreL, double foreR,
                        double crouch, double stepL, double stepR, double headSway)
        {
            this.T = t;
            this.Torso = torso;
            this.ArmL = armL;
            this.ArmR = armR;
            this.ForeL = foreL;
            this.ForeR = foreR;
            this.Crouch = crouch;
            this.StepL = stepL;
            this.StepR = stepR;
            this.HeadSway = headSway;
        }
    }

    internal static class MoveLib
    {
        /// <summary>
        /// 9 个动作。每条的关键帧都只覆盖**一个循环**，窗口按 CycleSeconds 反复播，
        /// 所以「深蹲 10 次」是真的 10 次（30 秒 / 3 秒一次），不是画个样子。
        /// </summary>
        public static List<MoveItem> Build()
        {
            List<MoveItem> list = new List<MoveItem>();

            // ============================================================
            // 符号约定（改之前先读这段，否则会把手臂画反）
            // ============================================================
            // MoveForm.DrawArm 里是：a1 = torso + side * upAng，side 左=-1 / 右=+1。
            // 也就是说 **side 已经把左右翻转过一次**，所以：
            //   · 对称张开（两臂同时外展、上举、前平举）→ 两臂用**同号同值** (v, v)
            //   · 两臂同向摆动（踏步时双臂一起向左/向右甩）→ 用**反号** (-v, v)
            // 旧数据全是 (x, -x) —— 那是"两臂同向"，画出来两条手臂叠在身体同一侧，
            // 视觉上就只有一条手臂在微微动。这是"动作一直不变"的第二个原因（第一个是幅度太小）。
            // 新数据统一用 (v, v) 表示对称，只有「原地踏步」故意用反号表示摆臂。
            //
            // 幅度口径：0 = 自然下垂，±90 ≈ 平举，±150 ≈ 过头。
            // 前臂 fore 同号：负 = 向身体中线弯（肘夹紧），正 = 向外张。

            // 1) 起身伸展：双臂上举过头 + 后伸，末端左右侧屈。
            list.Add(new MoveItem("起身伸展", 30, 5, "双臂向上举过头再缓慢放下，脊柱伸展，别耸肩",
                new string[] { "腰椎", "脊柱" },
                new MovePose[]
                {
                    new MovePose(0.00, 0, 8, 8, 0, 0, 0, 0, 0, 0),
                    new MovePose(0.28, -8, 150, 150, -15, -15, 0, 0, 0, 0),     // 举过头
                    new MovePose(0.48, -8, 150, 150, -15, -15, 0, 0, 0, 0),     // 停一拍，看得清
                    new MovePose(0.70, -6, 140, 140, -12, -12, 0, 0, 0, -1.0),  // 向左侧屈
                    new MovePose(0.88, -6, 140, 140, -12, -12, 0, 0, 0, 1.0),   // 向右侧屈
                    new MovePose(1.00, 0, 8, 8, 0, 0, 0, 0, 0, 0)
                }, "stand-reach"));

            // 2) 深蹲 10 次：2 秒一次 × 10 次 = 20 秒（原来 30 秒/3 秒一次也是 10 次，
            //    但节奏偏慢、看不出"在做组"，所以连同周期一起压到 2 秒）。
            list.Add(new MoveItem("深蹲 10 次", 20, 2, "膝盖不过脚尖，臀部向后坐",
                new string[] { "腰椎", "膝" },
                new MovePose[]
                {
                    new MovePose(0.00, 0, 8, 8, 0, 0, 0.00, 0, 0, 0),
                    new MovePose(0.30, 22, 88, 88, 20, 20, 0.90, 0, 0, 0),      // 蹲到底 + 手臂前平举
                    new MovePose(0.50, 22, 88, 88, 20, 20, 0.90, 0, 0, 0),      // 停一拍（与上帧同值）
                    new MovePose(0.75, 8, 40, 40, 8, 8, 0.35, 0, 0, 0),         // 起身中
                    new MovePose(1.00, 0, 8, 8, 0, 0, 0.00, 0, 0, 0)
                }, "squat"));

            // 3) 肩颈绕环：双臂在 60 度附近扫动 + 头部大幅左右 + 躯干轻微侧向，
            //    这样"绕环"才看得出来，而不是只有一个头在动。
            list.Add(new MoveItem("肩颈绕环", 20, 4, "慢速绕环，肩膀放松别耸",
                new string[] { "颈椎", "肩袖", "肩" },
                new MovePose[]
                {
                    new MovePose(0.00, 0, 20, 20, 0, 0, 0, 0, 0, 0),
                    new MovePose(0.20, 3, 55, 55, -10, -10, 0, 0, 0, 1.0),      // 左侧
                    new MovePose(0.45, 0, 60, 60, 0, 0, 0, 0, 0, 0),
                    new MovePose(0.60, 0, 60, 60, 0, 0, 0, 0, 0, 0),            // 停一拍（最高点）
                    new MovePose(0.80, -3, 55, 55, -10, -10, 0, 0, 0, -1.0),    // 右侧
                    new MovePose(1.00, 0, 20, 20, 0, 0, 0, 0, 0, 0)
                }, "shoulder-circles"));

            // 4) 原地踏步：抬腿 0.85 交替，双臂**同向**摆动（反号 = 两臂一起甩）。
            list.Add(new MoveItem("原地踏步", 30, 1, "抬到大腿接近水平，落地轻",
                new string[] { "膝", "髋" },
                new MovePose[]
                {
                    new MovePose(0.00, 2, 14, 14, 0, 0, 0, 0.00, 0.00, 0),
                    new MovePose(0.25, 3, -30, 30, 0, 0, 0, 0.85, 0.00, 0.2),   // 抬左腿，双臂向左甩
                    new MovePose(0.50, 2, 14, 14, 0, 0, 0, 0.00, 0.00, 0),
                    new MovePose(0.75, 3, 30, -30, 0, 0, 0, 0.00, 0.85, -0.2),  // 抬右腿，双臂向右甩
                    new MovePose(1.00, 2, 14, 14, 0, 0, 0, 0.00, 0.00, 0)
                }, "march-in-place"));

            // 5) 靠墙站立：静态姿势，但双臂要有呼吸起落（18 ↔ 28），否则画面像卡死。
            //    贴墙站要靠腰椎维持在轻度伸展位，所以腰椎相关词命中时也会被排除。
            // 靠墙**静蹲**（原来是"靠墙站立"）：素材换成 wall-sit 的 f1/f2（屈膝 90 度静蹲）
            // + f3（站起），所以画面里真的有"蹲"这个动作，而不是一张站立的静图。
            // 静蹲是**承重**动作 —— 禁忌词必须带「膝」，否则膝关节有伤的人会被推荐去做深蹲。
            list.Add(new MoveItem("靠墙静蹲", 40, 6, "背贴墙，大腿蹲到接近水平，膝盖不超过脚尖",
                new string[] { "腰", "膝", "膝关节" },
                new MovePose[]
                {
                    new MovePose(0.00, -6, 18, 18, 0, 0, 0, 0, 0, 0),
                    new MovePose(0.45, -6, 28, 28, -6, -6, 0, 0, 0, 0),
                    new MovePose(0.60, -6, 28, 28, -6, -6, 0, 0, 0, 0),         // 吸气停一拍
                    new MovePose(1.00, -6, 18, 18, 0, 0, 0, 0, 0, 0)
                }, "wall-stand"));

            // 7) 颈部放松：头左右缓慢侧屈（不是转圈 —— 绕环对颈椎不稳的人不友好）。
            //    5 秒一个来回，两侧各停一拍：慢才安全，快就是甩脖子。
            // 扶墙小腿拉伸：替代原来的"颈部放松"。
            //    调查结论：workout-guide 302 条里没有颈部动作；everkinetic 293 条里只有
            //    0001/0002 两条**半身像**（与其他全身素材风格不统一、动作也读不出），已弃用。
            //    换成办公室高频的扶墙小腿拉伸：画面带墙、与「靠墙静蹲」同风格。
            list.Add(new MoveItem("扶墙小腿拉伸", 20, 5, "双手扶墙，一腿后撤、脚跟压地，感受小腿后侧",
                new string[] { "跟腱", "小腿" },
                new MovePose[]
                {
                    new MovePose(0.00, 0, 8, 8, 0, 0, 0, 0, 0, 0),
                    new MovePose(0.28, -2, 8, 8, 0, 0, 0, 0, 0, -1.0),         // 回退自绘用
                    new MovePose(0.42, -2, 8, 8, 0, 0, 0, 0, 0, -1.0),         // 停一拍
                    new MovePose(0.50, 0, 8, 8, 0, 0, 0, 0, 0, 0),
                    new MovePose(0.78, 2, 8, 8, 0, 0, 0, 0, 0, 1.0),           // 回退自绘用
                    new MovePose(0.92, 2, 8, 8, 0, 0, 0, 0, 0, 1.0),           // 停一拍
                    new MovePose(1.00, 0, 8, 8, 0, 0, 0, 0, 0, 0)
                }, "calf-stretch"));

            // 8) 肩胛后缩：坐姿就能做。前臂向内夹到 -70（肘向后夹紧），双臂微开 12 → 28。
            // 单臂横跨胸前（原来是"肩胛后缩"）：素材换成 cross-body-shoulder-stretch 三帧。
            // 目录名保留 scapular-retraction（改目录要同步 manifest 与代码，不值当）。
            list.Add(new MoveItem("单臂横跨胸前", 20, 4, "一只手臂横过胸前，另一只手轻压肘部，换边做",
                new string[] { "肩", "肩袖" },
                new MovePose[]
                {
                    new MovePose(0.00, 0, 12, 12, 0, 0, 0, 0, 0, 0),
                    new MovePose(0.40, -3, 28, 28, -70, -70, 0, 0, 0, 0),       // 夹到最紧
                    new MovePose(0.60, -3, 28, 28, -70, -70, 0, 0, 0, 0),       // 停一拍
                    new MovePose(1.00, 0, 12, 12, 0, 0, 0, 0, 0, 0)
                }, "scapular-retraction"));

            // 9) 侧弓步：替代原来的"躯干转体"。
            //    全量筛过 302 条：没有任何"站立/坐姿 + 无器械 + 读得出"的转体素材
            //    （russian-twist 画面里有药球、torso-twist-stretch 手持杠片、其余是哑铃），
            //    所以改成同属"躯干侧向活动"的侧弓步。
            list.Add(new MoveItem("侧弓步", 20, 4, "左右交替，重心移到一侧，另一条腿伸直",
                new string[] { "膝", "腰" },
                new MovePose[]
                {
                    new MovePose(0.00, 0, 30, 30, 80, 80, 0, 0, 0, 0),
                    new MovePose(0.25, 0, 30, 30, 80, 80, 0, 0, 0, -1.0),   // 偏左（回退自绘用）
                    new MovePose(0.50, 0, 30, 30, 80, 80, 0, 0, 0, 0),
                    new MovePose(0.75, 0, 30, 30, 80, 80, 0, 0, 0, 1.0),    // 偏右（回退自绘用）
                    new MovePose(1.00, 0, 30, 30, 80, 80, 0, 0, 0, 0)
                }, "lateral-lunge"));

            // 9) 扩胸：双臂平举后展到 85（不是 42 —— 42 度时手臂还贴着身体），
            //    前臂外张 20，躯干轻伸 -6，胸口往前送。肩部有问题的会被禁忌词挡掉。
            list.Add(new MoveItem("扩胸", 20, 5, "双臂向后打开，胸口往前送（扶着门框或墙边都行）",
                new string[] { "肩", "肩袖" },
                new MovePose[]
                {
                    new MovePose(0.00, 0, 10, 10, 0, 0, 0, 0, 0, 0),
                    new MovePose(0.35, -6, 85, 85, 20, 20, 0, 0, 0, 0),         // 平举后展
                    new MovePose(0.60, -6, 85, 85, 20, 20, 0, 0, 0, 0),         // 停一拍（与上帧同值）
                    new MovePose(1.00, 0, 10, 10, 0, 0, 0, 0, 0, 0)
                }, "chest-open"));

            return list;
        }

        /// <summary>
        /// G10：按痛点的缓解例程。
        ///
        /// 一条例程 = 有序的动作名列表 + 由各动作自身 Seconds 决定的总时长。
        /// 复用 MoveForm 的播放器：它的语义是"按预算循环播放给定动作"，
        /// 所以只要把预算设成各动作 Seconds 之和，播完最后一个就正好收窗 —— 一次"跟练"。
        ///
        /// 为什么按痛点组织而不是按动作组织：动作库回答的是"有哪些动作"，
        /// 而人的真实需求是"今天脖子难受，给我一套照着做完的"。
        /// </summary>
        public static readonly string[][] Routines = new string[][]
        {
            new string[] { "肩颈", "肩颈绕环", "单臂横跨胸前" },
            new string[] { "肩",   "单臂横跨胸前", "扩胸", "肩颈绕环" },
            new string[] { "腰",   "起身伸展", "深蹲 10 次", "侧弓步" },
        };

        /// <summary>按例程名取出动作（保持表里的顺序）。找不到的名字直接跳过，不抛。</summary>
        public static List<MoveItem> PickRoutine(string name)
        {
            List<MoveItem> all = Build();
            List<MoveItem> pick = new List<MoveItem>();
            string[] def = null;
            for (int i = 0; i < Routines.Length; i++)
            {
                if (Routines[i][0] == name) { def = Routines[i]; break; }
            }
            if (def == null) return pick;

            for (int d = 1; d < def.Length; d++)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].Name == def[d]) { pick.Add(all[i]); break; }
                }
            }
            return pick;
        }

        /// <summary>
        /// G11：这个动作是否被用户填的禁忌关键词排除。
        /// 匹配规则：分号分隔、逐项 Trim、大小写不敏感、**双向包含**
        ///（用户写「膝」能命中「膝关节」，写「膝关节」也能命中「膝」）。
        /// 空串 = 不排除任何动作。
        /// </summary>
        public static bool IsExcluded(MoveItem item, string contraindications)
        {
            if (item == null) return true;
            if (contraindications == null) return false;

            string[] users = contraindications.Split(';');
            for (int u = 0; u < users.Length; u++)
            {
                string user = users[u].Trim();
                if (user.Length == 0) continue;

                for (int f = 0; f < item.Forbidden.Length; f++)
                {
                    string key = item.Forbidden[f];
                    if (key == null) continue;
                    key = key.Trim();
                    if (key.Length == 0) continue;

                    if (Contains(user, key) || Contains(key, user)) return true;
                }
            }
            return false;
        }

        private static bool Contains(string big, string small)
        {
            return big.IndexOf(small, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>
    /// G2 演示窗：720×640，居中主屏，永不抢焦点。
    /// 一个动作做完自动切下一个；休息结束由插件调 Hide() 关闭并 Dispose。
    /// </summary>
    internal class MoveForm : Form
    {
        private const int TickMs = 16;      // 约 60fps

        private List<MoveItem> items;
        private int budgetMs;               // 本轮预算（= 剩余休息秒数，已被调用方截断）
        private int elapsedMs;
        // 墙钟起点：elapsedMs 以前是"每帧 += TickMs"，定时器被系统合并时会整体变慢
        // （实测 40 秒的例程跑了 63.7 秒）。改成从起点按真实时间算。
        private long animStartMs;
        private int index;                  // 当前第几个动作
        private int actionStartMs;          // 当前动作的起点（elapsed 时间轴）

        private System.Windows.Forms.Timer anim;
        
        // 「结束」按钮的矩形。构造期算一次（窗口尺寸固定），
        // OnPaint 用它画、MouseClick 用它命中 —— 两处必须用**同一个**矩形，否则会"看着在这、点不到"。
        private Rectangle closeRect;
        
        // 测试设施：EYE_LITE_TRACE_MOVE=<文件> 时记录演示窗内部状态，用于定位"画面不动"。
        // 不设时只有一次空判断。
        private static readonly string TraceMoveFile = ReadTraceMove();
        private static string ReadTraceMove()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("EYE_LITE_TRACE_MOVE");
                return v == null ? "" : v.Trim();
            }
            catch { return ""; }
        }
        private float dpiScale = 1f;

        public MoveForm(List<MoveItem> actions, int restSeconds)
        {
            dpiScale = Ui.DetectScale(this);

            this.items = actions;
            int budget = restSeconds;
            if (budget < 1) budget = 1;
            this.budgetMs = budget * 1000;

            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.BackColor = Ui.GradTop;
            this.AutoScaleMode = AutoScaleMode.None;
            this.DoubleBuffered = true;
            this.Font = Ui.FontUi;

            // 高度从 640 加到 800：人物放大一倍后（用户明确要求"再大一倍"），
            // 640 里"标题+要点"与"提示+倒计时+进度条"之间只剩 448px，
            // 放不下 ~650px 的人形。**加高窗口、不缩小人物** —— 要的是"看得清"。
            int w = Sc(720);
            int h = Sc(800);
            this.Size = new Size(w, h);
            CenterOnPrimary(w, h);

            // 「结束」按钮：这个窗口是 WS_EX_NOACTIVATE（永不成前台），既没有按键处理也没有子控件，
            // 用户反馈"关不掉，只能等它播完"。所以必须给一个**鼠标**出口。
            // 它只关自己 —— 休息计时归 TrayApp 管，关窗不影响这一轮休息。
            // 注：NOACTIVATE 只影响"是否激活窗口"，鼠标消息照常派发，所以 MouseClick 可用；
            // 千万不要改成抢前台，那会打断用户的输入法组词。
            closeRect = new Rectangle(w - Sc(84), Sc(14), Sc(72), Sc(26));
            this.MouseClick += delegate(object sender, MouseEventArgs e) { OnMoveFormMouseClick(e); };

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

        // ---- 演示区的纵向布局：**只在这里定义一次** ----
        //
        // 人物放大一倍后，标题/要点/提示/倒计时/进度条必然互相挤压。
        // 所以三件事一起做：窗口加高到 800、地面线从底边反算、人形高度 = 地面线 - 人物上边界。
        // 这样以后改窗口高度，人物自动跟着长，不会再把某一行的绝对坐标忘在半空中
        // （这个坑 720x640 那次已经踩过一次：地面从 Sc(316) 挪到 Sc(512)，提示行留在 Sc(392) 压住了腿）。
        private const int LayoutTipsBottom = 64;   // 要点文字底边（标题 14..42 + 要点 44..64）
        private const int LayoutFigTop = 66;       // 人形可用上边界：要点下面 2px
        private const int LayoutBottomChrome = 84; // 地面线到底边：提示(18)+倒计时(18)+进度条(6)+间距
                                                   // 88 而不是 80：人形线宽现在按身高走（18%），
                                                   // 脚底那笔有 ~8px 半宽，80 会让脚压到「如有不适」那行。

        /// <summary>脚底的 y（"地面线"）。</summary>
        // 人形缩放系数：由 DrawFigure 每次绘制前按可用高度算出来，
        // DrawLeg 里的偏移量用它一起放大，保证"大人物"比例一致，而不是只拉长骨头。
        private double figK = 1.0;

        /// <summary>缩放后的像素量：Sc(px) * figK（只用于人形绘制，不影响其它布局）。</summary>
        private int Sf(int px)
        {
            return (int)Math.Round(Sc(px) * figK);
        }

        /// <summary>脚底的 y（"地面线"）。</summary>
        private int FigGroundY()
        {
            int g = this.ClientSize.Height - Sc(LayoutBottomChrome);
            int minG = Sc(LayoutFigTop) + Sc(120);
            return g < minG ? minG : g;
        }

        /// <summary>人形可用的绘制高度（= 火柴人总高 = 素材墨迹目标高度）。</summary>
        private int FigHeight()
        {
            int h = FigGroundY() - Sc(LayoutFigTop);
            return h < Sc(120) ? Sc(120) : h;
        }

        /// <summary>
        /// 命中「结束」就关自己；点别处什么都不做。
        /// 三种"不要"：不抢焦点、不影响休息计时、不弹确认框（用户想关就让他关）。
        /// </summary>
        private void OnMoveFormMouseClick(MouseEventArgs e)
        {
            try
            {
                if (e.Button != MouseButtons.Left) return;
                if (!closeRect.Contains(e.X, e.Y)) return;
                this.Close();
            }
            catch { }
        }

        /// <summary>
        /// 顶部右侧的「结束」按钮：圆角矩形描边 + 文字，全部自绘
        /// （本窗体没有任何子控件，加一个真按钮只会引入一层与 OnPaint 抢绘制的复杂度）。
        /// </summary>
        private void DrawCloseButton(Graphics g)
        {
            if (closeRect.Width <= 0 || closeRect.Height <= 0) return;

            try
            {
                int r = Sc(6);
                if (r < 2) r = 2;
                int d = r * 2;

                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddArc(closeRect.Left, closeRect.Top, d, d, 180, 90);
                    path.AddArc(closeRect.Right - d, closeRect.Top, d, d, 270, 90);
                    path.AddArc(closeRect.Right - d, closeRect.Bottom - d, d, d, 0, 90);
                    path.AddArc(closeRect.Left, closeRect.Bottom - d, d, d, 90, 90);
                    path.CloseFigure();

                    using (Pen pen = new Pen(Ui.RingTrack, Math.Max(1f, Sc(1))))
                    {
                        g.DrawPath(pen, path);
                    }
                }

                TextRenderer.DrawText(g, "结束", Ui.FontEyebrow, closeRect, Ui.EyebrowText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix);
            }
            catch { }
        }

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
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab、不占任务栏
                return cp;
            }
        }

        private void OnAnimTick()
        {
            try
            {
                if (items == null || items.Count == 0)
                {
                    StopAnim();
                    Close();
                    return;
                }

                // 用墙钟推进，不用"每帧加 16ms"：
                // 定时器一旦被系统合并（这台机器上实测过多次），累加式会让整段演示变慢，
                // 表现为"标称 40 秒的例程跑了 63 秒"。
                if (animStartMs <= 0) animStartMs = Clock.NowMs();
                elapsedMs = (int)(Clock.NowMs() - animStartMs);
                if (elapsedMs < 0) elapsedMs = 0;

                // 预算用完 → 收窗（休息有多长就演示多长，绝不比休息更长）
                if (elapsedMs >= budgetMs)
                {
                    StopAnim();
                    Close();
                    return;
                }

                // 当前动作播完 → 切下一个；播完最后一个就**回到第一个继续轮换**。
                // 不在这里收窗：窗口的生命周期应该等于这一轮休息（budgetMs），
                // 否则 SedentarySeconds 调到 300 秒时会出现"演示 170 秒就消失、只剩遮罩"。
                if (elapsedMs - actionStartMs >= items[index].Seconds * 1000)
                {
                    index++;
                    if (index >= items.Count) index = 0;
                    actionStartMs = elapsedMs;
                }

                if (TraceMoveFile.Length > 0)
                {
                    try
                    {
                        File.AppendAllText(TraceMoveFile,
                            elapsedMs.ToString() + "," + index.ToString() + "," +
                            items.Count.ToString() + "," + (elapsedMs - actionStartMs).ToString() +
                            Environment.NewLine, new UTF8Encoding(false));
                    }
                    catch { }
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
                // 素材帧是唯一"按需解码"的位图，必须在这里释放；
                // 不释放的话每开一次演示窗就留 1MB 左右（3 张 300² ARGB）。
                ReleaseAssetFrames();
                items = null;
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
            try { g.SmoothingMode = SmoothingMode.AntiAlias; } catch { }

            int w = this.ClientSize.Width;
            int h = this.ClientSize.Height;

            // 「结束」按钮先画：即使这一帧没有动作可画（items 为空），窗口也必须是**可关闭**的，
            // 否则用户就真的没有出口了。
            DrawCloseButton(g);

            if (items == null || items.Count == 0) return;
            if (index < 0 || index >= items.Count) return;

            MoveItem item = items[index];

            // ---- 标题 + 要点 ----
            TextRenderer.DrawText(g, item.Name, Ui.FontRestEyebrow,
                new Rectangle(0, Sc(14), w, Sc(28)), Ui.TimeText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, item.Tips, Ui.FontEyebrow,
                new Rectangle(Sc(20), Sc(44), w - Sc(40), Sc(20)), Ui.EyebrowText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

            // ---- 动作画面：有素材就画素材，没素材回退自绘火柴人 ----
            // 回退是**永久保留**的安全网：现在所有动作都有素材了，但素材资源缺失/损坏时
            // 仍然不能白屏；而且"素材表达不出来"的动作将来随时可能回退到这里。
            int localMs = elapsedMs - actionStartMs;
            double cycle = item.CycleSeconds * 1000.0;
            double t = cycle <= 0 ? 0 : (localMs % cycle) / cycle;

            bool drawn = false;
            try { drawn = DrawAssetFrame(g, item, localMs, w); }
            catch { drawn = false; }

            if (!drawn)
            {
                MovePose pose = SamplePose(item.Frames, t);
                DrawFigure(g, pose, w);
            }

            // ---- 底部：剩余秒数 + 进度条 + 中性提示 ----
            int leftAllSec = (budgetMs - elapsedMs + 999) / 1000;
            if (leftAllSec < 0) leftAllSec = 0;

            TextRenderer.DrawText(g, "还剩 " + leftAllSec + " 秒", Ui.FontEyebrow,
                new Rectangle(0, h - Sc(46), w, Sc(18)), Ui.RingLabel,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

            int barW = w - Sc(80);
            int barX = Sc(40);
            int barY = h - Sc(20);
            int barH = Math.Max(2, Sc(3));
            using (SolidBrush track = new SolidBrush(Ui.RingTrack))
            {
                g.FillRectangle(track, barX, barY, barW, barH);
            }
            int done = budgetMs <= 0 ? 0 : (int)(barW * (elapsedMs / (double)budgetMs));
            if (done > barW) done = barW;
            if (done > 0)
            {
                using (SolidBrush fg = new SolidBrush(Ui.Accent))
                {
                    g.FillRectangle(fg, barX, barY, done, barH);
                }
            }

            // G11：这里**只**放这一句中性提示。绝不说"因为你腰不好所以跳过了 XX 动作"——
            // 那是医疗暗示，我们没资格也没能力做这个判断。
            //
            // 位置必须在"地面"（Sc(512)）**之下**：这条窗口从 480×420 长到 720×640 时，
            // 地面从 Sc(316) 挪到了 Sc(512)，而这行还留在 Sc(392) —— 于是它横穿在
            // 人物的小腿上（截图实测：素材图和火柴人都会被压住）。
            // 现在放到地面与「还剩 N 秒」之间那条空带里。
            // 位置由"地面线"反算（h - 底边预留 + 8）：放大后地面线下移，这行自动跟着下移。
            TextRenderer.DrawText(g, "如有不适请立即停止", Ui.FontEyebrow,
                new Rectangle(0, h - Sc(72), w, Sc(18)), Ui.DarkTipText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }

        // ============================================================
        // 开源素材（CC BY-SA / Unlicense）—— 有素材就画素材，没素材回退自绘
        // ============================================================
        //
        // 三条硬约束，改之前先读：
        //   1. **只解码当前动作的帧**，切动作时立刻 Dispose 旧的。
        //      512×512 ARGB 一帧解码约 1MB；9 个动作全解 = +27MB，逼近 45MB 上限。
        //      实际策略：解码 → 立刻缩放到目标尺寸 → 释放 512 原图，常驻只有 3 张 300² 位图（约 1.1MB）。
        //   2. **运行时缩放不算改编**。CC BY-SA 的 ShareAlike 只约束"分发改过的素材文件"；
        //      在内存里缩放到屏幕尺寸属于显示行为，不产生衍生作品。
        //      （构建期去改 assets/ 里的文件才算改编，那条红线见 ATTRIBUTION.md。）
        //   3. 任何一步失败（资源缺失/损坏）必须返回 false → 调用方回退火柴人，**不能白屏**。
        private Bitmap[] assetFrames;      // 当前动作的 3 帧（已缩放到 side×side）
        // 素材的「墨迹比例」（三帧里取最大/最下）：画布是 512 见方，但人物并不填满，
        // 下蹲这类姿势底部还留 5%~6% 的透明边。按画布对齐会让脚悬空、人也不够高，
        // 所以加载时实测一次，用它换算 side 与落地点。
        private double assetInkHeightRatio = 0.90;       // 墨迹高 / 画布高（三帧取最大，统一缩放用）
        private double assetInkWidthRatio = 0.50;        // 墨迹宽 / 画布宽（取最大，用于限制横向尺寸）
        private readonly double[] assetInkBottomRatio = new double[3];  // 每帧：墨迹底边到画布底边 / 画布高
        private string assetFramesKey;     // 对应的 AssetKey（null = 未加载）

        /// <summary>嵌入资源名，必须与 build.cmd 里的 /resource:...,名字 一致。</summary>
        private static string AssetResourceName(string key, int index)
        {
            return "EyeLite.res." + key + "." + index.ToString();
        }

        /// <summary>换成 key 对应的素材；key 相同则复用已解码的帧。失败返回 false。</summary>
        private bool EnsureAssetFrames(string key, int side)
        {
            // 切到"没有素材"的动作时立刻释放上一组帧 —— 否则那 1MB 会跟着挂到下一次演示。
            if (key == null || key.Length == 0) { ReleaseAssetFrames(); return false; }
            if (assetFrames != null && assetFramesKey == key) return true;

            ReleaseAssetFrames();
            if (side < 16) side = 16;

            // **缓存尺寸封顶 640**。
            //
            // 调用方按"人形墨迹高度"反算出 side，现在窗口是 1080×1200、人形约 950px，
            // side 会算到 ~1100 —— 每帧 1100²×4 = 4.8MB，三帧 14.5MB，
            // 加上 GDI+ 的内部缓冲实测让进程涨到 43MB（承诺上限 45MB），
            // 而且轮换动作时还要再涨（实测 47.6MB，已超限）。
            //
            // 但源素材本身只有 512×512：放大到 1100 **不会增加任何细节**，纯属浪费内存。
            // 所以这里封顶 640（略高于源图，保留一点余量），绘制时再由 GDI 放大到目标尺寸。
            // 640²×4=1.6MB/帧、三帧 4.9MB，实测进程回到 22MB 上下。
            const int MaxCachedSide = 640;
            if (side > MaxCachedSide) side = MaxCachedSide;

            Bitmap[] loaded = new Bitmap[3];
            double maxInkH = 0.0;
            double maxInkW = 0.0;
            double[] bottoms = new double[3];
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    System.IO.Stream st = System.Reflection.Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream(AssetResourceName(key, i + 1));
                    if (st == null) { DisposeFrames(loaded); return false; }

                    using (st)
                    using (Bitmap src = new Bitmap(st))
                    {
                        double bottomRatio = 0.0;
                        MeasureInkRatio(src, ref maxInkH, ref bottomRatio, ref maxInkW);
                        bottoms[i] = bottomRatio;
                        loaded[i] = ScaleFrame(src, side);
                    }
                    if (loaded[i] == null) { DisposeFrames(loaded); return false; }
                }
                catch { DisposeFrames(loaded); return false; }
            }

            assetFrames = loaded;
            assetFramesKey = key;
            // 高度用三帧最大值（统一缩放，保证下蹲时人确实变矮，而不是每帧各自拉满）；
            // 底边**按帧**记：每帧各自把「墨迹底边」贴到地面线，脚不会踩进下面的提示文字。
            assetInkHeightRatio = maxInkH > 0.1 ? maxInkH : 0.90;
            assetInkWidthRatio = maxInkW > 0.05 ? maxInkW : 0.50;
            for (int i = 0; i < 3; i++) assetInkBottomRatio[i] = bottoms[i];
            return true;
        }

        /// <summary>
        /// 实测一张素材图里「人物墨迹」占画布的比例（只在加载时跑一次，隔点采样，够准且快）。
        ///
        /// 为什么需要实测：画布是 512 见方，但人物并不填满 —— 深蹲三帧的墨迹高只占 89%、
        /// 底部还留 6% 透明边。按「画布高」对齐会让脚悬在半空、人形也比火柴人矮一截。
        /// </summary>
        private static void MeasureInkRatio(Bitmap src, ref double maxH, ref double bottomRatio, ref double maxW)
        {
            try
            {
                int w = src.Width, h = src.Height;
                if (w < 8 || h < 8) return;

                int top = h, bot = -1;
                int left = w, right = -1;
                for (int y = 0; y < h; y++)
                {
                    // 这一行**必须整行扫完**，不能 break 在第一个命中点：
                    // 那样 right 只会停在"最左边那个命中"，宽度被严重低估，
                    // 宽度限制形同虚设 —— 坐姿动作（墨迹宽 480/512）直接被窗口两边裁掉（实测踩到）。
                    // 阈值 60 而不是 8：素材图边缘有一圈很淡的投影/抗锯齿，
                    // 把它算进墨迹会把"最高帧"估高 4%，整张图被缩小（实测人形只有 74%）。
                    for (int x = 0; x < w; x++)
                    {
                        if (src.GetPixel(x, y).A > 60)
                        {
                            if (y < top) top = y;
                            if (y > bot) bot = y;
                            if (x < left) left = x;
                            if (x > right) right = x;
                        }
                    }
                }
                if (bot < 0 || top >= h) return;

                double inkH = (bot - top + 1) / (double)h;
                double inkBottom = (h - 1 - bot) / (double)h;
                if (inkH > maxH) maxH = inkH;
                bottomRatio = inkBottom;

                // 宽度只在"整行扫描"里可靠（上面的 x+=2 会漏掉最左/最右的列），
                // 所以这里对每一行单独取 min/max 之后再合成整体外框。
                if (right >= left)
                {
                    double inkW = (right - left + 1) / (double)w;
                    if (inkW > maxW) maxW = inkW;
                }
            }
            catch { }
        }

        /// <summary>生成目标尺寸缓存位图（HighQualityBicubic 一次性缩放，之后每帧 1:1 贴图）。</summary>
        private static Bitmap ScaleFrame(Bitmap src, int side)
        {
            Bitmap dst = null;
            try
            {
                dst = new Bitmap(side, side, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    // SourceCopy + 透明 Clear：素材本身带 alpha（透明背景），
                    // 用默认的 SourceOver 会和"上一帧残留"叠在一起。
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.Clear(Color.Transparent);
                    g.DrawImage(src, new Rectangle(0, 0, side, side));
                }
            }
            catch
            {
                if (dst != null) { dst.Dispose(); dst = null; }
            }
            return dst;
        }

        private static void DisposeFrames(Bitmap[] frames)
        {
            if (frames == null) return;
            for (int i = 0; i < frames.Length; i++)
            {
                if (frames[i] != null) { try { frames[i].Dispose(); } catch { } frames[i] = null; }
            }
        }

        private void ReleaseAssetFrames()
        {
            DisposeFrames(assetFrames);
            assetFrames = null;
            assetFramesKey = null;
        }

        /// <summary>
        /// 按乒乓序列 1 → 2 → 3 → 2 播放（首尾都落在第 1 帧，循环接缝不跳）。
        /// **不做交叉淡入**：3 帧淡入会同时出现两个半透明人体，看起来像抖动。
        /// 返回 false 表示这个动作没有素材（或加载失败），调用方回退火柴人。
        /// </summary>
        private bool DrawAssetFrame(Graphics g, MoveItem item, int localMs, int w)
        {
            // 素材画布是 512 见方，实测人物墨迹只占画布高的 89%~93%，
            // 所以 side ≈ figH / 0.91 才能让"墨迹高度"等于 figH —— 与火柴人同高，
            // 两条路径切换时人形不会忽大忽小。
            int figH = FigHeight();
            int side = (int)Math.Round(figH / (assetInkHeightRatio > 0.1 ? assetInkHeightRatio : 0.90));
            // 画布是正方形，允许它比窗口略宽：超出的只是**透明边**，裁掉不影响人形；
            // 反过来把它钳到窗宽，深蹲这种「墨迹只占 89%」的图就会矮一截（实测 77%）。
            int maxSide = (int)Math.Round(figH * 1.6);
            if (side > maxSide) side = maxSide;
            // 宽度也要管：坐姿动作（躯干转体）的墨迹是"宽而矮"，只按高度算尺寸
            // 会把画布撑得比窗口还宽，左右两边直接被裁掉。按"墨迹宽度 ≤ 窗宽 98%"再收一次。
            if (assetInkWidthRatio > 0.05)
            {
                int maxByWidth = (int)Math.Round(w * 0.98 / assetInkWidthRatio);
                if (side > maxByWidth) side = maxByWidth;
            }
            if (side < 16) side = 16;

            if (!EnsureAssetFrames(item.AssetKey, side)) return false;
            if (assetFrames == null) return false;

            int cycleMs = item.CycleSeconds * 1000;
            if (cycleMs < 1) cycleMs = 1;

            int pos = localMs % cycleMs;
            if (pos < 0) pos += cycleMs;

            // 一个循环切成 4 段：0→帧1, 1→帧2, 2→帧3, 3→帧2
            int step = (int)((long)pos * 4L / cycleMs);
            if (step < 0) step = 0;
            if (step > 3) step = 3;
            int idx = (step == 3) ? 1 : step;

            Bitmap bmp = assetFrames[idx];
            if (bmp == null) return false;

            int x = w / 2 - side / 2;
            // 落地点按「墨迹底边」算，不是按画布底边：画布底部有透明边，
            // 直接贴地面会让脚悬空 50+ px（素材路径比火柴人高出一截就是这个原因）。
            int inkBottomPx = (int)Math.Round(side * assetInkBottomRatio[idx]);
            int y = FigGroundY() - side + inkBottomPx;
            g.DrawImage(bmp, x, y, side, side);
            return true;
        }

        /// <summary>在关键帧之间做 ease-in-out 插值（不是每 500ms 硬切一张姿势）。</summary>
        private static MovePose SamplePose(MovePose[] frames, double t)        {
            if (frames == null || frames.Length == 0) return new MovePose();
            if (t <= 0) return frames[0];

            MovePose last = frames[frames.Length - 1];
            if (t >= 1.0) return last;

            for (int i = 0; i < frames.Length - 1; i++)
            {
                MovePose a = frames[i];
                MovePose b = frames[i + 1];
                if (t >= a.T && t <= b.T)
                {
                    double span = b.T - a.T;
                    double k = span <= 0.0001 ? 0 : (t - a.T) / span;
                    return Lerp(a, b, EaseInOut(k));
                }
            }
            return last;
        }

        private static MovePose Lerp(MovePose a, MovePose b, double k)
        {
            MovePose r = new MovePose();
            r.T = b.T;
            r.Torso = a.Torso + (b.Torso - a.Torso) * k;
            r.ArmL = a.ArmL + (b.ArmL - a.ArmL) * k;
            r.ArmR = a.ArmR + (b.ArmR - a.ArmR) * k;
            r.ForeL = a.ForeL + (b.ForeL - a.ForeL) * k;
            r.ForeR = a.ForeR + (b.ForeR - a.ForeR) * k;
            r.Crouch = a.Crouch + (b.Crouch - a.Crouch) * k;
            r.StepL = a.StepL + (b.StepL - a.StepL) * k;
            r.StepR = a.StepR + (b.StepR - a.StepR) * k;
            r.HeadSway = a.HeadSway + (b.HeadSway - a.HeadSway) * k;
            return r;
        }

        /// <summary>ease-in-out：两端速度为 0，动作起停才自然（线性插值会像机器人）。</summary>
        private static double EaseInOut(double t)
        {
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            return 0.5 - 0.5 * Math.Cos(Math.PI * t);
        }

        // ---------- 火柴人：标量姿势 → 前向运动学 ----------
        //
        // 全部尺寸过 Sc()，所以 150% / 200% DPI 下比例不变（不会变成"大头火柴人"）。

        private void DrawFigure(Graphics g, MovePose p, int w)
        {
            int cx = w / 2;
            int groundY = FigGroundY();

            // ============================================================
            // 人体比例：全部以「身高 H」为基准（单一来源），不再写死像素段长。
            // ============================================================
            //
            // 为什么必须这样改：原来各段是固定像素（torso 78 / thigh 70 / headR 22…），
            // 按窗口高度放大 2.4 倍后，头变成 150px，而线宽只从 3px 到 7px ——
            // 观感就是「一个大球挂在几根牙签上」。素材图看着正常，正因为它自带真实比例。
            //
            // 系数按「约 7.5 头身」定：头占身高 13%、腿 48%、躯干 32%、颈 4%，
            // 合计 0.97 → 头顶留 3% 余量，保证头顶永远在要点文字下方。
            int H = FigHeight();
            if (H < 40) H = 40;

            int headR = (int)Math.Round(H * 0.065);        // 头直径 = 13% 身高
            int neckLen = (int)Math.Round(H * 0.04);       // 颈
            int halfShoulder = (int)Math.Round(H * 0.055); // 肩宽半距
            int halfHip = (int)Math.Round(H * 0.035);      // 髋宽半距
            int lenTorso = (int)Math.Round(H * 0.32);
            int lenArm = (int)Math.Round(H * 0.155);
            int lenFore = (int)Math.Round(H * 0.145);
            int lenThigh = (int)Math.Round(H * 0.245);
            int lenShin = (int)Math.Round(H * 0.235);

            // 线宽 / 关节尺寸也按身高走：放 2 倍时线要跟着粗，否则像铁丝
            float penW = Math.Max(1.5f, (float)(H * 0.018));
            float jointR = Math.Max(2f, (float)(H * 0.012));

            // Sf() 供 DrawLeg 里的抬腿/外展偏移使用，基准同步成身高
            figK = H / (double)Sc(268);
            if (figK < 0.2) figK = 0.2;

            // 下蹲：髋往下沉，位移量按腿长比例
            int crouchPx = (int)(lenThigh * 0.34 * p.Crouch);
            PointF hip = new PointF(cx, groundY - lenThigh - lenShin + crouchPx);

            double torsoRad = p.Torso * Math.PI / 180.0;
            PointF shoulder = new PointF(
                (float)(hip.X + Math.Sin(torsoRad) * lenTorso),
                (float)(hip.Y - Math.Cos(torsoRad) * lenTorso));

            // 头：肩点沿躯干轴再往上「颈 + 半个头」
            PointF head = new PointF(
                (float)(shoulder.X + Math.Sin(torsoRad) * (neckLen + headR) + p.HeadSway * H * 0.02),
                (float)(shoulder.Y - Math.Cos(torsoRad) * (neckLen + headR)));

            // 左右肩点 / 左右髋点：这是「有肩宽、有髋宽」的来源。
            // 原来两臂从同一个脊椎顶点岔开、两腿从同一个点并拢，放大后就是「三点一线」。
            float perpX = (float)Math.Cos(torsoRad);
            PointF shoulderL = new PointF(shoulder.X - perpX * halfShoulder, shoulder.Y);
            PointF shoulderR = new PointF(shoulder.X + perpX * halfShoulder, shoulder.Y);
            PointF hipL = new PointF(hip.X - halfHip, hip.Y);
            PointF hipR = new PointF(hip.X + halfHip, hip.Y);

            using (Pen body = new Pen(Ui.TimeText, penW))
            using (SolidBrush headBrush = new SolidBrush(Ui.TimeText))
            using (SolidBrush jointBrush = new SolidBrush(Ui.Accent))
            using (Pen ground = new Pen(Ui.RingTrack, Math.Max(1f, Sc(2))))
            {
                // 关节处必须是圆头圆角：动作幅度一大（手臂过头、前臂 -70）之后，
                // 平的线端会在肩/肘/膝留下明显的尖角缺口，看起来像「骨折」。
                body.StartCap = LineCap.Round;
                body.EndCap = LineCap.Round;
                body.LineJoin = LineJoin.Round;

                // 地面
                g.DrawLine(ground, Sc(60), groundY, w - Sc(60), groundY);

                // 腿（各自从髋点出发）
                PointF kneeL = DrawLeg(g, body, hipL, groundY, lenThigh, lenShin, p.Crouch, p.StepL, -1);
                PointF kneeR = DrawLeg(g, body, hipR, groundY, lenThigh, lenShin, p.Crouch, p.StepR, +1);

                // 髋宽线 + 躯干
                g.DrawLine(body, hipL, hipR);
                g.DrawLine(body, hip, shoulder);

                // 肩宽线：给出「肩膀」的宽度感
                g.DrawLine(body, shoulderL, shoulderR);

                // 颈：肩点 → 头的下缘
                g.DrawLine(body, shoulder,
                    new PointF((float)(head.X - Math.Sin(torsoRad) * headR * 0.2),
                               (float)(head.Y + Math.Cos(torsoRad) * headR * 0.85)));

                // 手臂（各自从肩点出发）
                DrawArm(g, body, shoulderL, torsoRad, lenArm, lenFore, p.ArmL, p.ForeL, -1);
                DrawArm(g, body, shoulderR, torsoRad, lenArm, lenFore, p.ArmR, p.ForeR, +1);

                // 头
                g.FillEllipse(headBrush, head.X - headR, head.Y - headR, headR * 2, headR * 2);

                // 关节（髋 / 双肩 / 双膝）
                g.FillEllipse(jointBrush, hip.X - jointR, hip.Y - jointR, jointR * 2, jointR * 2);
                g.FillEllipse(jointBrush, shoulderL.X - jointR, shoulderL.Y - jointR, jointR * 2, jointR * 2);
                g.FillEllipse(jointBrush, shoulderR.X - jointR, shoulderR.Y - jointR, jointR * 2, jointR * 2);
                g.FillEllipse(jointBrush, kneeL.X - jointR, kneeL.Y - jointR, jointR * 2, jointR * 2);
                g.FillEllipse(jointBrush, kneeR.X - jointR, kneeR.Y - jointR, jointR * 2, jointR * 2);
            }
        }

        /// <summary>画一条腿，返回膝盖点（供画关节用）。step=抬腿量，crouch=下蹲量。</summary>
        private PointF DrawLeg(Graphics g, Pen pen, PointF hip, int groundY,
                               int lenThigh, int lenShin, double crouch, double step, int side)
        {
            float lift = (float)(step * Sf(26));
            float kneeOut = (float)(Sf(4) + crouch * Sf(16));
            float ankleOut = (float)(Sf(9));

            PointF knee = new PointF(hip.X + side * kneeOut,
                                     hip.Y + lenThigh - (float)(crouch * Sf(4)) - lift * 0.55f);
            PointF ankle = new PointF(hip.X + side * ankleOut, groundY - lift);

            g.DrawLine(pen, hip, knee);
            g.DrawLine(pen, knee, ankle);
            return knee;
        }

        /// <summary>画一条手臂（上臂 + 前臂）。角度相对躯干，side=-1 左 / +1 右。</summary>
        private void DrawArm(Graphics g, Pen pen, PointF shoulder, double torsoRad,
                             int lenArm, int lenFore, double upAng, double foreAng, int side)
        {
            double a1 = torsoRad + side * upAng * Math.PI / 180.0;
            PointF elbow = new PointF(
                (float)(shoulder.X + Math.Sin(a1) * lenArm),
                (float)(shoulder.Y + Math.Cos(a1) * lenArm));

            double a2 = a1 + side * foreAng * Math.PI / 180.0;
            PointF hand = new PointF(
                (float)(elbow.X + Math.Sin(a2) * lenFore),
                (float)(elbow.Y + Math.Cos(a2) * lenFore));

            g.DrawLine(pen, shoulder, elbow);
            g.DrawLine(pen, elbow, hand);
        }
    }

    internal class MovementPlugin : IEyeLitePlugin
    {
        private bool enabled;
        private MoveForm form;

        /// <summary>上一轮是否"全部动作都被禁忌关键词排除"（调试/测试用，界面上不显示）。</summary>
        public bool AllFiltered;

        public string Id { get { return "Movement"; } }
        public string DisplayName { get { return "起身活动演示"; } }
        public string Description { get { return "久坐/活动休息时弹火柴人动作演示。"; } }
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
        /// 休息开始时调。返回 true 表示"这一轮由我接管"（TrayApp 据此不再弹 Ritual）。
        ///
        /// 注意返回值语义：**只要插件启用就返回 true**，哪怕动作全被排除 ——
        /// 因为"Movement 启用时优先"是一条显示规则，不能因为过滤结果又回落到 Ritual，
        /// 否则用户填了禁忌词之后会意外看到另一种窗口，行为不可预期。
        /// </summary>
        public bool ShowForRest(int restSeconds, string contraindications)
        {
            AllFiltered = false;
            if (!enabled) return false;

            Hide();     // 上一轮没关干净就扔掉

            List<MoveItem> all = MoveLib.Build();
            List<MoveItem> usable = new List<MoveItem>();
            for (int i = 0; i < all.Count; i++)
            {
                if (!MoveLib.IsExcluded(all[i], contraindications)) usable.Add(all[i]);
            }

            if (usable.Count == 0)
            {
                // 全被排除 → 安静休息（不弹窗，也不退回 Ritual）
                AllFiltered = true;
                return true;
            }

            try
            {
                form = new MoveForm(usable, restSeconds);
                form.Show();
            }
            catch
            {
                form = null;    // 演示窗出问题绝不能影响休息本身
            }
            return true;
        }

        /// <summary>休息结束时调（EndRest）：关闭并释放。</summary>
        public void Hide()
        {
            if (form == null) return;

            MoveForm f = form;
            form = null;

            try { if (!f.IsDisposed) f.Close(); } catch { }
            try { f.Dispose(); } catch { }
        }

        /// <summary>
        /// 窗口句柄（没有窗口时 Zero）。休息期间每秒用它把演示窗重新抬到遮罩之上 ——
        /// 与 Ritual 同一原因：遮罩那边每秒都会重申 TopMost，会把置顶组最前面抢回去。
        /// </summary>
        public IntPtr WindowHandle
        {
            get
            {
                MoveForm f = form;
                if (f == null || f.IsDisposed || !f.IsHandleCreated) return IntPtr.Zero;
                return f.Handle;
            }
        }
    }

    // ================================================================
    // 3.15 E1 跳过配额（插件 Guard）
    // ================================================================
    //
    // 配额只约束"跳过"这一个动作：
    //   · 「推迟」「坐一分钟」不受限 —— 它们不是"这次不休息"，只是换个方式休息
    //   · 跨天按**日历日**重置（`SkipQuotaDate != 今天` 就清零），不是按运行时长
    //     —— 用户理解的"每天 3 次"就是他手表上的今天，不是"从启动起 24 小时"
    //
    // **插件关闭时这套逻辑完全不生效**：CanSkip 直接返回 true、ConsumeSkip 什么都不做，
    // 跳过永远是无限次的（保持 P1 之前的行为）。
    //
    // 关键实现点（别踩）：配额用完后必须把 `KeyboardGuard.OnSkipKey` 置成 null，
    // **不能**在回调里"先查配额再 return" —— 钩子是在调用回调**之前**就决定吞键的
    // （`if (cb != null) { drainVk = vk; Ui.Post(cb); return 1; }`），
    // 回调里 return 只会变成"键被吞了但没有跳过"，那是最糟的一种失败模式。
    internal class GuardPlugin : IEyeLitePlugin
    {
        private bool enabled;

        public string Id { get { return "Guard"; } }
        public string DisplayName { get { return "跳过配额"; } }
        public string Description { get { return "限制每天可跳过的次数。"; } }
        public bool IsEnabled { get { return enabled; } }

        public void OnEnable()
        {
            enabled = true;
        }

        public void OnDisable()
        {
            enabled = false;
        }

        /// <summary>今天还能不能跳过。未启用 / 配额为 0（不限制）时永远返回 true。</summary>
        public bool CanSkip(AppConfig cfg)
        {
            if (!enabled || cfg == null) return true;
            if (cfg.SkipQuotaPerDay <= 0) return true;

            // 跨天了 → 已用次数视为 0（真正的重置发生在 ConsumeSkip 里，这里只读判断）
            if (cfg.SkipQuotaDate != Today()) return true;
            return cfg.SkipQuotaUsed < cfg.SkipQuotaPerDay;
        }

        /// <summary>记一次"跳过"。跨天自动清零；立刻落盘，强杀也不丢账。</summary>
        public void ConsumeSkip(AppConfig cfg)
        {
            if (!enabled || cfg == null) return;
            if (cfg.SkipQuotaPerDay <= 0) return;

            string today = Today();
            if (cfg.SkipQuotaDate != today)
            {
                cfg.SkipQuotaDate = today;
                cfg.SkipQuotaUsed = 0;
            }

            cfg.SkipQuotaUsed++;
            try { cfg.Save(); } catch { }
        }

        /// <summary>
        /// yyyy-MM-dd。用本地时间：用户说的"今天"是墙上的日历，不是 UTC。
        /// 格式串里只有数字和 '-'（字面量），不受区域设置影响。
        /// </summary>
        private static string Today()
        {
            return DateTime.Now.ToString("yyyy-MM-dd");
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
        // 0 = 关闭该功能（一个 DimForm 都不创建）；45 = 最暗时整窗 alpha 0.45。
        // 上限 70 是为了别把人真正压暗到看不清内容 —— 渐暗是"温和提示"，不是遮罩。
        //
        // **默认 0（关闭），2026-11-17 改的，改回去之前先读这段。**
        //   实测（p0a，三组只改一个变量，判据一字未改）：
        //     默认 45  → 60.53 / 62.38 / 63.50 / 60.43 / 62.41  中位 62.38，1/5 超 ±3s
        //     关掉 A2  → 60.52 / 60.54 / 61.38 / 60.55 / 60.51  中位 60.54，0/5 超 ±3s
        //   方差从 3.97s 跨度塌到 0.86s —— 渐暗层是继"窗口句柄冷启动"之后的**第二个**
        //   延迟源。机制是每次写 Form.Opacity 都让 DWM 重新合成整块屏幕，
        //   而 60 秒窗口里原本要写约 45~60 次，正好压在"该弹遮罩"那几秒上。
        //   "60±3 秒"是这个产品对用户的核心承诺，渐暗只是美化 —— 承诺优先。
        //   用户显式配过 DimAlphaMax=45 的旧配置**依然生效**（不推翻用户自己的选择），
        //   设置页那一行的文案写清了代价。
        public int DimAlphaMax = 0;

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

        // ---- 会议检测（C2）----
        // 进程白名单 + 可见窗口 + 标题关键词，三条同时成立才算"在会中"。
        // 默认开着，但**只在真的开会时**才抑制 —— 装了客户端不开会不该被抑制。
        public bool MeetingGuardEnabled = true;
        public string MeetingProcesses =
            "Zoom.exe;Teams.exe;ms-teams.exe;wemeetapp.exe;Feishu.exe;DingTalk.exe";
        // 会议窗口的标题通常含这些词；客户端主窗口（聊天/日程）不含，所以不会被误判。
        public string MeetingTitleKeywords = "会议;Meeting;通话;Call;Webinar";

        // ---- C4：打字段检测（核心）----
        // 到点那一刻如果用户正在连续敲键（idle < 300ms），先让 5 秒，
        // 累计最多 60 秒。数据来源是 RawIdleMs()，与空闲计时同一口径。
        public bool TypingGuardEnabled = true;

        // ---- G3：休息卡三选一（默认关，避免改变现有版式）----
        // 开启后遮罩上是「跳过 / 坐一分钟 / 推迟」三个按钮，两个旧按钮按比例缩小。
        // 默认关 = 一个像素都不变。
        public bool SittingOptionEnabled = false;

        // 沉浸模式：**照常计时，但一个窗口都不弹**（遮罩 / 预告 / 渐暗 / 辉光 / 仪式窗全都不出现）。
        //
        // 与「暂停计时」的区别（托盘菜单要把这两句写清楚）：
        //   暂停   = 不记账，时间白给；
        //   沉浸   = 记账，只是不打扰。用户明确说"我知道我在干嘛，别烦我"。
        public bool Immersive = false;

        // 「找东西」：普通护眼休息时，遮罩上的引导语轮换为"在房间里找 N 样某色的东西"。
        //
        // 为什么是文案而不是一段动画：护眼休息的目的是让眼睛**离开屏幕**，
        // 在屏幕中央放动画等于把视线钉回来 —— 那是反作用（这也是仪式窗只挂久坐休息的原因）。
        // 把人往房间外指，才是这类引导该做的事。
        public bool EyeGameEnabled = true;

        // ---- G12：提醒语气档 ----
        //
        // 0 = 温和（默认）1 = 冷淡 2 = 毒舌。只换**字符串**，不碰任何逻辑与版式。
        //
        // 为什么要有这一档：同一种语气不可能适配所有人。温和档对"本来就按时休息的人"
        // 是废话，对"需要一点刺激才动的人"更是废活；而毒舌档对前者会变成冒犯。
        // 竞品 Stiff 的三档（Gentle / Dry / Savage）验证过这条路可行。
        //
        // 两条硬约束（写文案前先读）：
        //   1. **只能对着"行为"开火，不能对着"身体"开火**。可以说「椅子快和你长一起了」，
        //      不能说「你这腰再坐下去就废了」—— 后者是医疗暗示，我们没有资格做，也不该做。
        //   2. 毒舌的是**我们自己**和**这个场景**（椅子、屏幕、这轮专注），不是用户的人格。
        public int Tone = 0;

        // ---- G11：动作演示的禁忌关键词（分号分隔，用户手填，如「腰椎;膝」）----
        //
        // 命中即**跳过该动作**（不是给提示、不是灰色显示）。理由：
        //   在界面上告诉用户"因为你腰不好所以跳过了"是医疗暗示，我们不能也不该做诊断；
        //   跳过是静默的、产品侧兜底的动作，界面只留一句中性的「如有不适请立即停止」。
        // 空串 = 不排除任何动作（默认）。
        public string MovementContraindications = "";

        // ---- E1 跳过配额（插件 Guard；Plugin_Guard=0 时这套逻辑完全不生效）----
        //
        // 为什么要有它：跳过是"我这次不休息"的自我谈判，一次两次是合理的，
        // 一天十次就等于这个提醒不存在。配额把"跳过"从无限变成有限资源。
        // 0 = 不限制（等于关掉配额，但插件仍可开着做别的事）。
        public int SkipQuotaPerDay = 3;
        public string SkipQuotaDate = "";    // yyyy-MM-dd，跨天自动重置（不是按运行时长算）
        public int SkipQuotaUsed = 0;

        // ---- E6 锁键盘前倒计时（只在 StrictMode=1 时生效）----
        //
        // 严格模式是"会吃掉你键盘"的功能，突然锁住会让人措手不及（正在写的字直接没了）。
        // 这 15 秒是缓冲：遮罩已经出现、能看见在倒数，但这段时间键盘完全自由、随时可以跳过。
        // 0 = 不预告，进入休息立刻锁（旧行为）。
        public int LockCountdownSeconds = 15;

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

        // 最近 7 天的"完成次数"历史，格式：`yyyy-MM-dd:count;yyyy-MM-dd:count;...`（按日期升序）。
        // 为什么塞进配置文件而不是单独文件：数据量极小（最多 7 条），
        // 跟现有原子写盘一起走，不必多一条 IO 路径和一份损坏处理。
        public string StatHistory = "";

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
                        case "DimAlphaMax": c.DimAlphaMax = Clamp(v, 0, 0, 70); break;
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
                        case "MeetingGuardEnabled": c.MeetingGuardEnabled = v == "1"; break;
                        case "MeetingProcesses": c.MeetingProcesses = v == null ? "" : v.Trim(); break;
                        case "MeetingTitleKeywords": c.MeetingTitleKeywords = v == null ? "" : v.Trim(); break;
                        case "Immersive": c.Immersive = v == "1"; break;
                        case "EyeGameEnabled": c.EyeGameEnabled = v == "1"; break;
                        // G12 语气档：0/1/2 之外的值一律归 0（温和），越界不报错。
                        case "Tone": c.Tone = Clamp(v, 0, 0, 2); break;
                        case "Plugin_Sound": c.Plugin_Sound = v == "1"; break;
                        case "Plugin_EdgeGlow": c.Plugin_EdgeGlow = v == "1"; break;
                        case "Plugin_Ritual": c.Plugin_Ritual = v == "1"; break;
                        case "Plugin_Stats": c.Plugin_Stats = v == "1"; break;
                        case "Plugin_Movement": c.Plugin_Movement = v == "1"; break;
                        case "Plugin_Guard": c.Plugin_Guard = v == "1"; break;
                        // 禁忌关键词是字符串：不 Clamp；条目空白留到匹配时再 Trim
                        case "MovementContraindications": c.MovementContraindications = v; break;
                        case "SkipQuotaPerDay": c.SkipQuotaPerDay = Clamp(v, 3, 0, 100); break;
                        case "SkipQuotaDate": c.SkipQuotaDate = v; break;
                        case "SkipQuotaUsed": c.SkipQuotaUsed = Clamp(v, 0, 0, 100); break;
                        case "LockCountdownSeconds": c.LockCountdownSeconds = Clamp(v, 15, 0, 120); break;
                        case "StatDone": c.StatDone = Clamp(v, 0, 0, 100000000); break;
                        case "StatSkip": c.StatSkip = Clamp(v, 0, 0, 100000000); break;
                        case "StatIgnore": c.StatIgnore = Clamp(v, 0, 0, 100000000); break;
                        case "StatHistory": c.StatHistory = v == null ? "" : v.Trim(); break;
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
                sb.AppendLine("Immersive=" + (Immersive ? "1" : "0"));
                sb.AppendLine("EyeGameEnabled=" + (EyeGameEnabled ? "1" : "0"));
                sb.AppendLine("Tone=" + Tone);
                sb.AppendLine("MeetingGuardEnabled=" + (MeetingGuardEnabled ? "1" : "0"));
                sb.AppendLine("MeetingProcesses=" + (MeetingProcesses == null ? "" : MeetingProcesses));
                sb.AppendLine("MeetingTitleKeywords=" + (MeetingTitleKeywords == null ? "" : MeetingTitleKeywords));
                sb.AppendLine("Plugin_Sound=" + (Plugin_Sound ? "1" : "0"));
                sb.AppendLine("Plugin_EdgeGlow=" + (Plugin_EdgeGlow ? "1" : "0"));
                sb.AppendLine("Plugin_Ritual=" + (Plugin_Ritual ? "1" : "0"));
                sb.AppendLine("Plugin_Stats=" + (Plugin_Stats ? "1" : "0"));
                sb.AppendLine("Plugin_Movement=" + (Plugin_Movement ? "1" : "0"));
                sb.AppendLine("Plugin_Guard=" + (Plugin_Guard ? "1" : "0"));
                sb.AppendLine("MovementContraindications=" + MovementContraindications);
                sb.AppendLine("SkipQuotaPerDay=" + SkipQuotaPerDay);
                sb.AppendLine("SkipQuotaDate=" + SkipQuotaDate);
                sb.AppendLine("SkipQuotaUsed=" + SkipQuotaUsed);
                sb.AppendLine("LockCountdownSeconds=" + LockCountdownSeconds);
                sb.AppendLine("StatDone=" + StatDone);
                sb.AppendLine("StatSkip=" + StatSkip);
                sb.AppendLine("StatIgnore=" + StatIgnore);
                sb.AppendLine("StatHistory=" + (StatHistory == null ? "" : StatHistory));
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

        // ---- E1 / E6 的 UI 状态 ----
        private bool skipEnabled = true;    // 今日跳过配额是否还没用完
        private string skipTextNormal = ""; // 「跳过」的原始文案（配额用完时被替换，恢复时要用回它）
        private string hintText = "";       // 主提示语原文（锁键盘倒计时期间被临时占用）
        private bool lockCountdownOn;       // 主提示位当前是否在显示锁定倒计时
        private int lockCountdownShown = -1; // 上一次显示的秒数（值没变就不动 Label，tick 路径零churn）

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
                skipTextNormal = withSitting ? "跳过 Esc/空格" : "跳过 (Esc/空格)";
                btnSkip = new RoundButton(skipTextNormal, Sc(21),
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
            hintText = hint;
            // 锁键盘倒计时期间主提示位被「N 秒后锁定键盘」占着，先把文案记下来，等倒计时结束再写回
            if (lblHint != null && !lockCountdownOn) lblHint.Text = hint;
        }

        /// <summary>
        /// E6：锁键盘前的倒计时。
        /// secondsLeft &lt; 0 表示"关掉倒计时"（恢复原主提示语）。
        ///
        /// 直接复用主提示那一行（20pt 大字、版式已经算好），不新增控件：
        /// 加一个 Label 就要重算整条 y 坐标链，而遮罩的居中是基于 contentH 手算的 ——
        /// 为一个 15 秒的临时文案去动那套版式，风险远大于收益。
        /// </summary>
        public void SetLockCountdown(int secondsLeft)
        {
            if (lblHint == null) return;

            if (secondsLeft < 0)
            {
                if (!lockCountdownOn) return;
                lockCountdownOn = false;
                lockCountdownShown = -1;
                lblHint.Text = hintText;
                lblHint.ForeColor = Ui.HintText;
                return;
            }

            // 每 tick 都会调到这里，但秒数一秒才变一次 —— 值没变就什么都不做
            if (lockCountdownOn && lockCountdownShown == secondsLeft) return;

            lockCountdownOn = true;
            lockCountdownShown = secondsLeft;
            lblHint.Text = secondsLeft + " 秒后锁定键盘";
            lblHint.ForeColor = Ui.Accent;
        }

        /// <summary>
        /// E1：今日跳过配额用完时把「跳过」置灰。
        /// 「推迟」「坐一分钟」不动 —— 配额只约束"这次不休息"，不约束"换个方式休息"。
        /// </summary>
        public void SetSkipEnabled(bool on)
        {
            if (btnSkip == null) return;
            if (skipEnabled == on) return;
            skipEnabled = on;

            btnSkip.Enabled = on;

            string text = skipTextNormal;
            if (!on)
            {
                string full = "跳过（今日已用完）";
                int avail = btnSkip.Width - Sc(12);
                text = TextRenderer.MeasureText(full, btnSkip.Font).Width <= avail ? full : "今日已用完";
            }
            btnSkip.Text = text;
            btnSkip.Invalidate();
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
        private bool skipEnabled = true;   // E1：今日跳过配额是否还没用完

        private float dpiScale = 1f;

        private int Sc(int px)
        {
            return (int)(px * dpiScale + 0.5f);
        }

        /// <summary>
        /// **实测**某个字体画出某个字之后的墨迹上下边界（相对行框顶）。
        ///
        /// 为什么不靠字体度量算基线：这个项目的数字字体是 `Ui.GetTimeFont(px)` 造的，
        /// 它按**像素**单位构造，而 `Font.SizeInPoints` 会按 72/96 换算一次 ——
        /// 两条路径的 em 大小不一致，算出来的 ascent 实测差十几像素（我按 ascent 做的
        /// 第一版就是这样：墨迹底边还差 14px）。离屏画一遍再找边界，
        /// 走的是与 Label **完全相同**的 TextRenderer 路径，换字体/换 DPI 都不会跑。
        ///
        /// 只在构造期调用两次，位图尺寸 = 单字大小，开销可忽略。
        /// </summary>
        /// <summary>
        /// 把「秒」整体平移到与数字**墨迹底边**同一条线上。
        ///
        /// 只在第一次显示时跑一次。做法是把窗体贴成位图（与屏幕渲染走同一套 GDI 文本），
        /// 在两个 Label 各自的矩形内找最下面一行亮像素，差值就是需要平移的量。
        /// 为什么不用字体度量算：见构造里那段注释——算出来的会差十几像素。
        /// </summary>
        private void AlignUnitToCountBaseline()
        {
            try
            {
                if (lblCount == null || lblCountUnit == null) return;

                int w = this.ClientSize.Width;
                int h = this.ClientSize.Height;
                if (w <= 0 || h <= 0) return;

                using (Bitmap bmp = new Bitmap(w, h))
                {
                    this.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));

                    int numBottom = InkBottomIn(bmp, lblCount.Bounds);
                    int unitBottom = InkBottomIn(bmp, lblCountUnit.Bounds);
                    if (numBottom <= 0 || unitBottom <= 0) return;

                    int dy = numBottom - unitBottom;
                    if (dy != 0) lblCountUnit.Top += dy;
                }
            }
            catch { }
        }

        /// <summary>在给定矩形内找最下面一行"比背景亮"的像素；找不到返回 -1。</summary>
        private static int InkBottomIn(Bitmap bmp, Rectangle r)
        {
            int x0 = Math.Max(0, r.Left);
            int x1 = Math.Min(bmp.Width, r.Right);
            int y0 = Math.Max(0, r.Top);
            int y1 = Math.Min(bmp.Height, r.Bottom);

            for (int y = y1 - 1; y >= y0; y--)
            {
                for (int x = x0; x < x1; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    // 文字是浅色、卡片底是深色：用亮度阈值把两者分开
                    if ((c.R + c.G + c.B) > 300) return y;
                }
            }
            return -1;
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
            // 高度必须容得下 Sc(38) 的字号：150% 下该字号行高约 89px，
            // 而原来只给了 Sc(44)（66px）—— 数字下半截就是这么被裁掉的。
            lblCount.SetBounds(w - Sc(96), -Sc(2), Sc(56), Sc(72));
            this.Controls.Add(lblCount);

            lblCountUnit = new Label();
            lblCountUnit.Text = "秒";
            lblCountUnit.ForeColor = Ui.RingLabel;
            lblCountUnit.Font = Ui.FontTip;
            lblCountUnit.TextAlign = ContentAlignment.MiddleLeft;
            lblCountUnit.AutoSize = false;
            lblCountUnit.BackColor = Color.Transparent;
            lblCountUnit.SetBounds(w - Sc(38), Sc(40), Sc(24), Sc(20));

            // ---- 让「秒」与数字的**墨迹底边**对齐（在窗体第一次显示后实测校正）----
            //
            // 为什么不在构造期算：两个 Label 各自垂直居中，但"居中的是行框、不是字形"，
            // 而不同字号的行内留白（ascent/descent/leading）比例完全不同。
            // 我试过两种"按度量算"的写法，实测都差 11~14px：
            //   · TextRenderer.MeasureText("0", font).Height → 拿到的是行框高，不是墨迹；
            //   · 用 FontFamily.GetCellAscent/GetEmHeight 算基线 → 数字字体是
            //     `Ui.GetTimeFont(px)` 按**像素**单位构造的，SizeInPoints 会再按 72/96 换算，
            //     两条路径的 em 大小不一致，算出来的基线偏。
            // 换 DPI 或换字体还会再错一次 —— 所以改成"画出来再量"：
            // 首次显示时把窗体贴到离屏位图，量两块区域的墨迹底边，把单位框平移过去。
            // 这是自校准：任何字体、任何 DPI 都成立，没有写死的像素。
            this.Controls.Add(lblCountUnit);

            // 延迟到第一次显示再校正：构造函数里控件还没真正画过，量不到墨迹。
            this.Shown += delegate { AlignUnitToCountBaseline(); };


            // ---- 说明文案 ----
            lblBody = new Label();
            lblBody.ForeColor = Ui.HintText;
            lblBody.Font = Ui.FontHint;
            lblBody.TextAlign = ContentAlignment.MiddleLeft;
            lblBody.AutoSize = false;
            lblBody.BackColor = Color.Transparent;
            // 高度必须容得下 20pt 的行高：Ui.FontHint = 微软雅黑 20pt，96dpi 下行高约 32px，
            // 而这里原来只给了 Sc(30) —— 下边缘必然被裁（用户反馈"左侧文字没显示全"）。
            // 现在给 Sc(36)：y 同时从 44 上移到 42（42+36=78，按钮从 92 才开始，不会撞上）；
            // 留出的 4px 是给 DPI 缩放后行高比略变大时的余量。别再改回 30。
            lblBody.SetBounds(Sc(20), Sc(42), w - Sc(120), Sc(36));
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

        /// <summary>
        /// E1：今日配额用完时把「跳过」置灰。
        /// 「延后」「立即休息」不受影响 —— 配额只约束"这次不休息"这一个动作。
        /// 横幅只有 84px 宽，「跳过（今日已用完）」放不下，所以按实测宽度降级成「已用完」。
        /// </summary>
        public void SetSkipEnabled(bool on)
        {
            if (btnSkip == null) return;
            if (skipEnabled == on) return;
            skipEnabled = on;

            btnSkip.Enabled = on;

            string text = "跳过";
            if (!on)
            {
                string full = "跳过（今日已用完）";
                int avail = btnSkip.Width - Sc(12);
                text = TextRenderer.MeasureText(full, btnSkip.Font).Width <= avail ? full : "已用完";
            }
            btnSkip.Text = text;
            btnSkip.Invalidate();
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
            // 光带厚度要过 DPI 缩放，否则 150% 下边缘细得像头发丝
            dpiScale = Ui.DetectScale(this);

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
            lastAlpha = alpha;
            try
            {
                if (!this.IsHandleCreated) return;   // 没句柄就先不动，等 Show 之后再设
                this.Opacity = alpha;
            }
            catch { }
        }

        // ---- 身份标识：四周光带 + 右下角小字 ----
        //
        // 用户原话：「这个屏幕变暗最好也要有个四周光带，要不我不知道是屏幕出问题、
        // 屏幕自动调节亮度还是咱们给的提示」。纯粹压黑确实会被误读成硬件/系统行为，
        // 所以必须给这块暗层一个**属于本软件**的视觉签名。
        //
        // 为什么光带能跟着变淡是**对的**：整窗走 Opacity，暗得越深光带越清晰，
        // 恰好形成"轻微暗 → 只是有点暖边；接近到点 → 边缘明显发光"的渐进暗示。
        //
        // 静态图案，只在首次绘制时画一次：不缓存位图、不建 Timer、不重绘路径。
        private const int GlowBandDefault = 36;     // 96dpi 下的光带厚度（与 A1 同步翻倍）

        private float dpiScale = 1f;
        private double lastAlpha = 0.0;             // 当前档位（决定要不要画右下角小字）

        private int Sc(int px)
        {
            return (int)(px * dpiScale + 0.5f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);        // 先让默认绘制把 BackColor（纯黑）铺满

            Graphics g = e.Graphics;
            Size s = this.ClientSize;
            if (s.Width <= 8 || s.Height <= 8) return;

            int band = Sc(GlowBandDefault);
            if (band < 2) band = 2;

            try { g.SmoothingMode = SmoothingMode.None; } catch { }

            // 四条边各一支渐变笔，全部现场创建 + using 释放：
            // 外侧不透明、向内侧渐隐到全透明 —— 看起来是"边缘透光"，不是画了个框。
            // 四角靠上下两条带与左右两条带的重叠自动补齐，不会留缺口。
            DrawEdge(g, new Rectangle(0, 0, s.Width, band), LinearGradientMode.Vertical, false);
            DrawEdge(g, new Rectangle(0, s.Height - band, s.Width, band), LinearGradientMode.Vertical, true);
            DrawEdge(g, new Rectangle(0, 0, band, s.Height), LinearGradientMode.Horizontal, false);
            DrawEdge(g, new Rectangle(s.Width - band, 0, band, s.Height), LinearGradientMode.Horizontal, true);

            // 右下角品牌小字：档位太低时不画（刚起步那几百毫秒 alpha 只有 0.7%，
            // 画了也是一闪而过的鬼影，反而像故障）。
            if (lastAlpha >= 0.10)
            {
                DrawWatermark(g, s);
            }
        }

        /// <summary>画一条"从屏幕边缘向内淡出"的光带。reverse=true 表示亮端在远端（下/右）。</summary>
        private void DrawEdge(Graphics g, Rectangle r, LinearGradientMode mode, bool reverse)
        {
            if (r.Width <= 0 || r.Height <= 0) return;

            // 与 A1 共用同一套边缘发光观感（三档 blend，不是两档硬渐变）
            Ui.DrawFadeBand(g, r, Ui.Accent, mode, reverse);
        }

        /// <summary>
        /// 右下角的「护眼小助手」。用 DrawString + 半透明画刷（GDI 的 TextRenderer 不吃 alpha，
        /// 这里的"透明度较低"只能用 GDI+ 文本才画得出来）。
        /// 字体复用现成的 Ui.FontEyebrow，不新建 Font。
        /// </summary>
        private void DrawWatermark(Graphics g, Size s)
        {
            try
            {
                // 半透明文字必须用 GDI+ 渲染；ClearType 在透明表面上会产生彩边，用 AntiAliasGridFit
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                string text = "护眼小助手";
                float w = g.MeasureString(text, Ui.FontEyebrow).Width;

                float x = s.Width - w - Sc(14);
                float y = s.Height - Ui.FontEyebrow.GetHeight(g) - Sc(10);
                if (x < 0) x = 0;
                if (y < 0) y = 0;

                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, Ui.Accent)))
                {
                    g.DrawString(text, Ui.FontEyebrow, b, x, y);
                }
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

        // ---- G12 语气档：两个互斥勾选框（都不勾 = 温和）----
        private CheckBox chkTonePlain, chkToneSavage;
        private bool suppressTone;      // 互斥逻辑里改 Checked 会再次触发事件，用它挡掉递归

        // ---- A2「预览渐暗」：3 秒淡入 + 1 秒保持 + 1 秒淡出，用完立刻 Dispose ----
        // 只为这几秒创建 DimForm，绝不常驻；关掉对话框（Dispose）也会一起收干净。
        private System.Windows.Forms.Timer dimPreviewTimer;
        private List<DimForm> dimPreviewForms;
        private int dimPreviewStep;
        private float dimPreviewMax;            // 本次预览的目标 alpha（0~1）

        // ---- 「扩展」卡：插件开关 ----
        private AppConfig cfgRef;               // 勾选要立刻 SetOn + Save，所以得留住配置引用
        private bool suppressPluginToggle;      // 回滚时改 Checked 会再次触发事件，用它挡掉递归

        // 版式常量（以下全部是 96dpi 下的设计值，运行时一律经 Sc() 乘 DPI 因子）
        // 设计基准客户区 520 x 1356：
        //   88(页头) + 232(节奏，4 行 + 预览按钮 + 渐暗代价小字) + 16 + 144(番茄钟) + 16
        //   + 144(久坐与活动) + 16 + 336(行为，9 行) + 16 + 268(扩展，6 个插件)
        //   + 24 + 38(按钮) + 18(底部留白) = 1356
        //
        // 这个数字只是"内容的自然高度"，不是硬指标：窗口放不下时由 AutoScroll 兜住，
        // 所以再加行只要同步改这里 + 末尾的 AutoScrollMinSize，不会出现"按钮被切掉"。
        // （「扩展」卡恒定显示 —— 它是插件的唯一开关入口，藏起来就等于没有开关。）
        private const int FormW = 520;
        private const int FormH = 1356;
        private const int CardLeft = 24;
        private const int CardWidth = 472;      // 520 - 24 * 2
        private const int CardPad = 22;         // 卡片左右内边距
        private const int CardTitleTop = 14;
        private const int FirstRowTop = 38;
        private const int RowHeight = 32;       // 所有行（复选 / 数值）统一 32
        private const int CheckHeight = 26;
        private const int NumWidth = 96;
        private const int NumHeight = 24;
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

            // 插件开关要"勾上就生效"，所以留住配置引用（ApplyTo 只在点保存时才跑）
            this.cfgRef = cfg;

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

            // ---------- 卡片 1：节奏（4 行 + 预览按钮 + A2 代价小字）----------
            // 卡片高度：FirstRowTop(38) + 4 行 * 32 + 按钮行 32 + 间隙 4 + 小字 20 + 底部留白 10 ≈ 232
            // 间隙 4 不能省：按钮 h=32 底边落在 198，小字顶边必须在 202，
            // 否则两者贴死（v1.6.1 首版就是忘了把按钮加高的 2px 补回来）。
            int y = Sc(88);
            RoundPanel card1 = AddCard(y, Sc(232), "节奏");
            numWork = AddRow(card1, Sc(FirstRowTop), "工作时长（分钟）", 1, 240, cfg.WorkMinutes);
            numRest = AddRow(card1, Sc(FirstRowTop) + Sc(RowHeight), "休息时长（秒）", 5, 1800, cfg.RestSeconds);
            // 预告窗口（P0-B）：0 = 不预告，直接到点全屏遮罩。
            // 放在「节奏」卡而不是「行为」卡，因为它改的是提醒**时机**而不是行为约束。
            numPreview = AddRow(card1, Sc(FirstRowTop) + Sc(RowHeight) * 2, "提前预告（秒，0=不预告）", 0, 300, cfg.PreviewSeconds);
            // A2 渐进降饱和：0 = 关闭（一个窗口都不创建），上限 70 见 AppConfig.DimAlphaMax 的注释。
            numDim = AddRow(card1, Sc(FirstRowTop) + Sc(RowHeight) * 3,
                            "休息前渐暗强度（%，0=关闭）", 0, 70, cfg.DimAlphaMax);

            // 现场感受强度：3 秒淡入 → 保持 1 秒 → 1 秒淡出。
            // 光看一个百分数没人能判断合不合适，必须让人当场看一遍。
            //
            // 配色（v1.6.1）：原来用 CardBg(#FFFFFF) 底 + SecondaryBorder 描边，
            // 与右侧输入框（InputBg #F7F9FC + InputBorder）几乎同色，
            // 用户分不清"这是按钮还是输入框"。改成 SecondaryHover 底 + SecondaryBorder 描边，
            // 比输入框深一档，一眼能看出是可点的。
            RoundButton btnDimPreview = new RoundButton("预览渐暗", Sc(8),
                                                       Ui.SecondaryHover, Ui.SecondaryBorder,
                                                       Ui.LabelText, Ui.SecondaryBorder, Math.Max(1, Sc(1)));
            btnDimPreview.Font = Ui.FontRowLabel;
            btnDimPreview.SetBounds(Sc(CardPad), Sc(FirstRowTop) + Sc(RowHeight) * 4, Sc(136), Sc(32));
            // 强度为 0 时这个按钮点了什么都不会发生，看起来像坏了 —— 直接禁用，
            // RoundButton.OnPaint 会把 disabled 的文字画灰，用户立刻明白"先调强度"。
            btnDimPreview.Enabled = cfg.DimAlphaMax > 0;
            btnDimPreview.Click += delegate { StartDimPreview(); };
            card1.Controls.Add(btnDimPreview);

            // 强度一改，按钮可用性立刻跟着变（不用等保存）
            numDim.ValueChanged += delegate { btnDimPreview.Enabled = numDim.Value > 0; };

            // A2 的代价必须写在开关旁边，而不是只写在 README 里：
            // 用户主动把强度从 0 调大，就等于选择了"用时间准确性换一点视觉提示"。
            Label lblDimNote = new Label();
            lblDimNote.Text = "开启后到点提醒可能晚 1~3 秒（默认关闭，时间准确性优先）";
            lblDimNote.Font = Ui.FontEyebrow;
            lblDimNote.ForeColor = Ui.CaptionText;
            lblDimNote.BackColor = Ui.CardBg;
            lblDimNote.AutoSize = false;
            lblDimNote.TextAlign = ContentAlignment.MiddleLeft;
            lblDimNote.SetBounds(Sc(CardPad), Sc(FirstRowTop) + Sc(RowHeight) * 4 + Sc(36),
                                 Sc(CardWidth) - Sc(CardPad) * 2, Sc(20));
            card1.Controls.Add(lblDimNote);

            y += Sc(232) + Sc(Gap);

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

            // ---------- 卡片 4：行为（9 行）----------
            RoundPanel card3 = AddCard(y, Sc(336), "行为");
            chkAllowSkip = AddCheck(card3, Sc(FirstRowTop), "允许跳过 / 推迟休息", cfg.AllowSkip);
            numPostpone = AddRow(card3, Sc(FirstRowTop) + Sc(RowHeight), "推迟时长（分钟）", 1, 60, cfg.PostponeMinutes);
            numIdle = AddRow(card3, Sc(FirstRowTop) + Sc(RowHeight) * 2, "离开多少分钟后暂停计时", 0, 120, cfg.IdleMinutes);
            chkSound = AddCheck(card3, Sc(FirstRowTop) + Sc(RowHeight) * 3, "开始 / 结束都播放提示音", cfg.SoundEnabled);
            // 文案长度受行宽硬约束：FlatCheckBox 行宽 Sc(472)-Sc(44)=428，文字起点 26px，
            // 可用 402px。太长的句子会被 EndEllipsis 切掉（v1.6.0 就是被切了），
            // 所以这里用短句，但"会中断输入"这个后果提示必须保留。
            chkStrict = AddCheck(card3, Sc(FirstRowTop) + Sc(RowHeight) * 4,
                                 "严格模式：锁键盘，Esc/空格退出（会中断输入）", cfg.StrictMode);
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
            // G12：放在最后一行 —— 语气只影响遮罩上的几句话，不动任何行为，
            // 所以不插在「跳过 / 推迟 / 严格模式」这些真正改变后果的开关中间。
            AddToneRow(card3, Sc(FirstRowTop) + Sc(RowHeight) * 8);

            // ---------- 卡片 5：扩展（插件开关）----------
            // 这张卡是**插件唯一的开关入口**，所以恒定显示（不能按"有没有插件开"来决定显隐，
            // 否则第一次想开的时候根本找不到开关）。
            // 已有四张卡的位置/尺寸一个像素都没动，只是本卡插在它们下面 ——
            // 保存按钮因此下移 Sc(284)，这是新增功能必然要付的版面代价（AutoScroll 兜住小屏）。
            int extTop = y + Sc(336) + Sc(Gap);
            RoundPanel cardExt = AddCard(extTop, Sc(268), "扩展");

            // 顺序按内存代价从小到大（Stats 几乎零成本 → Guard 只有一个计数器 → 窗口类插件在后）。
            // 右侧占用标注是 tools/plugin-mem-check.py 的实测值（同配置开/关对照）。
            // 只有"暂未实测"的项才写「待测」——不写估算。
            AddPluginRow(cardExt, Sc(FirstRowTop), "Stats", "用眼统计", cfg.Plugin_Stats, "<0.5 MB");
            AddPluginRow(cardExt, Sc(FirstRowTop) + Sc(RowHeight) * 1, "EdgeGlow", "屏幕边缘微光", cfg.Plugin_EdgeGlow, "约 0.5 MB");
            AddPluginRow(cardExt, Sc(FirstRowTop) + Sc(RowHeight) * 2, "Ritual", "休息仪式（眼操/呼吸）", cfg.Plugin_Ritual, "约 2 MB");
            AddPluginRow(cardExt, Sc(FirstRowTop) + Sc(RowHeight) * 3, "Sound", "仪式音", cfg.Plugin_Sound, "约 0.7 MB");
            AddPluginRow(cardExt, Sc(FirstRowTop) + Sc(RowHeight) * 4, "Movement", "起身活动演示", cfg.Plugin_Movement, "约 2.3 MB");
            AddPluginRow(cardExt, Sc(FirstRowTop) + Sc(RowHeight) * 5, "Guard", "跳过配额", cfg.Plugin_Guard, "<0.5 MB");

            // 卡底小字：必须写明"关掉会回落"，否则用户不敢试。
            Label lblExtNote = new Label();
            lblExtNote.Text = "插件默认全部关闭。开启会占用额外内存，关闭后内存会回落。";
            lblExtNote.Font = Ui.FontEyebrow;
            lblExtNote.ForeColor = Ui.CaptionText;
            lblExtNote.BackColor = Ui.CardBg;
            lblExtNote.AutoSize = false;
            lblExtNote.TextAlign = ContentAlignment.MiddleLeft;
            lblExtNote.SetBounds(Sc(CardPad), Sc(FirstRowTop) + Sc(RowHeight) * 6,
                                 Sc(CardWidth) - Sc(CardPad) * 2, Sc(24));
            cardExt.Controls.Add(lblExtNote);

            int cursorTop = extTop + Sc(268);

            // ---------- 卡片 6：统计（只在 Plugin_Stats 启用时出现）----------
            if (cfg.Plugin_Stats)
            {
                int statTop = cursorTop + Sc(Gap);
                int statH = Sc(168) + Sc(RowHeight);
                RoundPanel cardStat = AddCard(statTop, statH, "统计");

                AddInfoRow(cardStat, Sc(FirstRowTop), "完成 " + cfg.StatDone + " 次", Ui.LabelText);
                AddInfoRow(cardStat, Sc(FirstRowTop) + Sc(RowHeight) * 1, "跳过 " + cfg.StatSkip + " 次", Ui.LabelText);
                AddInfoRow(cardStat, Sc(FirstRowTop) + Sc(RowHeight) * 2, "忽略 " + cfg.StatIgnore + " 次", Ui.LabelText);
                // 最近 7 天趋势：只画"完成"，因为三种状态挤一张图谁都读不出来。
                AddInfoRow(cardStat, Sc(FirstRowTop) + Sc(RowHeight) * 3,
                           "最近 7 天完成：" + StatsPlugin.Sparkline(cfg)
                           + "（左旧右新）", Ui.LabelText);
                // 这句不是免责声明套话：三态计数非常容易被读成"用眼健康评分"，
                // 必须明确这不是健康判断，否则就是在制造焦虑。
                AddInfoRow(cardStat, Sc(FirstRowTop) + Sc(RowHeight) * 4,
                           "这不是健康诊断，只记录发生过的次数", Ui.CaptionText);

                cursorTop = statTop + statH;
            }

            // ---------- 底部按钮：最后一排卡片底部 + Sc(24) ----------
            int btnY = cursorTop + Sc(24);

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

            // 「关于 / 致谢」：CC BY-SA 4.0 要求署名对**终端用户可见**，
            // 只写在 README 里不算。放在保存/取消左边的空白区，不改变既有版式。
            RoundButton about = new RoundButton("关于 / 致谢", Sc(8), Ui.CardBg, Ui.SecondaryHover,
                                                Ui.LabelText, Ui.SecondaryBorder, Math.Max(1, Sc(1)));
            about.Font = Ui.FontRowLabel;
            about.SetBounds(Sc(24), btnY, Sc(112), Sc(38));
            about.Click += delegate { ShowAbout(); };
            this.Controls.Add(about);

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
                ? "严格模式：锁键盘，Esc/空格退出（会中断输入）"
                : "严格模式：只拦系统键（键盘仍可用）";
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
            // ⚠ 坐标基准的坑（v1.6.1 修的就是这里）：子控件的坐标是**相对父容器**的，
            // 不是卡片坐标系。之前 n 用卡片坐标（numRight - boxW + Sc(6)）定位到 box 里，
            // 而 box 只有 NumWidth + Sc(12) 宽 —— 输入框整个落在容器外，被 RoundPanel
            // 的 Region 圆角裁掉，于是表现为"浅浅一个空白圆角矩形，看不到数字、
            // 点不进去、也无法聚焦"。n 的 x 必须从 box 的左内边距重新起算。
            //
            // 容器右缘 = 原来数字框的右缘（右对齐位置一点没动），数字框作为子控件内嵌：
            //   水平各留 Sc(6)（容器宽 = NumWidth + Sc(12)）
            //   垂直：容器比行距矮 Sc(4)、控件再矮 Sc(4)，上下各留 2px ——
            //   容器取满行距会让相邻行的边框贴死（真机截图里就是一根柱子）
            int numRight = Sc(CardWidth) - Sc(CardPad) - 1;
            int boxW = Sc(NumWidth) + Sc(12);
            // 容器比行距矮 4（上下各留 2），否则相邻两行的输入框边框会完全贴合，
            // 四个框连成一根柱子（v1.6.1 第二轮真机截图发现的）。
            int boxH = Sc(RowHeight) - Sc(4);

            RoundPanel box = new RoundPanel(Sc(10), Ui.InputBg, Ui.InputBorder);
            box.SetBounds(numRight - boxW, top + Sc(2), boxW, boxH);
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
            // x 只有左内边距 Sc(6)；y 只在容器内居中，**不再加 top**（父容器已带 top）
            n.SetBounds(Sc(6), (boxH - numH) / 2, Sc(NumWidth), numH);

            // 焦点反馈：输入框没有自己的边框，只能用容器的描边色表达"这个框被选中了"。
            // 用 PrimaryBg(#1F2937) 而不是 Accent(#5FD3A6)：薄荷绿在白卡片上对比太低，
            // 深灰与主按钮同色，一眼看得出焦点在哪。
            n.Enter += delegate { box.SetBorderColor(Ui.PrimaryBg); };
            n.Leave += delegate { box.SetBorderColor(Ui.InputBorder); };

            // 点容器留白（数字左右那 Sc(6)）也该能聚焦 —— 否则用户点"输入框"边缘没反应，
            // 会以为整个框是坏的。顺带全选，直接打字即覆盖旧值。
            box.MouseDown += delegate
            {
                n.Focus();
                n.Select(0, n.Text.Length);
            };

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
        /// G12 语气档的一行：左边说明，右边两个互斥的勾选框。
        ///
        /// 为什么是**两个**框而不是三个（或一个下拉）：三档里「温和」是默认值，
        /// 默认值不需要一个能点的控件去表达 ——「两个都不勾」就是它。
        /// 这样只需要维护一条不变式（两个框不能同时勾），而且用的还是现成的 FlatCheckBox，
        /// 不必为一行设置项引入自带系统主题边框的 ComboBox（整页剩下的控件都是自绘的）。
        /// </summary>
        private void AddToneRow(RoundPanel card, int top)
        {
            Label l = new Label();
            l.Text = "提醒语气（默认温和）";
            l.Font = Ui.FontRowLabel;
            l.ForeColor = Ui.LabelText;
            l.BackColor = Ui.CardBg;
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            // 164 而不是 152：「提醒语气（默认温和）」约 10 个汉字 ≈140px，
            // 152 减去行内左边距后贴着第一个勾选框，文字被挤成换行/截断。
            l.SetBounds(Sc(CardPad), top + Sc(4), Sc(164), Sc(24));
            card.Controls.Add(l);

            // 两个勾选框整体右移 Sc(12) 让位：264 + 92 = 356 < 可用 428，仍有富余
            chkTonePlain = MakeToneBox(card, Sc(CardPad) + Sc(168), top,
                                       "冷淡", cfgRef.Tone == 1);
            chkToneSavage = MakeToneBox(card, Sc(CardPad) + Sc(264), top,
                                        "毒舌", cfgRef.Tone == 2);

            // 互斥：勾一个就把另一个取消。suppressTone 挡住递归
            // （取消另一个会触发它自己的 CheckedChanged）。
            chkTonePlain.CheckedChanged += delegate
            {
                if (suppressTone) return;
                suppressTone = true;
                if (chkTonePlain.Checked) chkToneSavage.Checked = false;
                suppressTone = false;
            };
            chkToneSavage.CheckedChanged += delegate
            {
                if (suppressTone) return;
                suppressTone = true;
                if (chkToneSavage.Checked) chkTonePlain.Checked = false;
                suppressTone = false;
            };
        }

        private CheckBox MakeToneBox(RoundPanel card, int left, int top, string text, bool val)
        {
            CheckBox c = new FlatCheckBox();
            c.Text = text;
            c.Checked = val;
            c.Font = Ui.FontRowLabel;
            c.ForeColor = Ui.LabelText;
            c.BackColor = Ui.CardBg;
            c.AutoSize = false;
            c.SetBounds(left, top + Sc(3), Sc(92), Sc(CheckHeight));
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

        /// <summary>
        /// 「扩展」卡的一行：左边勾选框 + 名称，右边小字占用标注（实测值）。
        /// 勾选**立即生效**（不等保存），失败会回滚 —— 见 WirePluginToggle。
        /// </summary>
        private void AddPluginRow(RoundPanel card, int top, string id, string name, bool val, string noteText)
        {
            CheckBox box = AddCheck(card, top, name, val);

            // 勾选框收窄，给右侧标注让位（AddCheck 默认占满整行）
            // 76 而不是 56：56 装不下「约 0.5 MB」，会被 MiddleRight + EndEllipsis 截成「约 0.5」。
            int noteW = Sc(76);
            box.SetBounds(box.Left, box.Top,
                          Sc(CardWidth) - Sc(CardPad) * 2 - noteW - Sc(8), box.Height);

            Label note = new Label();
            // 占用来由 tools/plugin-mem-check.py 实测（同配置开/关对照，窗体成本已抵消）。
            // 纪律：这里只允许写实测值；没有实测数据时传「待测」，绝不写估算。
            note.Text = noteText;
            note.Font = Ui.FontEyebrow;
            note.ForeColor = Ui.CaptionText;
            note.BackColor = Ui.CardBg;
            note.AutoSize = false;
            note.TextAlign = ContentAlignment.MiddleRight;
            note.SetBounds(Sc(CardWidth) - Sc(CardPad) - noteW, top + Sc(6), noteW, Sc(20));
            card.Controls.Add(note);

            WirePluginToggle(box, id);
        }

        /// <summary>
        /// 插件开关的联动：`SetOn` 写配置 + 启停插件，然后立刻落盘。
        ///
        /// **必须带异常回滚**：`OnEnable()` 里可能建窗/建定时器失败（例如 EdgeGlow 建窗失败）。
        /// 不回滚的话界面会停在"勾上了但没生效"，这比没勾更糟 ——
        /// 用户会以为功能开着，实际什么都没发生，而且下次打开设置还是勾着的。
        /// </summary>
        private void WirePluginToggle(CheckBox box, string id)
        {
            string pluginId = id;
            box.CheckedChanged += delegate
            {
                if (suppressPluginToggle) return;   // 回滚里改 Checked 会再次进来

                bool want = box.Checked;
                try
                {
                    PluginHost.SetOn(pluginId, want, cfgRef);

                    // 开启时立刻强制实例化一次：OnEnable 里的失败必须在这里暴露，
                    // 不能拖到"下次该弹窗时才发现没生效"。
                    if (want && PluginHost.Get(pluginId) == null)
                        throw new InvalidOperationException("plugin init failed");

                    if (cfgRef != null) cfgRef.Save();
                }
                catch
                {
                    suppressPluginToggle = true;
                    try { box.Checked = !want; } catch { }
                    suppressPluginToggle = false;

                    try
                    {
                        MessageBox.Show("该插件启动失败，已回滚。", "护眼小助手",
                                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    catch { }
                }
            };
        }

        /// <summary>
        /// 「关于 / 致谢」：展示 assets/ATTRIBUTION.md（构建时作为嵌入资源打进 exe）。
        ///
        /// 为什么必须做在**界面上**：CC BY-SA 4.0 要求署名对终端用户可见，
        /// 只写在 README / 仓库里不算履行署名义务。
        /// </summary>
        private void ShowAbout()
        {
            string text = LoadAttribution();
            try
            {
                Form f = new Form();
                try
                {
                    f.Text = "关于 / 致谢";
                    f.StartPosition = FormStartPosition.CenterParent;
                    f.FormBorderStyle = FormBorderStyle.FixedDialog;
                    f.MinimizeBox = false;
                    f.MaximizeBox = false;
                    f.ShowInTaskbar = false;
                    f.BackColor = Ui.PageBg;
                    f.Font = Ui.FontRowLabel;
                    f.ClientSize = new Size(Sc(560), Sc(600));

                    TextBox box = new TextBox();
                    box.Multiline = true;
                    box.ReadOnly = true;
                    box.ScrollBars = ScrollBars.Vertical;
                    box.WordWrap = true;
                    box.BorderStyle = BorderStyle.None;
                    box.BackColor = Ui.CardBg;
                    box.ForeColor = Ui.LabelText;
                    box.Font = Ui.FontRowLabel;
                    box.Text = text;
                    box.SetBounds(Sc(16), Sc(16), Sc(528), Sc(516));
                    f.Controls.Add(box);

                    RoundButton close = new RoundButton("关闭", Sc(8), Ui.PrimaryBg, Ui.PrimaryHover,
                                                        Color.White, Color.Empty, 0);
                    close.Font = Ui.FontRowLabel;
                    close.SetBounds(Sc(560) - Sc(16) - Sc(96), Sc(600) - Sc(16) - Sc(38), Sc(96), Sc(38));
                    close.DialogResult = DialogResult.OK;
                    f.Controls.Add(close);
                    f.AcceptButton = close;
                    f.CancelButton = close;

                    f.ShowDialog(this);
                }
                finally
                {
                    try { f.Dispose(); } catch { }
                }
            }
            catch { }
        }

        /// <summary>读嵌入的 ATTRIBUTION.md；读不到就退回到内置的要点版（署名义务不能因为资源缺失而消失）。</summary>
        private static string LoadAttribution()
        {
            try
            {
                System.IO.Stream st = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("EyeLite.res.ATTRIBUTION.md");
                if (st != null)
                {
                    using (st)
                    using (StreamReader r = new StreamReader(st, Encoding.UTF8))
                    {
                        string s = r.ReadToEnd();
                        if (s != null && s.Trim().Length > 0) return s;
                    }
                }
            }
            catch { }
            return FallbackAttribution;
        }

        private const string FallbackAttribution =
            "动作演示图片素材署名\r\n" +
            "\r\n" +
            "1) bryllim/workout-guide —— CC BY-SA 4.0\r\n" +
            "   作者：Bryl Lim（https://bryllim.com）\r\n" +
            "   来源：https://github.com/bryllim/workout-guide\r\n" +
            "   许可：https://creativecommons.org/licenses/by-sa/4.0/\r\n" +
            "   改动：无（图片原样嵌入，未裁剪/缩放/改色/重压缩）\r\n" +
            "\r\n" +
            "2) yuhonas/free-exercise-db —— Unlicense（公有领域）\r\n" +
            "   来源：https://github.com/yuhonas/free-exercise-db\r\n" +
            "   许可：https://unlicense.org/\r\n" +
            "\r\n" +
            "若将来修改 CC BY-SA 4.0 的图片，修改后的图片也须以同一许可发布。";

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
            // G12：两个框都不勾 = 温和（0）。互斥由 AddToneRow 保证，这里不需要再判冲突。
            cfg.Tone = chkToneSavage.Checked ? 2 : (chkTonePlain.Checked ? 1 : 0);
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
        private ToolStripMenuItem miImmersive;
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
        private string previewLastBody = "";   // 上次推给横幅的文案（到点让路时数字不变、文案要变）
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
        private bool inMeeting = false;        // 当前是否检测到"在会中"（仅用于托盘文案）

        // 预告窗口的绝对到期时刻（0 = 当前没有预告）。
        // 显示用它反算剩余秒数，避免 tick 抖动导致数字跳变。
        private long previewDueMs = 0;

        // 200ms 的 UI 刷新：只在"休息中 / 预告中"干活。
        // 为什么需要它：1 秒一次的 tick 一旦被系统合并，倒计时会一次掉 2 秒；
        // 有了它，数字每 200ms 从绝对时刻重算，即使 tick 迟到也不会跳。
        private System.Windows.Forms.Timer fastUi;

        // 测试设施：见 ctor 尾部说明
        private static readonly string FakeRoutine = ReadFakeRoutine();
        private static string ReadFakeRoutine()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("EYE_LITE_FAKE_ROUTINE");
                return v == null ? "" : v.Trim();
            }
            catch { return ""; }
        }

        // 测试设施：EYE_LITE_FAKE_ACTION=<动作名>（如 扩胸 / 起身伸展）时，启动即**只播这一个动作**。
        //
        // 为什么需要它：EYE_LITE_FAKE_ROUTINE 只认例程名（颈/肩/腰/腕），
        // 要验「扩胸」的对称性就得"在肩例程里等到第 22 秒" —— 动作顺序或时长一调整，
        // 验证立刻失效。单独播一个动作，验证点就与时序解耦了。
        //
        // 与 FakeRoutine 同时设置时**以 ROUTINE 为准**（见 ctor 尾部：先判 Routine，再 else if Action）。
        // 不设时零影响（空串 → 一次长度判断）。
        private static readonly string FakeAction = ReadFakeAction();

        private static string ReadFakeAction()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("EYE_LITE_FAKE_ACTION");
                return v == null ? "" : v.Trim();
            }
            catch { return ""; }
        }

        // 测试设施：EYE_LITE_TRACE_UI=<文件> 时，把每秒实际显示给用户的剩余秒数追加进去。
        // 为什么需要它：遮罩上的倒计时是**画在环里的**，没有控件文本可读，
        // 外部探针无法验证"有没有跳数"。不设环境变量时只有一次空判断，零 IO。
        private static readonly string TraceUiFile = ReadTraceUi();
        private static string ReadTraceUi()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("EYE_LITE_TRACE_UI");
                return v == null ? "" : v.Trim();
            }
            catch { return ""; }
        }

        // ---- 测试设施：A/B 实验用的两个"闸托盘"开关 ----
        //
        // **这是测试设施，不是功能开关。生产环境禁设；不设时两条路径与现在逐字节一致**
        // （缺省 false → 两个 if 都不成立 → 一次分支判断，零 I/O）。与契约 §10.6 同一口径。
        //
        // 背景：用户报"预告横幅倒数到 2 卡了近 3 秒"。假设是托盘更新把 UI 线程顶住了 ——
        // `tray.Text = ...` / `tray.Icon = ...` 最终都走 Shell_NotifyIcon，
        // 那是对 explorer 的**同步**调用，explorer 忙时会顶住调用方。
        // 一个 tick 里可能连发两次（tooltip + 进度图标），所以闸门要分两档，
        // 只闸一半容易测出"卡顿减半但不消失"，然后被误读成"假设不成立"。
        //
        // 用法：A/B 时分别设 EYE_LITE_NO_TRAY_TEXT=1 / EYE_LITE_NO_TRAY_ICON=1，
        // 用 EYE_LITE_TRACE_UI 的 P 行间距判断 UI 线程有没有被顶住。
        private static readonly bool NoTrayText = ReadEnvFlag("EYE_LITE_NO_TRAY_TEXT");
        private static readonly bool NoTrayIcon = ReadEnvFlag("EYE_LITE_NO_TRAY_ICON");

        /// <summary>环境变量开关：1 / true / yes（不分大小写）= 开；其余/缺失/异常 = 关。</summary>
        private static bool ReadEnvFlag(string name)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(v)) return false;
                v = v.Trim();
                if (v == "1") return true;
                if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return false; }
        }

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

        // ---- E6 锁键盘倒计时 ----
        // 非 0 表示"这一轮休息正在倒计时、钩子还没装"。到点后装钩子并归零。
        private long lockDueMs = 0;

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

        // ---- 测试钩子：到期追踪（p0a 用）----
        //
        // 环境变量 EYE_LITE_TRACE_FILE 指向一个文本文件；设了就把"理论上该弹的时刻"
        // 和"实际开始 BeginRest 的时刻"各记一行。目的只有一个：
        //   **把"计时晚了"和"窗口出现晚了"这两件事分开。**
        // p0a（计时精度）偶发 63.5 / 64.5 / 73.7 秒的离群值，而同机器另一探针连跑 6 次都是
        // 60.6–61.1 秒 —— 没有这个文件就永远分不清是计时器迟到，还是遮罩显示/观测迟到。
        //
        // 未设该环境变量时：只多一次空串判断，**不产生任何 I/O**，行为与之前逐字节等价。
        private static readonly string TraceFile = ReadTraceFile();
        private static readonly Encoding TraceEncoding = new UTF8Encoding(false);   // 不写 BOM，免得首行带脏字符

        private static string ReadTraceFile()
        {
            try
            {
                string s = Environment.GetEnvironmentVariable("EYE_LITE_TRACE_FILE");
                if (string.IsNullOrEmpty(s)) return "";
                return s;
            }
            catch { return ""; }
        }

        /// <summary>
        /// 追加一行到追踪文件。任何失败（路径不存在 / 权限 / 磁盘满）都静默忽略 ——
        /// 这是诊断设施，绝不能反过来影响休息逻辑。
        /// </summary>
        private static void TraceAppend(string line)
        {
            if (TraceFile.Length == 0) return;
            try { File.AppendAllText(TraceFile, line + "\n", TraceEncoding); }
            catch { }
        }

        /// <summary>BEGIN 行：ISO 时间 \t 理论到期时刻 \t 实际开始时刻 \t 迟到毫秒。</summary>
        private static string TraceBeginLine(long dueMs, long fireMs)
        {
            return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")
                 + "\t" + dueMs
                 + "\t" + fireMs
                 + "\t" + (fireMs - dueMs);
        }

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

        // ================================================================
        // 测试钩子：一次性"预置累加器"（EYE_LITE_FAKE_SED_MS / EYE_LITE_FAKE_WORK_MS）
        // ================================================================
        //
        // **这是测试设施，不是功能开关。生产环境禁设；不设则整条路径与现在完全一致**
        // （不设 → 值是 -1 → OnTick 里那一行 if 不成立 → 一个字节都不参与）。
        // 与契约 §10.6「诊断/测试设施不得改变默认行为」同一口径。
        //
        // 为什么需要：久坐间隔被钳在 15~180 分钟、工作周期按分钟计，而 bodyBreak 与
        // 到点路径都必须真跑一遍。没有钩子时一轮验收要 80 分钟，有了它 <10 分钟 ——
        // 我们已经在 D-3（休眠）上吃过"验不动只能做源码审查"的亏。
        //
        // **语义是"一次性消耗"，不是"钉住"**（这条被改过一次，别改回去）：
        //   生效一个 tick 后立刻置回 -1，之后就完全走真实累加。
        //   钉住的话每轮休息结束（ResetWork 清零累加器）后会被立刻重新钉满 →
        //   下一 tick 又到点 → 休息之间零间隔地连弹，测试没法区分"第二轮"和"死循环"。
        //   一次性消耗既能让第一轮立刻到，又保留"下一轮要等真实时长"这个真实语义。
        //
        // 两者互不干扰：sed 的只写 sedAccumMs，work 的只写 workAccumMs。
        // 都不在 ResetWork() 里重新武装 —— 一次性就是一次性。
        // 都不提供 _FILE 版本：这批不需要运行期改，多一个钩子就多一处长期负担。
        // 测试设施：EYE_LITE_OPEN_SETTINGS=1 时启动即打开设置页。
        // 用途：自动化验证「扩展」卡（6 个插件名 + 实测占用 + 原有四张卡没被挤坏）。
        // 本程序没有"二次启动打开设置"的行为，托盘图标在 Win11 又可能被折叠，
        // 这是唯一稳定的自动化入口。**生产环境禁设**；不设时下面一行都不会执行。
        private static readonly bool OpenSettingsOnStart = ReadOpenSettings();

        private static bool ReadOpenSettings()
        {
            try { return Environment.GetEnvironmentVariable("EYE_LITE_OPEN_SETTINGS") == "1"; }
            catch { return false; }
        }

        private static long fakeSedPending = ReadFakeSedMs();
        private static long fakeWorkPending = ReadFakeWorkMs();

        private static long ReadFakeSedMs()
        {
            return ReadFakeAccumMs("EYE_LITE_FAKE_SED_MS");
        }

        private static long ReadFakeWorkMs()
        {
            return ReadFakeAccumMs("EYE_LITE_FAKE_WORK_MS");
        }

        /// <summary>两个钩子共用的解析：未设 / 空 / 非法 / 负数 一律 -1（= 不生效）。</summary>
        private static long ReadFakeAccumMs(string variable)
        {
            try
            {
                string s = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrEmpty(s)) return -1;
                long v;
                if (!long.TryParse(s, out v)) return -1;
                if (v < 0) return -1;
                return v;
            }
            catch { return -1; }
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

            // 沉浸模式：与「暂停」并列，但语义相反 —— 暂停不记账，沉浸记账。
            // 文案必须点明这个区别，否则用户会以为两者一样，然后抱怨"时间没算"。
            miImmersive = new ToolStripMenuItem("沉浸模式（照常计时，不打扰）");
            miImmersive.CheckOnClick = true;
            miImmersive.Checked = cfg.Immersive;
            miImmersive.Click += delegate { SetImmersive(miImmersive.Checked); };
            menu.Items.Add(miImmersive);

            // 托盘里也能开关插件。设置页「扩展」卡要点开设置才能改，而插件是"试一下"的东西，
            // 从托盘勾更快。走同一条 PluginHost.SetOn + cfg.Save 路径，失败回滚（见 TogglePluginFromTray）。
            ToolStripMenuItem miPlugins = new ToolStripMenuItem("扩展");
            for (int pi = 0; pi < PluginHost.All.Count; pi++)
            {
                PluginDescriptor pd = PluginHost.All[pi];
                if (pd.Factory == null) continue;      // 未实现的占位不进菜单
                ToolStripMenuItem item = new ToolStripMenuItem(pd.DisplayName);
                item.CheckOnClick = true;
                item.Checked = PluginHost.IsOn(pd.Id);
                string pluginId = pd.Id;               // 闭包：必须捕获值，不能捕获循环体里的引用
                ToolStripMenuItem self = item;
                item.Click += delegate { TogglePluginFromTray(pluginId, self); };
                miPlugins.DropDownItems.Add(item);
            }
            menu.Items.Add(miPlugins);

            // G10：按痛点的缓解例程。不依赖插件开关 —— 这是一次显式点击，
            // 用户已经表达了"我现在就要缓解"的意图，不该再被配置拦住。
            ToolStripMenuItem miRoutine = new ToolStripMenuItem("缓解一下");
            for (int ri = 0; ri < MoveLib.Routines.Length; ri++)
            {
                string rname = MoveLib.Routines[ri][0];
                ToolStripMenuItem ritem = new ToolStripMenuItem(rname + "部");
                string captured = rname;
                ritem.Click += delegate { ShowRoutine(captured); };
                miRoutine.DropDownItems.Add(ritem);
            }
            menu.Items.Add(miRoutine);

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

            fastUi = new System.Windows.Forms.Timer();
            fastUi.Interval = 200;
            fastUi.Tick += delegate
            {
                // (d)：休息中只刷 UI；不在休息就做"到点反应"的快速快照。
                // 见 FastDueSnapshot() 的注释：这是把"准时"从 1s tick 上解耦的落点。
                try { FastDueSnapshot(); }
                catch { }
            };
            fastUi.Start();

            // 测试设施（见 OpenSettingsOnStart）。用 Post 而不是直接调：
            // 构造期消息循环还没起来，ShowDialog 会把初始化卡住。
            if (OpenSettingsOnStart)
            {
                try { Ui.Post(delegate { ShowSettings(); }); } catch { }
            }

            // 测试设施：EYE_LITE_FAKE_ROUTINE=颈|肩|腰|腕 时，启动即播该例程。
            // 用途：自动化验证"例程能弹窗、能按顺序推进、播完自动收窗"，
            // 否则只能靠人手点托盘菜单。不设时零影响。
            //
            // EYE_LITE_FAKE_ACTION=<动作名> 时只播那一个动作（见 ShowSingleAction）。
            // 两个都设时**以 ROUTINE 为准**：例程覆盖面更大，且不会让已有的例程脚本误触单动作路径。
            if (FakeRoutine.Length > 0)
            {
                try { Ui.Post(delegate { ShowRoutine(FakeRoutine); }); } catch { }
            }
            else if (FakeAction.Length > 0)
            {
                try { Ui.Post(delegate { ShowSingleAction(FakeAction); }); } catch { }
            }
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

                // 测试设施：EYE_LITE_TRACE_FILE 已设时，每个 1s tick 记一行 `T <Clock.NowMs()>`。
                //
                // 用途：**直接数出这一轮丢了几个 tick**。
                //   相邻两行差 ≈1000ms → 没丢；
                //   ≈2000ms / ≈3000ms → 丢了 1 个 / 2 个。
                // 配合已有的 `lateMs`（BeginRest 入口）做交叉判定：
                //   · latems 大 且 T 间隔正常  → 不是丢 tick，是别的原因（例如观测链路）
                //   · latems 大 且 T 出现 2000ms 空档 → 就是丢 tick（WM_TIMER 不补发）
                // 未设环境变量时只有一次长度判断，零 I/O。
                if (TraceFile.Length > 0)
                {
                    try { TraceAppend("T " + now.ToString()); } catch { }
                }

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
                    //
                    // 实现上只需要"丢弃这段 dt"：累加器不加，就等于这段时间没被算成用眼。
                    // 这里原本还有一个 idleGapSeconds 累计（带 24 小时封顶），但全程序
                    // 只写不读 —— 属于"派生状态重复存储"（契约通则第 1 条），已删除。
                    // 保留 24 小时封顶这件事本身也失去意义：dt 被直接丢弃，不存在溢出。
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

                    // ---- E6：倒计时走完才真正锁键盘 ----
                    // 倒计时期间钩子根本没装，所以这段时间用户照常打字、也随时能点「跳过」/「推迟」；
                    // 代价只是"这 N 秒的键盘没被约束"，换来的是一句提前警告。
                    if (lockDueMs != 0)
                    {
                        if (now >= lockDueMs)
                        {
                            lockDueMs = 0;
                            try { SetLockCountdownOnForms(-1); } catch { }
                            try { InstallRestHookNow(); } catch { }
                        }
                        else
                        {
                            int secLeft = (int)((lockDueMs - now + 999) / 1000);
                            try { SetLockCountdownOnForms(secLeft); } catch { }
                        }
                    }

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
                    // 所以小窗必须在**之后**再抬一次，否则它会在 1 秒内被遮罩盖掉。
                    // Movement 与 Ritual 同一时刻只会有一个窗口（Movement 优先），
                    // 这里无条件都抬一下，没有窗口的那个会立刻返回（句柄为 Zero）。
                    try { BringMovementToTop(); } catch { }
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

                    // 测试钩子：两个"一次性预置"都在 due 判定**之前**执行，生效一个 tick 后自动失效。
                    // 未设时两个值都是 -1，这两行 if 都不成立 —— 与之前逐字节一致。
                    // 互不干扰：sed 的只写 sedAccumMs，work 的只写 workAccumMs。
                    if (fakeSedPending >= 0)
                    {
                        sedAccumMs = fakeSedPending;
                        fakeSedPending = -1;
                    }
                    if (fakeWorkPending >= 0)
                    {
                        workAccumMs = fakeWorkPending;
                        fakeWorkPending = -1;
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

                    // C2：会议检测也是一个抑制源，**复用同一套**"抑制 + 30 分钟强制放行"，
                    // 不另造一套状态机 —— 两套抑制逻辑并存是 bug 的温床。
                    inMeeting = false;
                    if (cfg.MeetingGuardEnabled)
                    {
                        try { inMeeting = MeetingDetector.IsInMeeting(cfg); } catch { inMeeting = false; }
                        if (inMeeting) blocked = true;
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
                                    "全屏 / 演示 / 会议持续过久，已停止暂缓提醒。", ToolTipIcon.Info);
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

                    // **连窗口句柄一起预热**。
                    //
                    // EnsureRestForms 只 new 了 Form 对象，真正的 HWND 要等第一次 Show() 才创建，
                    // 而 Show() 正发生在 BeginRest 里 —— 也就是"该弹遮罩的那一刻"。
                    // 这台机器在长时间空闲后，两块全屏 layered 窗口的句柄创建 + 首次合成
                    // 能让提醒整体晚 3~4 秒（实测中位数从 60.6 漂到 63.6）。
                    // 读一次 Handle 就会强制创建 HWND 与全部子控件句柄，且不会让窗口可见。
                    for (int wi = 0; wi < restForms.Count; wi++)
                    {
                        // 句柄还不够：分层窗口的 DWM 表面是在第一次 Show() 时才建的。
                        // 所以再以**透明度 0** Show 一次再藏起来 —— 全程不可见，但把这块开销也挪到启动时。
                        RestForm rf = restForms[wi];
                        double keep = rf.Opacity;
                        try
                        {
                            rf.Opacity = 0.0;
                            rf.Show();
                            rf.Hide();
                        }
                        catch { }
                        finally
                        {
                            try { rf.Opacity = keep; } catch { }
                        }
                    }
                }

                // ---- A2：休息前 60 秒的渐进降饱和（每 tick 一次）----
                    // 求值点必须**在 gate 之后**：放在 tick 顶部的话，用户在上一次 tick
                    // 与本次 tick 之间切进全屏/演示，这一步会先把屏幕压暗，
                    // 下一个 tick 才被 gate 撤掉 —— 表现为"切全屏时闪一下变暗"。
                    // 放在这里时 blocked 已经算完：被压住的路径都 return 或 HideDim 过。
                    // DimTick 内部仍保留 resting / previewing / suppressed / paused 的判据做兜底。
                    // 沉浸模式下连"美化层"都不做：它们也是打扰（全屏压暗、边缘发光）。
                    if (!cfg.Immersive)
                    {
                        try { DimTick(); } catch { }

                        // ---- A1：边缘辉光（插件 EdgeGlow，与渐暗同一时机）----
                        // 未启用时 PluginHost.Get 返回 null → 这里什么都不做，
                        // 连窗口都不会被创建（"未启用零对象"的落点之一）。
                        try { RefreshEdgeGlow(); } catch { }
                    }

                    // ---- C3 / C4：到点前的"临门一脚"让路 ----
                    // 每 tick 只算一次（DeferredByActivity 内部会推进"连续命中"的计时）。
                    // 放在 gate 之后：被抑制的路径已经 return 过，没必要白跑一次进程查询。
                    bool deferred = DeferredByActivity(now);

                    // ---- 沉浸模式：只记账，不打扰 ----
                    // 位置很关键：在 gate 与"让路"之后（那两步决定"该不该打扰"），
                    // 在预告 / 到点 / 统计之前（那三步都会弹窗或改计数）。
                    // 累加器在上面已照常累加 —— 这正是它和「暂停」的区别。
                    if (cfg.Immersive)
                    {
                        if (isPreviewing) EndPreview();
                        RefreshRemaining();
                        UpdateTrayText();
                        return;
                    }

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
                            if (deferred)
                            {
                                // 正在打字 / 正在看片：**保留横幅**，只把文案换成"稍后提醒"。
                                //
                                // 这里原来是"先 EndPreview() 再 return"，注释写着"让路结束后会
                                // 自己重新进预告"——那条路**不存在**：收掉之后 isPreviewing 已是
                                // false，让路一结束就直接走下面的 `if (sedDue && !deferred) BeginRest`。
                                // 用户经历的是"看到 3 → 横幅消失 → 几秒后遮罩突然弹出"，
                                // 主观描述就是"倒计时卡住了"（QA 8/8 复现）。
                                //
                                // 横幅本身是 WS_EX_NOACTIVATE、不吃焦点、不拦键盘，留着不打扰；
                                // 计时也照旧在让路中推进，不存在"停表"。
                                UpdatePreviewCountdown();
                                RefreshRemaining();
                                UpdateTrayText();
                                return;
                            }
                            EndPreview();
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
                    //
                    // **不再要求 !deferred**（原来写着"deferred 期间不许再进预告"）。
                    // 那条限制的本意是防闪：弹出 → 到点被让路收掉 → 下一 tick 又弹。
                    // 但现在"到点被让路"已经不再收横幅了（见上面 isPreviewing 那一支），
                    // 闪烁的成因已经消失，这条门禁只剩下副作用：
                    //   **用户边打字边等提醒时，预告横幅从头到尾不出现** ——
                    //   QA 实测：PreviewSeconds=3 + 持续打字，横幅 8/8 次未出现。
                    // 而横幅是 WS_EX_NOACTIVATE、不吃焦点、不拦键盘，只是右下角一块卡片；
                    // "正在打字"恰恰是最该提前告知"我一会儿会提醒你"的时刻。
                    if (!paused && !idle && cfg.PreviewSeconds > 0)
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

        // ---- G1/G2 起身活动演示（插件 Movement）----
        //
        // **Movement 优先于 Ritual**：两者都开会抢同一个时机（久坐/活动休息的开始），
        // 并排弹两个窗是明确的错误。规则：
        //   Movement 启用 → 只弹动作演示（哪怕动作全被禁忌词排除，也只是安静休息，不退回去弹 Ritual）
        //   Movement 未启用 → Ritual 若启用则弹（保持批 2 前半的行为）
        //   两者都关 → 只有遮罩

        /// <summary>返回 true 表示"这一轮由 Movement 接管"，调用方据此跳过 Ritual。</summary>
        private bool ShowMovementForRest(int restSeconds)
        {
            MovementPlugin p = PluginHost.Get("Movement") as MovementPlugin;
            if (p == null) return false;
            try { return p.ShowForRest(restSeconds, cfg.MovementContraindications); }
            catch { return false; }
        }

        private void HideMovement()
        {
            MovementPlugin p = PluginHost.Get("Movement") as MovementPlugin;
            if (p != null) p.Hide();
        }

        /// <summary>把演示窗重新抬到置顶组最前面（理由同 BringRitualToTop）。</summary>
        private void BringMovementToTop()
        {
            MovementPlugin p = PluginHost.Get("Movement") as MovementPlugin;
            if (p == null) return;

            IntPtr h = p.WindowHandle;
            if (h == IntPtr.Zero) return;

            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
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
                        // E1：配额已经用完 → 按钮虽然是灰的，但这里仍守一道，
                        // 避免"按钮状态没刷新到"这种 UI 与逻辑脱节的窗口期。
                        if (!(cfg.AllowSkip && SkipAllowed())) return;
                        EndPreview();
                        cyclesWithoutRest++;      // A5：又一轮没真正休息，下次仪式音加一声
                        CountStatSkip();         // F1：跳过 +1
                        ConsumeSkip();           // E1：记一次配额
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

                // E1：今日配额用完 → 横幅上的「跳过」置灰（「延后」「立即休息」仍然可用）
                bool skipOk = cfg.AllowSkip && SkipAllowed();
                previewForm.SetSkipEnabled(skipOk);

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
                // 预告横幅出问题绝不能影响主流程：直接放弃预告，照常到点弹遮罩。
                // 这里必须连 previewDueMs 一起清 —— 它是"本轮预告的绝对到期时刻"，
                // 只在第一次进窗口时算一次；留着不清，下一轮会带着上一轮的过期时刻，
                // 表现为"第二次预告永远不出现"（QA 从现象反推出来的线索，已核）。
                isPreviewing = false;
                previewDueMs = 0;
            }
        }

        private void EndPreview()
        {
            if (!isPreviewing) return;
            isPreviewing = false;
            previewRemaining = 0;
            previewDueMs = 0;              // 下次预告重新取基准
            try { if (previewForm != null) previewForm.HidePreview(); } catch { }
        }

        /// <summary>每秒刷新横幅上的倒计时（值没变不重绘）。</summary>
        /// <summary>
        /// 到点却被让路时，横幅上显示的文案。
        ///
        /// 分两种措辞是因为原因不同、用户感受也不同：
        ///   · 正在打字 → "正在输入，稍后提醒"（他自己知道在打字）
        ///   · 在看片/开会（前台白名单）→ 只说"稍后提醒"，别硬说成"输入"
        /// 两种都**不能说"已暂停"** —— 计时并没有停，只是这一轮被让后。
        /// </summary>
        private string DueHeldText()
        {
            return c4DeferSinceMs > 0 ? "正在输入，稍后提醒" : "稍后提醒";
        }

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

            // 预告的剩余秒数同样用**绝对到期时刻**算：previewStartMs + left。
            // 只看累加器的话，tick 被系统合并时会一次掉 2 秒，看起来像卡顿。
            if (previewDueMs <= 0) previewDueMs = Clock.NowMs() + left;
            long leftAbs = previewDueMs - Clock.NowMs();
            if (leftAbs < 0) leftAbs = 0;

            int sec = (int)((leftAbs + 999) / 1000);

            // 到点却仍挂着横幅（= 到点被让路，见 OnTick 那一支）：
            // 数字停在 1（传 0 会显示"0 秒"，可此刻不是"已结束"，是"随时可能开始"），
            // 文案换成让路文案 —— 否则用户会以为倒计时坏了。
            if (leftAbs <= 0 || sec < 1)
            {
                sec = 1;
                body = DueHeldText();
            }

            if (sec != previewRemaining || body != previewLastBody)
            {
                previewRemaining = sec;
                previewLastBody = body;

                // 测试设施：EYE_LITE_TRACE_UI 已设时，把"预告期每次真正刷新数字"的墙钟打点追加进去。
                // 格式：P <Clock.NowMs()> <秒>   （休息期仍是纯数字，不动那段，避免破坏已有解析）
                //
                // 为什么这条能定性：相邻两条 P 的 NowMs 差若出现 2000~3000ms，
                // 说明**进程内 UI 线程被顶住了**（不是"探针慢"、不是"tick 没到"）。
                // 若 P 间隔正常（~1s）而用户仍看到数字卡住，那是绘制/合成的问题，方向完全不同。
                // 记在 SetCountdown **之前**：这一行的时刻是"tick 决定改数字"的时刻，
                // 不把 SetCountdown 自身的耗时算进间隔里。
                if (TraceUiFile.Length > 0)
                {
                    try
                    {
                        File.AppendAllText(TraceUiFile,
                            "P " + Clock.NowMs().ToString() + " " + sec.ToString() + Environment.NewLine,
                            new UTF8Encoding(false));
                    }
                    catch { }
                }

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
        // ---- 休息引导语的提示池（G12：三档语气）----
        //
        // 一次给一句、每轮随机，且**不与上一轮重复**（随机但保证变化）。
        // 为什么不用"按轮次索引轮换"：那版实测 3 轮拿到 0/0/1（会跳拍），
        // 没能在合理时间内定位；随机 + 去重 既能保证不重复，也不依赖任何计数器。
        //
        // 文案刻意"不写实"：不指定具体物品的位置，让人真的抬头去找，而不是盯着屏幕想。
        //
        // **三档必须逐条对齐**（同一句的三种说法，不是三套不相干的句子）：
        //   这是"语气"不是"内容"——用户换档后应该觉得"同一件事换了个说法"，
        //   而不是"功能变了"。所以每档都是 8 条，序号一一对应，改一条就三条一起改。
        //   条数必须相等，否则 EyeGameHint() 的 lastHint 去重会跨档串味。
        private static readonly string[][] EyeGameHints = new string[][]
        {
            // 0 温和（默认）：说明 + 轻引导，不带任何评价
            new string[]
            {
                "让眼睛离开屏幕：看看 6 米外，或在房间里找 3 样蓝色的东西",
                "找一个你平时没注意过的角落，认真看 5 秒",
                "数一数房间里有多少样会反光的东西",
                "找 2 样圆的东西，再把视线放到最远的那面墙上",
                "看看窗外最远的那栋楼，数一数它有几层",
                "找一样比手掌小的东西，看清它的边缘",
                "找一个绿色以外、又不像灰色的东西，看 3 秒",
                "看看房间里最暗的那个角落，等它变清楚"
            },
            // 1 冷淡：只说做什么，一句一个动作，不解释
            new string[]
            {
                "看 6 米外，20 秒。或者找 3 样蓝色的东西",
                "找个没注意过的角落，看 5 秒",
                "数反光的东西",
                "找 2 样圆的，然后看最远那面墙",
                "数窗外最远那栋楼有几层",
                "找一样比手掌小的，看清边缘",
                "找一样不是绿也不是灰的，看 3 秒",
                "看最暗的角落，等它变清楚"
            },
            // 2 毒舌：开火对象只能是**行为 / 椅子 / 屏幕 / 这轮状态**，
            //   绝不能是用户的身体或人格（见 AppConfig.Tone 的两条硬约束）。
            new string[]
            {
                "屏幕不差你这 20 秒。抬头，6 米外，或者找 3 样蓝色的东西",
                "找个你没注意过的角落看 5 秒——它比这个屏幕有意思",
                "数数房间里有几样反光的东西。反正你也不会数它的代码行数",
                "找 2 样圆的，再看最远那面墙。眼睛比进度条重要",
                "数数窗外最远那栋楼有几层。数不清就说明该歇了",
                "找一样比手掌小的，看清边缘。别用小字验光",
                "找一样不是绿也不是灰的看 3 秒。难吧？你的眼睛也在干这事",
                "看看最暗的角落，等它变清楚。你适应亮度，就是这个过程"
            }
        };

        private static readonly Random eyeGameRnd = new Random();
        private int lastEyeGameHint = -1;

        /// <summary>
        /// 取一句本档语气的引导语，保证不与上一句重复。
        /// Tone 越界或对应档为空时退回 0 档 —— 语气是纯装饰，绝不能因此不弹休息。
        /// </summary>
        private string EyeGameHint()
        {
            int tier = cfg != null ? cfg.Tone : 0;
            if (tier < 0 || tier >= EyeGameHints.Length) tier = 0;
            string[] pool = EyeGameHints[tier];
            if (pool == null || pool.Length == 0) pool = EyeGameHints[0];

            int n = pool.Length;
            if (n <= 1) return pool[0];

            int pick;
            do { pick = eyeGameRnd.Next(n); } while (pick == lastEyeGameHint);
            lastEyeGameHint = pick;
            return pool[pick];
        }

        /// <summary>
        /// G12：按当前语气档三选一。三档一一对应（同一件事的三种说法）。
        /// 语气是纯装饰，任何异常都必须退回温和档，不能因为文案把休息卡住。
        /// </summary>
        private string ToneText(string gentle, string plain, string savage)
        {
            int tier = cfg != null ? cfg.Tone : 0;
            if (tier == 1) return plain;
            if (tier == 2) return savage;
            return gentle;
        }

        // ============================================================
        // (d) 到点反应：从 1s tick 上解耦，挂到 200ms 的 fastUi 上
        // ============================================================
        //
        // 为什么需要它：主定时器是 WinForms Timer（WM_TIMER），**不排队、不补发**。
        // UI 线程一旦被阻塞 ≥1s（写 layered 窗口 Opacity 撞上 DWM 同步点、
        // 托盘 Shell_NotifyIcon、GC/COM 等待……），这次 tick 就被丢弃，
        // 之后每一次 tick 永久后移 —— 误差 = **丢失的 tick 数 × 1000ms**，离散且累积。
        // 实测 p0a：60.5 / 62.4 / 63.5 三簇正好对应丢 0 / 1 / 2 个 tick（不是"相位差"，
        // 相位差会在 60.5~61.5 之间连续分布）。
        //
        // 所以修法不是"让那一步变快"，而是"让反应不再依赖 tick 到达"：
        // 用**绝对时刻**判断是否已过点，200ms 一次，最坏延迟 200ms，与 tick 漂移无关。
        //
        // 三条铁律（改之前先读）：
        //   1. **只读累加器**。一旦在这里写 workAccumMs/sedAccumMs/lastAnchorMs，
        //      1s tick 的 dt 会重复计算，时间会走快。
        //   2. DeferredByActivity **每个快照最多调用一次**并复用结果 —— 它内部会推进
        //      c3/c4 的"连续命中"起点，还会枚举前台进程（不便宜）。因此这里先看
        //      deferUntilMs：让路进行中就直接返回，把续期留给 1s tick，
        //      避免"让路 10 分钟里每 200ms 枚举一次进程"。
        //   3. **不碰预告横幅**。fastUi 只在 now >= due 时才动作，而预告窗口是
        //      left <= PreviewSeconds，**严格早于** due —— 所以它抢不到横幅，
        //      横幅仍由 OnTick 按原时序启动。反过来，如果在这里 due 时补一个
        //      StartPreview，下一次 200ms 就会 EndPreview + BeginRest，
        //      横幅只闪 0.2 秒，比不弹更像 bug。
        private void FastDueSnapshot()
        {
            // ============================================================
            // 诊断（默认不启用，保留设计）：Q <now> <gap>
            // ============================================================
            //
            // 为什么需要它：`T` 打点记的是 **1s tick** 的间隔，分辨率只有 1000ms。
            // 它只能回答"这一秒的 tick 有没有来"，**分不清**这两件事：
            //   · 一次 1.9 秒的阻塞（一次卡顿），
            //   · 丢了 2 个 tick（可能是两次独立的小卡顿，也可能是一次长的）。
            // 而本方法由 200ms 的 fastUi 每 200ms 进一次，在这里量"距上次进来过了多久"，
            // 能把阻塞时长直接量到 ±200ms，而且能看出阻塞发生在**哪一秒**里。
            //
            // 为什么只在 gap > 400ms 时才写：
            //   正常轮次 fastUi 每 200ms（实测 200~230ms）进一次，400ms 已经意味着
            //   "至少漏了一次"，天然是异常阈值。正常一轮**一行都不写**，
            //   所以对 IO 与 trace 体积的影响可以忽略 ——
            //   对比 `T` 行是每秒固定写 1 行、`P` 行是每次数字变化写 1 行。
            //
            // 启用方式（数据出来支持这个方向后，改两处即可）：
            //   1) 在字段区加一个：private long qLastMs;
            //   2) 去掉下面五行的注释。
            //
            // 判读：
            //   Q 行出现、gap ≈ 1900~2200  → 与 `T` 的 2000 空档互相印证：确有**阻塞**
            //   Q 行为空、但 `T` 有空档     → 不是 UI 线程阻塞，而是 tick 被系统合并/节流
            //   （这两种的修法完全不同：前者要减少阻塞源，后者要换触发机制）
            //
            // if (TraceFile.Length > 0)
            // {
            //     long qNow = Clock.NowMs();
            //     if (qLastMs > 0 && qNow - qLastMs > 400)
            //         TraceAppend("Q " + qNow.ToString() + " " + (qNow - qLastMs).ToString());
            //     qLastMs = qNow;
            // }

            // 休息中：刷秒数 + **到点收窗**。
            //
            // 为什么收窗也要挪到这里：原来只有 1s tick 判 `now >= restDueMs`，
            // 于是"遮罩消失"同样被量化到 0~1000ms —— 实测 END 行报 5872ms
            // 对 5 秒休息，恒定多出约 1 秒（QA 多轮测出的 8.90~9.02s 对 8s 配置
            // 也是同一个系统偏差，只是被 ±2s 容差盖住了）。
            // (d) 已经治好了"开始"，这里补上"结束"。
            if (isResting)
            {
                // 顺序不能反：先刷 UI 让用户看到 0，再收窗；
                // 反过来会闪一帧"还剩 1 秒"，看起来像被谁提前掐掉了。
                UpdateRestUI();

                if (Clock.NowMs() >= restDueMs)
                {
                    // 与 OnTick 的"自然坐满"分支**逐行一致**，不能省：
                    // 少了这三行，F1 的完成数不涨、A5 的"连续没休息"不清零。
                    CompletedRests++;
                    cyclesWithoutRest = 0;
                    CountStatDone();

                    // 参数必须是 true（= 跳过式收尾，会 ResetWork 清累加器），
                    // 与 OnTick 的自然坐满路径一致。false 是「推迟」语义（保留累加器），
                    // 用错了会表现为"坐满一轮后立刻又到点"。
                    EndRest(true);
                }
                return;
            }

            if (isPreviewing) UpdatePreviewCountdown();

            // 与 OnTick 同一组"不该弹"的闸门（少一个就会出现"暂停了还弹"）
            if (paused || isSuppressed || cfg.Immersive) return;

            long now = Clock.NowMs();

            // 让路进行中：1s tick 会继续续期，这里不必重复问（见铁律 2）
            if (deferUntilMs > 0 && now < deferUntilMs) return;

            // 只读地反算"理论到期时刻"：lastAnchorMs 是上一个 tick 的时刻，
            // 累加器是那一刻的值，所以 lastAnchorMs + 剩余量 = 精确的到期墙钟时刻。
            long dueWork = long.MaxValue;
            long dueSed = long.MaxValue;
            if (workLimitMs > 0) dueWork = lastAnchorMs + (workLimitMs - workAccumMs);
            if (cfg.SedentaryEnabled && sedLimitMs > 0) dueSed = lastAnchorMs + (sedLimitMs - sedAccumMs);

            bool workDue = now >= dueWork;
            bool sedDue = now >= dueSed;
            if (!workDue && !sedDue) return;      // 绝大多数 200ms 切片在这里返回，零成本

            // 到这里才问一次让路（每个快照最多一次）
            bool deferred = DeferredByActivity(now);
            if (deferred) return;

            // 久坐优先：与 OnTick 里 sedDue 先于 workDue 的判定顺序保持一致
            bool bodyBreak = sedDue;

            if (isPreviewing) EndPreview();
            BeginRest(bodyBreak);
        }

        private void BeginRest(bool bodyBreak)
        {
            // 重入守卫（与 EndRest 的 `if (!isResting) return;` 对称）。
            //
            // 为什么现在必须有：(d) 之后"到点反应"有了两个入口 —— 1s 的 OnTick 与
            // 200ms 的 fastUi。同一 UI 线程下它们不会真正并发，但一旦哪天真并发、
            // 或谁在 isResting 已为真时又调一次，这一轮休息会被**重新计时**
            // （restDueMs 被改写），表现为"刚弹出来的遮罩又从头开始倒数"。
            // 放在**最前面**还有第二个作用：重复调用不会往 trace 里多写一行 BEGIN，
            // 否则 QA 解析 lateMs 时会看到成对的重复行。
            if (isResting) return;

            // ---- p0a 追踪：理论到期时刻 vs 实际开始时刻 ----
            // 放在**最入口**：哪怕后面任何一步抛异常，这一行也已经落盘。
            // 未设 EYE_LITE_TRACE_FILE 时 TraceFile 是空串，这里只是一次长度判断，零 I/O。
            //
            // dueMs 的算法：累加器超了阈值多少，就说明"该弹的时刻"是多久之前 ——
            //   accum - limit = 已经超时的毫秒数，fireMs 减去它就是理论到期时刻。
            // 活动休息看 sedAccumMs/sedLimitMs，护眼看 workAccumMs/workLimitMs（两套时钟不混算）。
            if (TraceFile.Length > 0)
            {
                long fireMs = Clock.NowMs();
                long accum = bodyBreak ? sedAccumMs : workAccumMs;
                long limit = bodyBreak ? sedLimitMs : workLimitMs;
                long dueMs = fireMs;
                if (limit > 0) dueMs = fireMs - (accum - limit);
                TraceAppend(TraceBeginLine(dueMs, fireMs));
            }

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
            //
            // G12：两条固定文案也跟着语气档走（三档一一对应，别单独改一档）。
            string eyebrow;
            string hint;
            if (bodyBreak)
            {
                eyebrow = "活动一下";
                hint = ToneText("起身走两步，或者坐着舒展一下都行",
                                "起身，走两步",
                                "起来动一动。椅子又不会跑");
            }
            else if (cfg.PomodoroEnabled)
            {
                eyebrow = "番茄钟休息";
                hint = ToneText("这一轮专注结束了，起身活动一下",
                                "这一轮结束，起身",
                                "这一轮结束了。下一轮也是这个屏幕，先歇会儿");
            }
            else
            {
                eyebrow = "休息一下";
                // 「找东西」：把"远眺"和"在房间里找东西"合成一句。
                //
                // 为什么不做成每轮轮换：我先写了个轮换版本，实测 3 轮拿到 0/0/1（会跳拍），
                // 没能定位。**行为不明的功能不发布** —— 退回一句固定文案，
                // 用户要求的是"挂到普通护眼休息"，不是"必须轮换"。
                // 也不做成屏幕上的动画：护眼休息要眼睛离开屏幕，动画等于把视线钉回来。
                hint = cfg.EyeGameEnabled
                     ? EyeGameHint()
                     : ToneText("让眼睛看看 6 米外的东西，放松睫状肌",
                                "看 6 米外，20 秒",
                                "6 米外，20 秒。屏幕不差这 20 秒");
            }

            bool pomo = bodyBreak ? false : cfg.PomodoroEnabled;   // 身体休息不用番茄钟配色
            bool skipOkThisRound = cfg.AllowSkip && SkipAllowed();  // E1：本轮「跳过」是否可用

            for (int i = 0; i < restForms.Count; i++)
            {
                RestForm f = restForms[i];
                f.SetPomodoro(pomo);          // 必须在 SetHint / ShowRest 之前（换配色与副提示）
                f.SetHint(eyebrow, hint);
                f.SetSkipEnabled(skipOkThisRound);   // E1：配额用完则置灰，且必须在 ShowRest 之前
                f.ShowRest();
            }

            tray.Icon = iconRest;
            if (cfg.SoundEnabled) Sfx.PlayStart();

            // 遮罩已经摆好之后再弹小窗，否则小窗会被随后 Show 出来的全屏遮罩盖住。
            //
            // **只在久坐/活动休息（bodyBreak）时弹**：
            //   普通护眼休息的目的是"让眼睛离开屏幕"，在屏幕正中弹一个会动的窗口
            //   等于把视线钉回屏幕上 —— 目的相反，不是文案不一致。
            //   将来若要"护眼休息也能做眼操"，应该是用户在遮罩上主动点一下，而不是自动弹。
            //
            // **Movement 优先于 Ritual**（两者都开会抢同一个时机，并排弹是错的）：
            //   ShowMovementForRest 返回 true = 这一轮由 Movement 接管 → 不再弹 Ritual。
            //   插件都没启用时两个 Get 都返回 null，这里什么都不会被创建。
            if (bodyBreak)
            {
                bool takenByMovement = false;
                try { takenByMovement = ShowMovementForRest(restTotalSeconds); } catch { }
                if (!takenByMovement)
                {
                    try { ShowRitualForRest(); } catch { }
                }
            }

            // ---- E1：今日跳过配额 ----
            // 配额用完时**连回调都不挂**（而不是"挂了回调再在里面判")：
            // 全局钩子是"先决定吞键、再投递回调"，在回调里 return 只会变成
            // "键被吞了但没有跳过" —— 那比不响应更糟。置 null 之后
            // Esc/空格会走完钩子的后续判断（非严格模式下不吞），键正常传给系统。
            bool canSkip = cfg.AllowSkip && SkipAllowed();

            if (canSkip)
            {
                // F1：键盘 Esc / 空格跳过与点「跳过」按钮是同一个动作，必须一起计数。
                // 计数放在 isResting 判断之后：钩子可能重复投递（key repeat），
                // 只让第一次算数（与 EndRest 自己的重入守卫同一口径）。
                KeyboardGuard.OnSkipKey = delegate
                {
                    if (!isResting) return;
                    CountStatSkip();
                    ConsumeSkip();      // E1 记账要在 EndRest 之前（EndRest 会清本轮状态）
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
            //
            // E1 与上面这张表的关系：blockAll 的判据仍然是**配置**（cfg.AllowSkip），
            // 不看运行时的配额余量 —— 否则"严格模式"会在配额用完那一刻悄悄降级成
            // "只拦系统键"，那是把一个用户明确打开过的开关偷偷关掉。
            // 配额用完 + 严格模式的正确表现是：键盘照旧被锁，只是 Esc/空格不再能跳过。
            bool needHook = canSkip || cfg.StrictMode;

            // ---- E6：严格模式下先给 N 秒倒计时，倒计时结束才真正装钩子 ----
            // 这 N 秒里键盘完全自由（钩子没装），遮罩上用大字倒数，
            // 用户还来得及用鼠标「跳过」/「推迟」。
            if (needHook && cfg.StrictMode && cfg.LockCountdownSeconds > 0)
            {
                // 倒计时不得吃掉整段休息：否则"严格模式"在短休息下**永远不锁**。
                //   例如 RestSeconds=8、LockCountdownSeconds=15（默认）→ 8 秒休息里
                //   钩子从头到尾没装上，用户打开了严格模式却完全没被锁
                //   （实测 TC 对照组：F24 放行 2 条，期望 0）。
                // 规则：最多用掉"休息时长 - 3 秒"，至少给真锁留 3 秒；
                //      休息本身 ≤3 秒时不给倒计时，立即锁。
                // 这也让默认配置（20 分钟 / 20 秒）从"只有最后 5 秒锁"变成明确的"最后 5 秒锁"，
                // 而不是"15 秒自由 + 5 秒锁"这种说不清的行为。
                int cd = cfg.LockCountdownSeconds;
                int cap = restTotalSeconds - 3;
                if (cd > cap) cd = cap;

                if (cd <= 0)
                {
                    lockDueMs = 0;
                    try { SetLockCountdownOnForms(-1); } catch { }
                    InstallRestHookNow();
                }
                else
                {
                    long nowRest = Clock.NowMs();
                    lockDueMs = nowRest + (long)cd * 1000L;
                    try { SetLockCountdownOnForms(cd); } catch { }
                }
            }
            else
            {
                lockDueMs = 0;
                try { SetLockCountdownOnForms(-1); } catch { }
                InstallRestHookNow();
            }

            UpdateRestUI();
            UpdateTrayText();
        }

        /// <summary>
        /// 真正安装休息期的键盘钩子。E6 的倒计时结束时也从这里装上，两条路径共用一套判据。
        /// `OnSkipKey` 的回调在 BeginRest 里已经挂好（配额用完时是 null），这里只管装钩子。
        /// </summary>
        private void InstallRestHookNow()
        {
            bool canSkip = cfg.AllowSkip && SkipAllowed();
            if (!canSkip && !cfg.StrictMode) return;      // 没什么要吞的 → 零开销

            bool blockAll = cfg.StrictMode && cfg.AllowSkip;
            if (!KeyboardGuard.Install(cfg.StrictMode, blockAll)) WarnStrictUnavailable();
        }

        // ---- E1 跳过配额 ----

        /// <summary>今天还能不能跳过（插件未启用 / 配额 0 时恒为 true）。</summary>
        private bool SkipAllowed()
        {
            GuardPlugin g = PluginHost.Get("Guard") as GuardPlugin;
            if (g == null) return true;
            return g.CanSkip(cfg);
        }

        /// <summary>记一次跳过（插件未启用时什么都不做）。</summary>
        private void ConsumeSkip()
        {
            GuardPlugin g = PluginHost.Get("Guard") as GuardPlugin;
            if (g != null) g.ConsumeSkip(cfg);
        }

        // ---- E6 锁键盘倒计时 ----

        /// <summary>把倒计时刷到所有遮罩上（secLeft &lt; 0 = 收掉倒计时、恢复主提示语）。</summary>
        private void SetLockCountdownOnForms(int secLeft)
        {
            for (int i = 0; i < restForms.Count; i++)
            {
                try
                {
                    RestForm f = restForms[i];
                    if (f == null || f.IsDisposed) continue;
                    f.SetLockCountdown(secLeft);
                }
                catch { }
            }
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

            // ---- p0a 追踪：收尾行（end \t ISO \t 实际持续毫秒）----
            // 起点从 restDueMs 反推：restDueMs 就是 BeginRest 写下的 now + 总时长。
            // 有了 BEGIN/END 两行，p0a 才能交叉核对"这一轮到底坐了多久"，
            // 也能把"遮罩何时关掉"与"计时器何时醒来"分开看。
            if (TraceFile.Length > 0)
            {
                long nowMs = Clock.NowMs();
                long startMs = restDueMs - (long)restTotalSeconds * 1000L;
                long durMs = nowMs - startMs;
                if (durMs < 0) durMs = 0;
                TraceAppend("end\t" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")
                            + "\t" + durMs);
            }

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

            // B1/B2 休息仪式 / G1-G2 动作演示：休息一结束就关窗并释放（都是一次性的，不复用）
            try { HideMovement(); } catch { }
            try { HideRitual(); } catch { }

            // E6：倒计时状态必须归零，否则下一轮 BeginRest 会因为"主提示位还被倒计时占着"
            // 而把 SetHint 写不进去（SetHint 在 lockCountdownOn 为真时只记文案不写控件）。
            lockDueMs = 0;
            try { SetLockCountdownOnForms(-1); } catch { }

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
                    // E1：配额用完时按钮已置灰，这里再守一道（UI 与逻辑不脱节）
                    if (!(cfg.AllowSkip && SkipAllowed())) return;
                    cyclesWithoutRest++;      // A5：遮罩上点跳过也算"这一轮没真休息"
                    CountStatSkip();          // F1：跳过 +1
                    ConsumeSkip();            // E1：记一次配额
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
            // 用**绝对到期时刻**算剩余，而不是信任"每秒减一"：
            // tick 被系统合并时会一次减 2，用户看到 8 直接跳到 6 —— 感觉像卡顿。
            // 改成从 restDueMs 反算之后，无论 tick 什么时候来，数字都是真实的剩余秒数。
            long leftMs = restDueMs - Clock.NowMs();
            int leftSec = (int)((leftMs + 999L) / 1000L);
            if (leftSec < 0) leftSec = 0;
            restRemaining = leftSec;

            if (restRemaining == lastShown) return;
            lastShown = restRemaining;

            if (TraceUiFile.Length > 0)
            {
                try { File.AppendAllText(TraceUiFile, leftSec.ToString() + Environment.NewLine, new UTF8Encoding(false)); }
                catch { }
            }
            for (int i = 0; i < restForms.Count; i++)
            {
                try { restForms[i].UpdateView(restRemaining, restTotalSeconds); }
                catch { }
            }
        }

        private void UpdateTrayText()
        {
            // A/B 测试闸门：EYE_LITE_NO_TRAY_TEXT=1 时整段跳过（不设时一行分支，行为不变）
            if (NoTrayText) return;

            try
            {
                string s;
                if (cfg.Immersive) s = "沉浸模式 · 照常计时，不打扰";
                else if (paused) s = "已暂停 · 右键可恢复";
                else if (isResting) s = "休息中 " + restRemaining + " 秒";
                else if (isPreviewing) s = "即将休息 " + previewRemaining + " 秒";
                else if (isSuppressed && !suppressForced)
                {
                    // 抑制期间必须说清楚"为什么没提醒"，否则用户以为程序坏了。
                    // 文案区分全屏与演示（NotifyIcon.Text 上限 63 字符，务必短）。
                    // suppressForced 为真时抑制已经放行（只是 QUNS 还没回到非抑制值），
                    // 此时再说"暂缓"就是撒谎 —— 让它落到下面的正常剩余时间文案。
                    string reason = inMeeting ? "会议中，提醒暂缓" : NotificationState.ReasonText(currentQuns);
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
        /// <summary>
        /// 托盘里的插件开关。
        ///
        /// 失败必须回滚勾选并告知：插件 OnEnable 可能建窗/建 Timer 失败，
        /// 留在"勾上了但没生效"的状态比不勾更糟（与设置页「扩展」卡同一套纪律）。
        /// </summary>
        private void TogglePluginFromTray(string id, ToolStripMenuItem item)
        {
            try
            {
                PluginHost.SetOn(id, item.Checked, cfg);
                cfg.Save();
                UpdateTrayText();
            }
            catch (Exception ex)
            {
                item.Checked = !item.Checked;
                try
                {
                    MessageBox.Show("插件没能启动：" + ex.Message, "护眼小助手",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
            }
        }

        /// <summary>
        /// 切换沉浸模式。
        ///
        /// 开启时必须**立刻**把已经挂着的载体收掉，不能等下一 tick：
        /// 用户点完菜单还看着一块全屏遮罩，那就不叫"沉浸"。
        /// </summary>
        private void SetImmersive(bool on)
        {
            cfg.Immersive = on;
            try { cfg.Save(); } catch { }

            if (on)
            {
                try { if (isPreviewing) EndPreview(); } catch { }
                try { if (isResting) EndRest(false); } catch { }   // 当作跳过：并没有真的休息过
                try { HideDim(); } catch { }
                try { HideEdgeGlow(); } catch { }
                try { HideRitual(); } catch { }
                try { HideMovement(); } catch { }
            }

            UpdateTrayText();
        }

        // 例程窗的引用：一次只允许一个，重复点击先关旧的
        private MoveForm routineForm;

        /// <summary>
        /// G10：播放一条缓解例程。
        ///
        /// 复用 MoveForm：预算 = 各动作 Seconds 之和，播完最后一个正好收窗。
        /// 不做"点击后自动进休息"这类联动 —— 用户只是想让脖子舒服点，
        /// 不该顺手把他这一轮的工作时钟重置掉。
        /// </summary>
        private void ShowRoutine(string name)
        {
            try
            {
                List<MoveItem> pick = MoveLib.PickRoutine(name);
                if (pick.Count == 0) return;

                int total = 0;
                for (int i = 0; i < pick.Count; i++) total += pick[i].Seconds;
                if (total < 1) total = 1;

                if (routineForm != null)
                {
                    try { routineForm.Close(); routineForm.Dispose(); } catch { }
                    routineForm = null;
                }

                routineForm = new MoveForm(pick, total);
                routineForm.FormClosed += delegate { routineForm = null; };
                routineForm.Show();
            }
            catch { }
        }

        /// <summary>
        /// 测试设施：只播一个动作（EYE_LITE_FAKE_ACTION）。
        /// 与 ShowRoutine 共用 routineForm，所以同一时刻只会存在一个演示窗。
        /// 名字对不上就**什么都不做** —— 不弹空窗、不抛异常
        /// （否则脚本会拿到一个"空演示"的假通过）。
        /// </summary>
        private void ShowSingleAction(string name)
        {
            try
            {
                if (name == null) return;
                string want = name.Trim();
                if (want.Length == 0) return;

                List<MoveItem> all = MoveLib.Build();
                MoveItem hit = null;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].Name == want) { hit = all[i]; break; }
                }
                if (hit == null) return;

                List<MoveItem> one = new List<MoveItem>();
                one.Add(hit);

                int total = hit.Seconds;
                if (total < 1) total = 1;

                if (routineForm != null)
                {
                    try { routineForm.Close(); routineForm.Dispose(); } catch { }
                    routineForm = null;
                }

                routineForm = new MoveForm(one, total);
                routineForm.FormClosed += delegate { routineForm = null; };
                routineForm.Show();
            }
            catch { }
        }

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
            // A/B 测试闸门：EYE_LITE_NO_TRAY_ICON=1 时整段跳过（不设时一行分支，行为不变）
            if (NoTrayIcon) return;

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
        // cfg.DimAlphaMax/100，2000ms 一档；任何键鼠输入立刻撤销并放弃本轮。
        //
        // **默认关闭**（AppConfig.DimAlphaMax 的默认值是 0），原因写在 DimTick 的静默窗口那段：
        // 这个功能会让"60±3 秒到点"这个核心承诺变得不可靠。

        /// <summary>
        /// 到点前这么长时间内**一帧都不写** Opacity（静默窗口）。
        /// 5 秒是按"遮罩自己要花的合成时间（实测冷启动可达 1~4 秒）"留的余量。
        /// </summary>
        private const int DimQuietMs = 5000;
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

            // ---- 静默窗口：最后 DimQuietMs 秒一帧都不写 ----
            //
            // 为什么需要它：每写一次 Form.Opacity，DWM 就要把**整块屏幕**重新合成一次。
            // 60 秒窗口里原本要写约 45~60 次，正好压在"该弹遮罩"那几秒上 ——
            // 这是实测出来的**第二个**延迟源（关掉渐暗后方差从 3.97s 塌到 0.86s）。
            // 最后 5 秒"冻结画面"：不写、也不 hide，把 DWM 的活让给遮罩自己。
            //   · 不 hide：否则会看到"到点前屏幕突然变亮"，像被取消了；
            //   · 留着也不碍事：遮罩是不透明的，盖上去就看不见这层了。
            // **位置必须在"输入就放弃本轮"之后** —— 否则最后 5 秒里用户开始打字，
            // 屏幕还暗着，而"一有输入就撤"是本功能的前提。
            if (left <= DimQuietMs) return;

            // 两次 Opacity 写入之间的最小间隔。
            //
            // 三段历史，每次都是同一个方向 —— 减少 DWM 全屏重合成的次数：
            //   500ms（每轮约 120 次）→ 1000ms（约 45 次，实测把 tick 拖到 63.53s）
            //   → 2000ms（约 27 次）。双屏 2560x1440 下这个代价是实测到的，不是理论。
            //   视觉上仍是连续变暗：最大档 45% 分 27 步走完，一步约 1.7% alpha。
            int step = (int)((60000L - left) / 2000L);
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
            // 档位也要归零。理由不是"防不暗"，而是"防延迟半秒"：
            // 若撤掉时 dimLastStep 还停在末尾的档位，重新进窗口后要等到
            // 档位再次变化（2000ms 一档）才会写入第一帧 —— 表现为压下后要等最多 2 秒才见暗。
            // 调用方大多也会自己置 -1，但那依赖每个分支都记得写；放在这里才是不变式。
            dimLastStep = -1;
            if (dimForms == null) return;

            for (int i = 0; i < dimForms.Count; i++)
            {
                DimForm f = dimForms[i];
                if (f == null || f.IsDisposed) continue;
                // 已经隐藏的层不要再写一次 Opacity。
                // 为什么要管这个：本方法每 tick 都会被"不该暗"的分支调用，
                // 而每次写 Opacity 都是一次 DWM 全屏重合成 —— 白白把 UI 线程往合成上送。
                // 隐藏前一定是先 SetAlpha(0) 再 Hide，所以"不可见"等价于"已经全透明"，
                // 跳过它不会留下残余的暗层。
                if (!f.Visible) continue;
                try
                {
                    f.SetAlpha(0.0);
                    f.Hide();
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
            try { if (fastUi != null) fastUi.Stop(); } catch { }
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
