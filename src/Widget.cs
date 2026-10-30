using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace AmoWinTrans
{
    /// <summary>
    /// 悬停在窗口标题栏上时冒出来的那个小控件。
    ///
    /// 两个形态：
    ///   - 折叠态：一个 40x22 的小胶囊，里面是"透明度"图标 + 当前百分比（100% 时显示一个小箭头）
    ///   - 展开态：向下弹出的面板，含滑块、预设、置顶 / 穿透 / 重置
    ///
    /// 全部自绘，窗体带 WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW，
    /// 所以它永远不会抢走目标窗口的焦点，也不会出现在 Alt+Tab 里。
    /// </summary>
    internal sealed class Widget : Form
    {
        // ---- 设计尺寸（96 DPI 下的逻辑像素，实际会按目标窗口 DPI 缩放）----
        private const int PillW = 40;
        private const int PillH = 22;
        private const int PanelW = 348;
        private const int PanelH = 122;

        private const int Pad = 12;
        private const int HeaderY = 9;
        private const int SliderX = 14;
        private const int SliderY = 36;
        private const int SliderW = 320;
        private const int SliderH = 6;
        private const int KnobR = 7;
        private const int PresetY = 52;
        private const int PresetH = 24;
        private const int PresetW = 60;
        private const int PresetGap = 5;
        private const int PresetX0 = 14;
        private const int BottomY = 86;
        private const int BottomH = 24;
        private const int BottomW = 50;
        private const int BottomGap = 4;
        private const int BottomX0 = 14;
        private const float Radius = 7f;

        private const int ChipPreset0 = 0;   // 预设 0..4
        private const int ChipPin = 100;
        private const int ChipThrough = 101;
        private const int ChipAuto = 102;
        private const int ChipAutoHide = 104;
        private const int ChipKeepAlive = 105;
        private const int ChipReset = 103;

        private static readonly int[] Presets = { 100, 80, 60, 40, 20 };

        // ---- 配色 ----
        private static readonly Color BgColor = Color.FromArgb(31, 31, 35);
        private static readonly Color BorderColor = Color.FromArgb(62, 62, 70);
        private static readonly Color TextColor = Color.FromArgb(236, 236, 240);
        private static readonly Color MutedColor = Color.FromArgb(150, 150, 160);
        private static readonly Color AccentColor = Color.FromArgb(76, 194, 255);
        private static readonly Color AccentGreen = Color.FromArgb(88, 214, 141);
        private static readonly Color ChipBg = Color.FromArgb(47, 47, 54);
        private static readonly Color ChipHover = Color.FromArgb(62, 62, 72);
        private static readonly Color TrackColor = Color.FromArgb(56, 56, 64);
        private static readonly Color ChipTextOn = Color.FromArgb(16, 20, 26);

        // ---- 状态 ----
        public IntPtr Target = IntPtr.Zero;
        public int Percent = 100;
        public bool IsTopMost;
        public bool IsClickThrough;
        public bool IsAutoPressing;
        public bool IsAutoHide;
        public bool IsKeepAlive;
        public bool IsMovedAway;
        public string AutoKeyName = "";

        public event Action<int> OpacityChanged;
        public event Action<bool> TopMostToggled;
        public event Action<bool> ClickThroughToggled;
        public event Action<bool> AutoHideToggled;  // 切换「鼠标移出就隐藏」
        public event Action<bool> KeepAliveToggled; // 切换「保活」
        public event Action KeepAliveMoveRequested; // 右键「保活」：挪到屏幕外 / 挪回来
        public event Action AutoPressToggled;      // 左键点「连按」：开 / 关
        public event Action AutoPressConfigure;    // 右键点「连按」：打开设置
        public event Action ResetRequested;

        private bool _expanded;
        private bool _dragging;
        private int _anchorRight;
        private int _anchorTop;
        private double _scale = 1.0;
        private int _hoverChip = -1;

        private readonly Timer _expandTimer = new Timer { Interval = 110 };
        private readonly Timer _collapseTimer = new Timer { Interval = 320 };

        private Font _fontTitle;
        private Font _fontValue;
        private Font _fontChip;
        private Font _fontSmall;

        private Rectangle _rcSliderHit;
        private readonly Rectangle[] _rcPresets = new Rectangle[5];
        private Rectangle _rcPin;
        private Rectangle _rcThrough;
        private Rectangle _rcAuto;
        private Rectangle _rcAutoHide;
        private Rectangle _rcKeepAlive;
        private Rectangle _rcReset;

        public Widget()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            BackColor = BgColor;
            Opacity = 0.96;
            AutoScaleMode = AutoScaleMode.None;
            Text = "AmoWinTransWidget";
            Size = new Size(PillW, PillH);

            BuildFonts();

            _expandTimer.Tick += (s, e) =>
            {
                _expandTimer.Stop();
                if (!Visible) return;
                if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
                Expand();
            };

            _collapseTimer.Tick += (s, e) =>
            {
                _collapseTimer.Stop();
                if (!_expanded || _dragging) return;
                if (ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
                Collapse();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        public bool IsExpanded => _expanded;

        public int PillWidth(double scale) => (int)Math.Round(PillW * scale);
        public int PillHeight(double scale) => (int)Math.Round(PillH * scale);

        // ------------------------------------------------------------------
        //  外部接口
        // ------------------------------------------------------------------

        /// <summary>把控件挂到某个窗口上。anchorRight/anchorTop 是屏幕坐标下的锚点（右上角）。</summary>
        public void AttachTo(IntPtr target, int anchorRight, int anchorTop, double scale)
        {
            bool scaleChanged = Math.Abs(scale - _scale) > 0.001;
            if (scaleChanged)
            {
                _scale = scale;
                BuildFonts();
            }

            if (Target != target || scaleChanged)
            {
                _expanded = false;
                _dragging = false;
                _hoverChip = -1;
            }

            Target = target;
            _anchorRight = anchorRight;
            _anchorTop = anchorTop;
            ApplyBounds();

            if (!Visible)
            {
                Show();
            }
            RaiseTopMost();
            if (scaleChanged) Invalidate();
        }

        /// <summary>目标窗口移动/缩放时，只更新锚点，不重置状态。</summary>
        public void MoveAnchor(int anchorRight, int anchorTop)
        {
            if (_anchorRight == anchorRight && _anchorTop == anchorTop) return;
            _anchorRight = anchorRight;
            _anchorTop = anchorTop;
            ApplyBounds();
        }

        public void HideWidget()
        {
            _expandTimer.Stop();
            _collapseTimer.Stop();
            _expanded = false;
            _dragging = false;
            _hoverChip = -1;
            Target = IntPtr.Zero;
            if (Visible) Hide();
        }

        public bool ContainsScreenPoint(Point p)
        {
            return Visible && Bounds.Contains(p);
        }

        /// <summary>目标窗口状态变了（比如热键改的），同步到界面上。</summary>
        public void SyncFrom(int percent, bool topMost, bool clickThrough, bool autoPressing, string autoKeyName,
                             bool autoHide, bool keepAlive, bool movedAway)
        {
            bool needRepaint = Percent != percent
                               || IsTopMost != topMost
                               || IsClickThrough != clickThrough
                               || IsAutoPressing != autoPressing
                               || IsAutoHide != autoHide
                               || IsKeepAlive != keepAlive
                               || IsMovedAway != movedAway
                               || AutoKeyName != (autoKeyName ?? "");
            Percent = percent;
            IsTopMost = topMost;
            IsClickThrough = clickThrough;
            IsAutoPressing = autoPressing;
            IsAutoHide = autoHide;
            IsKeepAlive = keepAlive;
            IsMovedAway = movedAway;
            AutoKeyName = autoKeyName ?? "";
            if (needRepaint && Visible) Invalidate();
        }

        private void RaiseTopMost()
        {
            if (!IsHandleCreated) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // ------------------------------------------------------------------
        //  尺寸 / 布局
        // ------------------------------------------------------------------

        private void BuildFonts()
        {
            float s = (float)_scale;
            _fontTitle?.Dispose();
            _fontValue?.Dispose();
            _fontChip?.Dispose();
            _fontSmall?.Dispose();

            _fontTitle = new Font("Microsoft YaHei UI", 13f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontValue = new Font("Microsoft YaHei UI", 13f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            _fontChip = new Font("Microsoft YaHei UI", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontSmall = new Font("Microsoft YaHei UI", 11f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private void ApplyBounds()
        {
            float s = (float)_scale;
            int w = (int)Math.Round((_expanded ? PanelW : PillW) * s);
            int h = (int)Math.Round((_expanded ? PanelH : PillH) * s);
            int x = _anchorRight - w;
            int y = _anchorTop;

            Screen scr = Screen.FromPoint(new Point(_anchorRight, _anchorTop));
            Rectangle wa = scr.WorkingArea;
            if (x + w > wa.Right - 4) x = wa.Right - 4 - w;
            if (x < wa.Left + 4) x = wa.Left + 4;
            if (y < wa.Top + 4) y = wa.Top + 4;
            if (y + h > wa.Bottom - 4) y = wa.Bottom - 4 - h;

            SetBounds(x, y, w, h);
            LayoutHitRects();
        }

        private void LayoutHitRects()
        {
            float s = (float)_scale;
            int k(int v) => (int)Math.Round(v * s);

            _rcSliderHit = new Rectangle(k(SliderX - 8), k(SliderY - 10), k(SliderW + 16), k(SliderH + 20));

            int px = k(PresetX0);
            for (int i = 0; i < Presets.Length; i++)
            {
                _rcPresets[i] = new Rectangle(px, k(PresetY), k(PresetW), k(PresetH));
                px += k(PresetW) + k(PresetGap);
            }

            int bx = k(BottomX0);
            _rcPin = new Rectangle(bx, k(BottomY), k(BottomW), k(BottomH));
            bx += k(BottomW) + k(BottomGap);
            _rcThrough = new Rectangle(bx, k(BottomY), k(BottomW), k(BottomH));
            bx += k(BottomW) + k(BottomGap);
            _rcAuto = new Rectangle(bx, k(BottomY), k(BottomW), k(BottomH));
            bx += k(BottomW) + k(BottomGap);
            _rcAutoHide = new Rectangle(bx, k(BottomY), k(BottomW), k(BottomH));
            bx += k(BottomW) + k(BottomGap);
            _rcKeepAlive = new Rectangle(bx, k(BottomY), k(BottomW), k(BottomH));
            bx += k(BottomW) + k(BottomGap);
            _rcReset = new Rectangle(bx, k(BottomY), k(BottomW), k(BottomH));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            using (GraphicsPath p = RoundedPath(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), Radius * (float)_scale))
            {
                Region old = Region;
                Region = new Region(p);
                old?.Dispose();
            }
        }

        private void Expand()
        {
            if (_expanded) return;
            _expanded = true;
            _hoverChip = -1;
            ApplyBounds();
            RaiseTopMost();
            Invalidate();
            Log.Info("Expand -> " + Bounds);
        }

        private void Collapse()
        {
            if (!_expanded) return;
            _expanded = false;
            _dragging = false;
            _hoverChip = -1;
            ApplyBounds();
            Invalidate();
        }

        // ------------------------------------------------------------------
        //  绘制
        // ------------------------------------------------------------------

        private static GraphicsPath RoundedPath(RectangleF r, float radius)
        {
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BgColor);

            if (_expanded) PaintPanel(g);
            else PaintPill(g);
        }

        private void PaintPill(Graphics g)
        {
            float s = (float)_scale;
            int w = ClientSize.Width;
            int h = ClientSize.Height;

            // 连按进行中时整个胶囊泛绿，一眼能看出"它正在自己按"
            Color pillBg = IsAutoPressing ? Color.FromArgb(24, 52, 40) : BgColor;
            Color pillBorder = IsAutoPressing ? AccentGreen : BorderColor;

            using (GraphicsPath path = RoundedPath(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), Radius * s))
            {
                using (var b = new SolidBrush(pillBg)) g.FillPath(b, path);
                using (var p = new Pen(pillBorder, IsAutoPressing ? 1.4f : 1f)) g.DrawPath(p, path);
            }

            Color c = Percent < 100 ? AccentColor : MutedColor;

            // 半透明图标：一个圆环，按透明度从下往上填充
            float cx = 13f * s;
            float cy = h / 2f;
            float r = 5.4f * s;
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(cx - r, cy - r, r * 2f, r * 2f);
                using (var p = new Pen(c, 1.4f * s)) g.DrawPath(p, gp);

                if (Percent > 0)
                {
                    Region old = g.Clip;
                    g.SetClip(gp, CombineMode.Intersect);
                    float top = cy + r - (r * 2f) * (Percent / 100f);
                    using (var b = new SolidBrush(c)) g.FillRectangle(b, cx - r, top, r * 2f, r * 2f);
                    g.Clip = old;
                }
            }

            // 右侧：100% 时显示下拉箭头，否则显示百分比数字
            if (Percent >= 100)
            {
                using (var p = new Pen(MutedColor, 1.5f * s))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;
                    g.DrawLines(p, new[]
                    {
                        new PointF(25f * s, 9f * s),
                        new PointF(28.5f * s, 12.5f * s),
                        new PointF(32f * s, 9f * s),
                    });
                }
            }
            else
            {
                string txt = Percent.ToString();
                SizeF sz = g.MeasureString(txt, _fontSmall);
                using (var b = new SolidBrush(AccentColor))
                {
                    g.DrawString(txt, _fontSmall, b, w - 6f * s - sz.Width + 1f, (h - sz.Height) / 2f + 0.5f);
                }
            }
        }

        private void PaintPanel(Graphics g)
        {
            float s = (float)_scale;
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int k(int v) => (int)Math.Round(v * s);

            using (GraphicsPath path = RoundedPath(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), Radius * s))
            {
                using (var b = new SolidBrush(BgColor)) g.FillPath(b, path);
                using (var p = new Pen(BorderColor, 1f)) g.DrawPath(p, path);
            }

            // 标题行
            TextRenderer.DrawText(g, "透明度", _fontTitle, new Point(k(Pad), k(HeaderY)), TextColor,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            string val = Percent + "%";
            Size vsz = TextRenderer.MeasureText(g, val, _fontValue, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, val, _fontValue,
                new Point(w - k(Pad) - vsz.Width, k(HeaderY)), Percent < 100 ? AccentColor : TextColor,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            // 滑块
            var track = new RectangleF(k(SliderX), k(SliderY), k(SliderW), k(SliderH));
            using (GraphicsPath tp = RoundedPath(track, k(SliderH) / 2f))
            using (var b = new SolidBrush(TrackColor)) g.FillPath(b, tp);

            float ratio = (Percent - Fx.MinPercent) / (float)(Fx.MaxPercent - Fx.MinPercent);
            float knobX = track.X + track.Width * ratio;
            float knobY = track.Y + track.Height / 2f;

            if (knobX > track.X + 1f)
            {
                var filled = new RectangleF(track.X, track.Y, knobX - track.X, track.Height);
                using (GraphicsPath fp = RoundedPath(filled, k(SliderH) / 2f))
                using (var b = new SolidBrush(AccentColor)) g.FillPath(b, fp);
            }

            float kr = KnobR * s;
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, knobX - kr, knobY - kr, kr * 2f, kr * 2f);
            using (var p = new Pen(Color.FromArgb(70, 0, 0, 0), 1f))
                g.DrawEllipse(p, knobX - kr, knobY - kr, kr * 2f, kr * 2f);

            // 预设
            for (int i = 0; i < Presets.Length; i++)
                DrawChip(g, _rcPresets[i], Presets[i] + "%", Percent == Presets[i], _hoverChip == i);

            // 底排
            DrawChip(g, _rcPin, IsTopMost ? "已置顶" : "置顶", IsTopMost, _hoverChip == ChipPin);
            DrawChip(g, _rcThrough, "穿透", IsClickThrough, _hoverChip == ChipThrough);
            DrawChip(g, _rcAuto, AutoChipText(), IsAutoPressing, _hoverChip == ChipAuto, AccentGreen);
            DrawChip(g, _rcAutoHide, IsAutoHide ? "已隐藏" : "隐藏", IsAutoHide, _hoverChip == ChipAutoHide, AccentColor);
            DrawChip(g, _rcKeepAlive, KeepAliveChipText(), IsKeepAlive, _hoverChip == ChipKeepAlive, AccentGreen);
            DrawChip(g, _rcReset, "重置", false, _hoverChip == ChipReset);
        }

        /// <summary>「连按」按钮上的文字：没开时写"连按"，开了就把键名亮出来。</summary>
        private string AutoChipText()
        {
            if (!IsAutoPressing) return "连按";
            string k = AutoKeyName;
            if (string.IsNullOrEmpty(k)) return "连按中";
            return "连按" + k;
        }

        private string KeepAliveChipText()
        {
            if (IsMovedAway) return "已挪走";
            return IsKeepAlive ? "保活中" : "保活";
        }

        private void DrawChip(Graphics g, Rectangle rc, string text, bool active, bool hover, Color? activeColor = null)
        {
            float s = (float)_scale;
            Color on = activeColor ?? AccentColor;
            Color bg = active ? on : (hover ? ChipHover : ChipBg);
            using (GraphicsPath p = RoundedPath(new RectangleF(rc.X, rc.Y, rc.Width, rc.Height), 6f * s))
            using (var b = new SolidBrush(bg)) g.FillPath(b, p);

            Color fg = active ? ChipTextOn : TextColor;
            Size sz = TextRenderer.MeasureText(g, text, _fontChip, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            var pt = new Point(rc.X + (rc.Width - sz.Width) / 2, rc.Y + (rc.Height - sz.Height) / 2);
            TextRenderer.DrawText(g, text, _fontChip, pt, fg,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        // ------------------------------------------------------------------
        //  鼠标交互
        // ------------------------------------------------------------------

        private int HitChip(Point p)
        {
            for (int i = 0; i < Presets.Length; i++)
                if (_rcPresets[i].Contains(p)) return i;
            if (_rcPin.Contains(p)) return ChipPin;
            if (_rcThrough.Contains(p)) return ChipThrough;
            if (_rcAuto.Contains(p)) return ChipAuto;
            if (_rcAutoHide.Contains(p)) return ChipAutoHide;
            if (_rcKeepAlive.Contains(p)) return ChipKeepAlive;
            if (_rcReset.Contains(p)) return ChipReset;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _collapseTimer.Stop();

            if (!_expanded)
            {
                Expand();
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                // 右键「连按」= 打开设置
                if (HitChip(e.Location) == ChipAuto) AutoPressConfigure?.Invoke();
                // 右键「保活」= 挪到屏幕外 / 挪回来
                if (HitChip(e.Location) == ChipKeepAlive) KeepAliveMoveRequested?.Invoke();
                return;
            }

            if (_rcSliderHit.Contains(e.Location))
            {
                _dragging = true;
                UpdateFromX(e.X);
                return;
            }

            int chip = HitChip(e.Location);
            switch (chip)
            {
                case 0: case 1: case 2: case 3: case 4:
                    SetPercent(Presets[chip]);
                    break;
                case ChipPin:
                    TopMostToggled?.Invoke(!IsTopMost);
                    break;
                case ChipThrough:
                    ClickThroughToggled?.Invoke(!IsClickThrough);
                    break;
                case ChipAuto:
                    AutoPressToggled?.Invoke();
                    break;
                case ChipAutoHide:
                    AutoHideToggled?.Invoke(!IsAutoHide);
                    break;
                case ChipKeepAlive:
                    KeepAliveToggled?.Invoke(!IsKeepAlive);
                    break;
                case ChipReset:
                    ResetRequested?.Invoke();
                    break;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _collapseTimer.Stop();

            if (_dragging)
            {
                UpdateFromX(e.X);
                return;
            }

            int chip = _expanded ? HitChip(e.Location) : -1;
            if (chip != _hoverChip)
            {
                _hoverChip = chip;
                if (_expanded) Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Log.Info("MouseEnter expanded=" + _expanded + " cursor=" + Cursor.Position + " bounds=" + Bounds);
            _collapseTimer.Stop();
            if (!_expanded) _expandTimer.Start();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Log.Info("MouseLeave expanded=" + _expanded + " cursor=" + Cursor.Position + " bounds=" + Bounds);
            _expandTimer.Stop();
            if (_hoverChip != -1)
            {
                _hoverChip = -1;
                if (_expanded) Invalidate();
            }
            if (!_dragging) _collapseTimer.Start();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_expanded) return;
            SetPercent(Percent + (e.Delta > 0 ? 5 : -5));
        }

        private void UpdateFromX(int x)
        {
            float s = (float)_scale;
            float left = SliderX * s;
            float width = SliderW * s;
            float t = (x - left) / width;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            int v = Fx.MinPercent + (int)Math.Round(t * (Fx.MaxPercent - Fx.MinPercent));
            SetPercent(v);
        }

        private void SetPercent(int v)
        {
            v = Fx.Clamp(v);
            if (v == Percent) return;
            Percent = v;
            Invalidate();
            OpacityChanged?.Invoke(v);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _expandTimer.Dispose();
                _collapseTimer.Dispose();
                _fontTitle?.Dispose();
                _fontValue?.Dispose();
                _fontChip?.Dispose();
                _fontSmall?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
