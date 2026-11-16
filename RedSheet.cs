using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;
using System.Drawing.Imaging;
using System.Threading.Tasks;

sealed class RedSheet : Form
{
#if RECORDING_BUILD
    static readonly bool RecordingBuild = true;
#else
    static readonly bool RecordingBuild = false;
#endif
    readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), RecordingBuild ? "KeyboardRedSheetRecording" : "KeyboardRedSheet", "settings.xml");
    bool recordingMode, recordingPending;
    Bitmap recordingSource;
    Point recordingOrigin;
    readonly NotifyIcon tray;
    bool testing;
    bool autoFilter;
    bool picking;
    Color textColor = Color.Red;
    float strength = 1f;
    Bitmap filtered;
    Point filteredOrigin;
    Color filteredColor;
    float filteredStrength;
    int capturedAt;
    bool redSheetVisible = true;
    Color sheetTint = Color.FromArgb(255, 153, 153);
    Color filteredTint;
    bool captureInProgress, closing;
    Action beforeFilterForTest;
    int lastMove;
    readonly Timer refresh = new Timer { Interval = 120 };
    Point dragStart, windowStart;
    bool dragging;
    bool savedFilter = true;
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    public RedSheet(bool selfTest)
    {
        testing = selfTest;
        Text = "赤シート（矢印で移動・Cで色選択）";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(200, 200, 640, 240);
        MinimumSize = new Size(80, 40);
        BackColor = Color.Red;
        Opacity = 0.55;
        TopMost = true;
        DoubleBuffered = true;
        if (!testing) LoadSettings();
        var menu = new ContextMenuStrip();
        if (RecordingBuild) {
            Text = "赤シート 撮影用（Ctrl+Shift+Rで撮影切替）";
            var recordingItem = new ToolStripMenuItem("撮影モード（Ctrl+Shift+R）");
            recordingItem.Click += delegate { ToggleRecording(); };
            menu.Items.Add(recordingItem);
            menu.Opening += delegate { recordingItem.Checked = recordingMode || recordingPending; };
        }
        menu.Items.Add("色を選ぶ（C / ダブルクリック）", null, delegate { PickColor(); });
        menu.Items.Add("画面から隠す文字色を拾う（3秒後）", null, delegate { PickScreenColor(); });
        var autoItem = new ToolStripMenuItem("文字を自動で隠す（F）");
        autoItem.Click += delegate { SetFilter(!autoFilter); };
        menu.Items.Add(autoItem);
        var redItem = new ToolStripMenuItem("赤いシートを表示（R）");
        redItem.Click += delegate { ToggleRedSheet(); };
        menu.Items.Add(redItem);
        menu.Items.Add("シートの背景色を選ぶ", null, delegate {
            picking = true;
            using (var dialog = new ColorDialog { Color = sheetTint, FullOpen = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) { sheetTint = dialog.Color; redSheetVisible = true; }
            picking = false; CaptureFiltered(); Invalidate();
        });
        menu.Opening += delegate { autoItem.Checked = autoFilter; redItem.Checked = redSheetVisible; };
        var opacityMenu = new ToolStripMenuItem("不透明度");
        foreach (int percent in new int[] { 20, 35, 50, 55, 65, 80, 100 })
        {
            int value = percent;
            var item = new ToolStripMenuItem(percent + "%");
            item.Click += delegate {
                if (recordingMode || recordingPending) { MessageBox.Show(this, "撮影モードを解除してから変更してください。", "赤シート 撮影用"); return; }
                SetFilter(false); Opacity = value / 100.0; Invalidate();
            };
            opacityMenu.DropDownItems.Add(item);
        }
        opacityMenu.DropDownOpening += delegate {
            foreach (ToolStripMenuItem item in opacityMenu.DropDownItems)
                item.Checked = item.Text == ((int)Math.Round(Opacity * 100)) + "%";
        };
        menu.Items.Add(opacityMenu);
        menu.Items.Add("操作方法", null, delegate {
            MessageBox.Show(this, "シートをクリックして選択後、矢印キーで移動します。\n\n矢印：10px移動（長押し可）\nShift + 矢印：1px移動\nCtrl + 矢印：50px移動\nF：自動モードの切替\nC / ダブルクリック：隠す文字色を選択\n右クリック→画面から隠す文字色を拾う：3秒以内に文字の中央へマウスを移動\n+ / -：自動モードの効き具合を調整\n通常モードでは不透明度を調整\nドラッグ：移動 ／ 端をドラッグ：サイズ変更\nAlt + F4：終了\n\nPDFを操作するときはPDFをクリックしてください。\nタスクトレイのアイコンをダブルクリックすると、シートを選択できます。", "赤シートの使い方");
        });
        menu.Items.Add("終了", null, delegate { Close(); });
        ContextMenuStrip = menu;
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "赤シート：ダブルクリックで選択", ContextMenuStrip = menu, Visible = !testing };
        tray.DoubleClick += delegate { Show(); Activate(); };
        MouseDown += delegate(object sender, MouseEventArgs e) {
            if (e.Button == MouseButtons.Left && e.Clicks == 1) {
                Activate();
                dragStart = Cursor.Position; windowStart = Location; dragging = false;
            }
        };
        MouseMove += delegate(object sender, MouseEventArgs e) {
            if (e.Button != MouseButtons.Left) return;
            Point delta = new Point(Cursor.Position.X - dragStart.X, Cursor.Position.Y - dragStart.Y);
            if (!dragging && Math.Abs(delta.X) < SystemInformation.DragSize.Width / 2 && Math.Abs(delta.Y) < SystemInformation.DragSize.Height / 2) return;
            dragging = true;
            Location = new Point(windowStart.X + delta.X, windowStart.Y + delta.Y);
        };
        MouseDoubleClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) PickColor(); };
        Activated += delegate { Invalidate(); };
        Deactivate += delegate { Invalidate(); };
        refresh.Tick += delegate {
            refresh.Interval = unchecked(Environment.TickCount - lastMove) < 150 ? 33 : 120;
            CaptureFiltered();
        };
        Shown += delegate { if (!testing && savedFilter) SetFilter(true); };
        LocationChanged += delegate { ScheduleMovementFrame(); };
        SizeChanged += delegate { ScheduleMovementFrame(); };
        FormClosed += delegate { closing = true; refresh.Dispose(); if (filtered != null) filtered.Dispose(); if (recordingSource != null) recordingSource.Dispose(); if (!testing) SaveSettings(); tray.Dispose(); };
    }

    void ScheduleMovementFrame()
    {
        if (!autoFilter) return;
        lastMove = Environment.TickCount;
        refresh.Interval = 33;
        Invalidate();
    }

    Color CurrentTint { get { return redSheetVisible ? sheetTint : Color.White; } }

    void ToggleRedSheet()
    {
        redSheetVisible = !redSheetVisible;
        CaptureFiltered(); Invalidate();
    }

    void PickColor()
    {
        picking = true;
        using (var dialog = new ColorDialog { Color = autoFilter ? textColor : BackColor, FullOpen = true, AnyColor = true })
            if (dialog.ShowDialog(this) == DialogResult.OK) {
                if (autoFilter) {
                    if (IsTextColor(dialog.Color)) textColor = dialog.Color;
                    else MessageBox.Show(this, "隠す文字色には赤などの色を選んでください。黒・白・灰色は適しません。", "赤シート");
                }
                else BackColor = dialog.Color;
            }
        picking = false;
        Activate();
    }

    bool EnableCaptureExclusion()
    {
        uint affinity;
        return SetWindowDisplayAffinity(Handle, 0x11) && GetWindowDisplayAffinity(Handle, out affinity) && affinity == 0x11;
    }

    void ToggleRecording()
    {
        if (recordingMode) {
            if (!EnableCaptureExclusion()) { MessageBox.Show(this, "録画からの除外に戻せませんでした。アプリを終了して起動し直してください。", "赤シート 撮影用"); return; }
            recordingMode = false;
            recordingSource.Dispose(); recordingSource = null;
            tray.Text = "赤シート 撮影用：通常モード";
            CaptureFiltered();
        } else if (recordingPending) {
            recordingPending = false;
        } else {
            if (!autoFilter) SetFilter(true);
            if (!autoFilter || !EnableCaptureExclusion()) return;
            recordingPending = true;
            CaptureFiltered();
        }
    }

    static bool IsTextColor(Color color)
    {
        int max = Math.Max(color.R, Math.Max(color.G, color.B));
        int min = Math.Min(color.R, Math.Min(color.G, color.B));
        return max >= 30 && max - min >= 20;
    }

    void SetFilter(bool enabled)
    {
        if (recordingMode || recordingPending) { MessageBox.Show(this, "撮影モードを解除してから切り替えてください。", "赤シート 撮影用"); return; }
        if (enabled && !EnableCaptureExclusion()) {
            MessageBox.Show(this, "自動モードにはWindows 10 バージョン2004以降の画面キャプチャ除外機能が必要です。", "赤シート");
            return;
        }
        autoFilter = enabled;
        Opacity = enabled ? 1.0 : 0.55;
        refresh.Enabled = enabled;
        if (enabled) CaptureFiltered();
        Invalidate();
    }

    void PickScreenColor()
    {
        if (recordingMode || recordingPending) { MessageBox.Show(this, "画面から色を拾うときは、Ctrl+Shift+Rで撮影モードを解除してください。", "赤シート 撮影用"); return; }
        if (!EnableCaptureExclusion()) { MessageBox.Show(this, "画面からの色取得に対応していないWindowsです。Cキーの色選択を利用してください。", "赤シート"); return; }
        picking = true;
        tray.Text = "3秒以内に隠したい文字の中央へマウスを移動";
        var delay = new Timer { Interval = 3000 };
        delay.Tick += delegate {
            delay.Stop(); delay.Dispose();
            try {
                using (var pixel = new Bitmap(1, 1)) {
                    using (var graphics = Graphics.FromImage(pixel)) graphics.CopyFromScreen(Cursor.Position, Point.Empty, new Size(1, 1));
                    Color selected = pixel.GetPixel(0, 0);
                    if (!IsTextColor(selected))
                        MessageBox.Show(this, "色のある文字の中央を選んでください。黒・白・灰色は隠す色に適しません。", "赤シート");
                    else { textColor = selected; strength = 1f; SetFilter(true); }
                }
            } catch (System.ComponentModel.Win32Exception e) { MessageBox.Show(this, e.Message, "画面の色を取得できませんでした"); }
            finally { picking = false; tray.Text = "赤シート：ダブルクリックで選択"; Activate(); }
        };
        delay.Start();
    }

    static Bitmap Filter(Bitmap source, Color selected, float amount)
    {
        return Filter(source, selected, amount, Color.White);
    }

    static unsafe Bitmap Filter(Bitmap source, Color selected, float amount, Color tint)
    {
        int selectedMin = Math.Min(selected.R, Math.Min(selected.G, selected.B));
        float selectedChroma = Math.Max(1, Math.Max(selected.R, Math.Max(selected.G, selected.B)) - selectedMin);
        float targetR = (selected.R - selectedMin) / selectedChroma;
        float targetG = (selected.G - selectedMin) / selectedChroma;
        float targetB = (selected.B - selectedMin) / selectedChroma;
        var output = (Bitmap)source.Clone();
        int width = source.Width, height = source.Height;
        var rect = new Rectangle(0, 0, width, height);
        BitmapData result = output.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try {
            for (int y = 0; y < height; y++) {
                byte* row = (byte*)result.Scan0 + y * result.Stride;
                for (int x = 0; x < width * 4; x += 4) {
                    int blue = row[x], green = row[x+1], red = row[x+2];
                    int min = Math.Min(red, Math.Min(green, blue));
                    int chroma = Math.Max(red, Math.Max(green, blue)) - min;
                    // Compare hue independently of brightness. Preserve neutral text exactly.
                    float weight = 0;
                    if (chroma >= 3) {
                        float distance = Math.Max(Math.Abs((red-min)/(float)chroma-targetR), Math.Max(Math.Abs((green-min)/(float)chroma-targetG), Math.Abs((blue-min)/(float)chroma-targetB)));
                        weight = amount * Math.Max(0f, Math.Min(1f, (0.45f-distance)/0.25f));
                    }
                    row[x] = (byte)Math.Round((blue + (255-blue)*weight) * tint.B / 255f);
                    row[x+1] = (byte)Math.Round((green + (255-green)*weight) * tint.G / 255f);
                    row[x+2] = (byte)Math.Round((red + (255-red)*weight) * tint.R / 255f);
                }
            }
        } finally {
            output.UnlockBits(result);
        }
        return output;
    }

    void CaptureFiltered()
    {
        if (!autoFilter || picking || closing || captureInProgress || !Visible || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        Bitmap capture = null;
        try {
            var view = new Rectangle(PointToScreen(Point.Empty), ClientSize);
            int now = Environment.TickCount;
            bool preparing = recordingPending;
            bool frozen = recordingMode;
            if (frozen && filteredColor == textColor && filteredStrength == strength && filteredTint == CurrentTint) return;
            if (!preparing && !frozen && unchecked(now - lastMove) < 150 && CanReuseCache(view, now)) return;
            var area = view;
            area.Inflate(256, 384);
            area = Rectangle.Intersect(area, SystemInformation.VirtualScreen);
            if (preparing) area = SystemInformation.VirtualScreen;
            if (area.IsEmpty) return;
            Point origin = frozen ? recordingOrigin : area.Location;
            if (frozen) capture = (Bitmap)recordingSource.Clone();
            else {
              capture = new Bitmap(area.Width, area.Height);
              using (var graphics = Graphics.FromImage(capture)) {
                graphics.Clear(Color.White);
                foreach (Screen screen in Screen.AllScreens) {
                    Rectangle part = Rectangle.Intersect(area, screen.Bounds);
                    if (part.Width > 0 && part.Height > 0)
                        graphics.CopyFromScreen(part.Location, new Point(part.X - origin.X, part.Y - origin.Y), part.Size);
                }
              }
            }
            Color selected = textColor;
            float amount = strength;
            Color tint = CurrentTint;
            Bitmap ownedCapture = capture;
            Action beforeFilter = beforeFilterForTest;
            captureInProgress = true;
            Task.Factory.StartNew(delegate {
                try {
                    if (beforeFilter != null) beforeFilter();
                    return Filter(ownedCapture, selected, amount, tint);
                } finally { if (!preparing) ownedCapture.Dispose(); }
            }).ContinueWith(task => {
                captureInProgress = false;
                if (task.IsFaulted) {
                    if (preparing) ownedCapture.Dispose();
                    recordingPending = false;
                    Exception error = task.Exception.GetBaseException();
                    if (!closing) { SetFilter(false); MessageBox.Show(this, error.Message, "色変換に失敗しました"); }
                    return;
                }
                Bitmap next = task.Result;
                if (closing || !autoFilter || picking || textColor != selected || strength != amount || CurrentTint != tint || (frozen && !recordingMode) || (preparing && !recordingPending)) {
                    next.Dispose(); if (preparing) ownedCapture.Dispose(); return;
                }
                if (preparing) {
                    recordingPending = false;
                    if (!SetWindowDisplayAffinity(Handle, 0)) {
                        ownedCapture.Dispose(); next.Dispose();
                        MessageBox.Show(this, "録画に映る状態へ切り替えられませんでした。", "赤シート 撮影用"); return;
                    }
                    recordingSource = ownedCapture; recordingOrigin = origin; recordingMode = true;
                    tray.Text = "赤シート 撮影中：画面は固定・Ctrl+Shift+Rで解除";
                }
                Bitmap previous = filtered;
                filtered = next; filteredOrigin = origin;
                filteredColor = selected; filteredStrength = amount; capturedAt = now;
                filteredTint = tint;
                if (previous != null) previous.Dispose();
                Invalidate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
            capture = null;
        } catch (System.ComponentModel.Win32Exception) { SetFilter(false); }
        finally { if (capture != null) capture.Dispose(); }
    }

    bool CanReuseCache(Rectangle view, int now)
    {
        if (filtered == null || filteredColor != textColor || filteredStrength != strength || filteredTint != CurrentTint || unchecked(now - capturedAt) >= 500) return false;
        // Refresh before reaching the edge, keeping room for the next movement.
        view.Inflate(64, 64);
        view = Rectangle.Intersect(view, SystemInformation.VirtualScreen);
        return new Rectangle(filteredOrigin, filtered.Size).Contains(view);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        Keys modifiers = keyData & Keys.Modifiers;
        if (RecordingBuild && keyData == (Keys.Control | Keys.Shift | Keys.R)) { ToggleRecording(); return true; }
        if (modifiers == Keys.None || modifiers == Keys.Shift || modifiers == Keys.Control)
        {
            int step = modifiers == Keys.Shift ? 1 : modifiers == Keys.Control ? 50 : 10;
            int dx = key == Keys.Left ? -step : key == Keys.Right ? step : 0;
            int dy = key == Keys.Up ? -step : key == Keys.Down ? step : 0;
            if (dx != 0 || dy != 0) {
                Rectangle target = Bounds;
                target.Offset(dx, dy);
                // Keep a reachable portion on at least one monitor, including negative coordinates.
                foreach (Screen screen in Screen.AllScreens) {
                    Rectangle visible = Rectangle.Intersect(target, screen.WorkingArea);
                    if (visible.Width >= 24 && visible.Height >= 24) { Location = target.Location; break; }
                }
                return true;
            }
        }
        if (modifiers == Keys.None) {
            if (key == Keys.R) { ToggleRedSheet(); return true; }
            if (key == Keys.F) { SetFilter(!autoFilter); return true; }
            if (key == Keys.C) { PickColor(); return true; }
            if (autoFilter && (key == Keys.Add || key == Keys.Oemplus || key == Keys.Subtract || key == Keys.OemMinus)) {
                strength = Math.Max(0f, Math.Min(1f, strength + (key == Keys.Add || key == Keys.Oemplus ? 0.05f : -0.05f)));
                CaptureFiltered(); return true;
            }
            if (key == Keys.Add || key == Keys.Oemplus) { Opacity = Math.Min(1, Opacity + 0.05); return true; }
            if (key == Keys.Subtract || key == Keys.OemMinus) { Opacity = Math.Max(0.1, Opacity - 0.05); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void WndProc(ref Message m)
    {
        if (testing && m.Msg == 0x8001) { ProcessCmdKey(ref m, (Keys)m.WParam.ToInt32()); return; }
        base.WndProc(ref m);
        if (m.Msg != 0x84 || (int)m.Result != 1) return;
        long position = m.LParam.ToInt64();
        Point p = PointToClient(new Point((short)(position & 0xffff), (short)((position >> 16) & 0xffff)));
        bool left = p.X < 8, right = p.X >= ClientSize.Width - 8;
        bool top = p.Y < 8, bottom = p.Y >= ClientSize.Height - 8;
        int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 1;
        m.Result = new IntPtr(hit);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (autoFilter && filtered != null) {
            Point origin = PointToScreen(Point.Empty);
            e.Graphics.DrawImageUnscaled(filtered, filteredOrigin.X - origin.X, filteredOrigin.Y - origin.Y);
        }
        using (var pen = new Pen(ContainsFocus ? Color.White : Color.FromArgb(100, 0, 0, 0), 2))
            e.Graphics.DrawRectangle(pen, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (autoFilter) e.Graphics.Clear(CurrentTint);
        else base.OnPaintBackground(e);
    }

    void LoadSettings()
    {
        try {
            if (!File.Exists(settingsPath)) return;
            XElement s = XElement.Load(settingsPath);
            BackColor = Color.FromArgb((int)s.Attribute("color"));
            if (s.Attribute("textColor") != null) textColor = Color.FromArgb((int)s.Attribute("textColor"));
            if (s.Attribute("redSheetVisible") != null) redSheetVisible = (bool)s.Attribute("redSheetVisible");
            if (s.Attribute("sheetTint") != null) sheetTint = Color.FromArgb((int)s.Attribute("sheetTint"));
            if (s.Attribute("autoFilter") != null) savedFilter = (bool)s.Attribute("autoFilter");
            if (s.Attribute("strength") != null) strength = Math.Max(0f, Math.Min(1f, (float)s.Attribute("strength")));
            Opacity = Math.Max(0.1, Math.Min(1, (double)s.Attribute("opacity")));
            var bounds = new Rectangle((int)s.Attribute("x"), (int)s.Attribute("y"), Math.Max(80, (int)s.Attribute("width")), Math.Max(40, (int)s.Attribute("height")));
            foreach (Screen screen in Screen.AllScreens)
                if (Rectangle.Intersect(bounds, screen.WorkingArea).Width >= 24 && Rectangle.Intersect(bounds, screen.WorkingArea).Height >= 24) { Bounds = bounds; break; }
        } catch (Exception e) {
            if (!(e is IOException || e is System.Xml.XmlException || e is ArgumentException || e is InvalidOperationException || e is FormatException || e is OverflowException || e is UnauthorizedAccessException)) throw;
        }
    }

    void SaveSettings()
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            new XElement("sheet", new XAttribute("color", BackColor.ToArgb()), new XAttribute("textColor", textColor.ToArgb()), new XAttribute("redSheetVisible", redSheetVisible), new XAttribute("sheetTint", sheetTint.ToArgb()), new XAttribute("autoFilter", autoFilter), new XAttribute("strength", strength), new XAttribute("opacity", Opacity), new XAttribute("x", Left), new XAttribute("y", Top), new XAttribute("width", Width), new XAttribute("height", Height)).Save(settingsPath);
        } catch (Exception e) {
            if (!(e is IOException || e is UnauthorizedAccessException)) throw;
            MessageBox.Show("設定を保存できませんでした。\n" + e.Message, "赤シート");
        }
    }

    public void RunSelfTest()
    {
        redSheetVisible = false;
        Point initial = Location;
        int stage = 0;
        Form backdrop = null;
        int markerY = Height + 4;
        var filterGate = new System.Threading.ManualResetEvent(false);
        var timer = new Timer { Interval = 250 };
        timer.Tick += delegate {
            try {
                if (stage == 0) PostMessage(Handle, 0x8001, new IntPtr((int)Keys.Down), IntPtr.Zero);
                if (stage == 1) {
                    if (Top != initial.Y + 10 || Left != initial.X) throw new Exception("Down did not move the real window by 10px");
                    PostMessage(Handle, 0x8001, new IntPtr((int)Keys.Up), IntPtr.Zero);
                }
                if (stage == 2) {
                    if (Location != initial) throw new Exception("Up did not restore the real window position");
                    Message msg = Message.Create(Handle, 0x100, IntPtr.Zero, IntPtr.Zero);
                    ProcessCmdKey(ref msg, Keys.Shift | Keys.Right);
                    if (Left != initial.X + 1) throw new Exception("Fine movement failed");
                    ProcessCmdKey(ref msg, Keys.Control | Keys.Left);
                    if (Left != initial.X - 49) throw new Exception("Fast movement failed");
                    double opacity = Opacity;
                    ProcessCmdKey(ref msg, Keys.Add);
                    if (Math.Abs(Opacity - opacity - 0.05) > 0.01) throw new Exception("Opacity failed");
                    using (var sample = new Bitmap(3, 1)) {
                        sample.SetPixel(0, 0, Color.Red); sample.SetPixel(1, 0, Color.Black); sample.SetPixel(2, 0, Color.White);
                        using (var result = Filter(sample, Color.Red, 1f)) {
                            if (result.GetPixel(0,0).R < 250 || result.GetPixel(0,0).G < 250 || result.GetPixel(1,0).R > 5 || result.GetPixel(2,0).R < 250) throw new Exception("Color conversion failed");
                        }
                        using (var result = Filter(sample, Color.Red, 0.5f)) {
                            if (result.GetPixel(0,0).G < 120 || result.GetPixel(0,0).G > 135 || result.GetPixel(1,0).R > 5) throw new Exception("Gradual strength adjustment failed");
                        }
                    }
                    using (var sample = new Bitmap(6, 1)) {
                        sample.SetPixel(0,0,Color.FromArgb(170,35,30));
                        sample.SetPixel(1,0,Color.FromArgb(255,200,200));
                        sample.SetPixel(2,0,Color.FromArgb(100,100,100));
                        sample.SetPixel(3,0,Color.Black);
                        sample.SetPixel(4,0,Color.Blue);
                        sample.SetPixel(5,0,Color.White);
                        using (var result = Filter(sample, Color.FromArgb(200,30,25), 1f)) {
                            for (int i = 0; i < 2; i++)
                                if (result.GetPixel(i,0).ToArgb() != Color.White.ToArgb()) throw new Exception("Picked red left gray text");
                            for (int i = 2; i < 6; i++)
                                if (result.GetPixel(i,0).ToArgb() != sample.GetPixel(i,0).ToArgb()) throw new Exception("Unrelated text color changed");
                        }
                    }
                    Location = initial;
                    backdrop = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = Bounds, BackColor = Color.Red, ShowInTaskbar = false, TopMost = true };
                    backdrop.Height += 160;
                    backdrop.Paint += delegate(object sender, PaintEventArgs e2) {
                        e2.Graphics.FillRectangle(Brushes.Black, 20, 20, 50, 50);
                        e2.Graphics.FillRectangle(Brushes.Blue, 100, markerY, 40, 30);
                    };
                    backdrop.Show(); BringToFront(); Activate();
                    SetFilter(true);
                    if (!autoFilter) throw new Exception("Capture exclusion unavailable");
                }
                if (stage == 4) {
                    refresh.Stop();
                    if (captureInProgress) return;
                    Point offset = new Point(initial.X - filteredOrigin.X, initial.Y - filteredOrigin.Y);
                    if (filtered == null || filtered.GetPixel(offset.X+100,offset.Y+100).G < 250 || filtered.GetPixel(offset.X+40,offset.Y+40).R > 5) {
                        if (filtered != null) filtered.Save("self-test-capture.png");
                        throw new Exception("Live capture failed");
                    }
                    beforeFilterForTest = delegate { if (!filterGate.WaitOne(5000)) throw new Exception("Input responsiveness test timed out"); };
                    CaptureFiltered();
                    PostMessage(Handle, 0x8001, new IntPtr((int)Keys.Down), IntPtr.Zero);
                }
                if (stage == 5) {
                    if (!captureInProgress || Top != initial.Y + 10) throw new Exception("Input blocked by background filter");
                    using (var painted = new Bitmap(Width, Height)) {
                        DrawToBitmap(painted, new Rectangle(Point.Empty, Size));
                        Color marker = painted.GetPixel(110, markerY - 10 + 1);
                        if (marker.B < 250 || marker.R > 5) throw new Exception("Cached margin did not display newly exposed blue marker while worker blocked");
                        if (painted.GetPixel(40,30).R > 5) throw new Exception("Cache was not aligned to document after movement");
                    }
                    beforeFilterForTest = null;
                    filterGate.Set();
                    PostMessage(Handle, 0x8001, new IntPtr((int)Keys.Up), IntPtr.Zero);
                }
                if (stage == 6) {
                    refresh.Stop();
                    if (captureInProgress) return;
                    if (Location != initial) throw new Exception("Move back after background filtering failed: expected=" + initial + " actual=" + Location);
                    CaptureFiltered();
                }
                if (stage == 7) {
                    refresh.Stop();
                    if (captureInProgress) return;
                    Point offset = new Point(initial.X - filteredOrigin.X, initial.Y - filteredOrigin.Y);
                    if (filtered.GetPixel(offset.X+100,offset.Y+100).G < 250 || filtered.GetPixel(offset.X+40,offset.Y+40).R > 5) throw new Exception("Frame after movement is stale or incorrect");
                    if (!CanReuseCache(new Rectangle(initial.X, initial.Y+10, Width, Height), capturedAt+100)) throw new Exception("Nearby cache was not reused");
                    if (CanReuseCache(new Rectangle(initial, Size), capturedAt+501)) throw new Exception("Expired cache was reused");
                    if (CanReuseCache(new Rectangle(filteredOrigin.X+filtered.Width-10, initial.Y, Width, Height), capturedAt+100)) throw new Exception("Cache boundary did not request refill");
                    redSheetVisible = true;
                    CaptureFiltered();
                }
                if (stage == 8) {
                    refresh.Stop();
                    if (captureInProgress) return;
                    Point offset = new Point(initial.X - filteredOrigin.X, initial.Y - filteredOrigin.Y);
                    if (filtered.GetPixel(offset.X+100,offset.Y+100).ToArgb() != sheetTint.ToArgb() || filtered.GetPixel(offset.X+40,offset.Y+40).ToArgb() != Color.Black.ToArgb()) throw new Exception("Red sheet did not hide red text while keeping black");
                    using (var sample = new Bitmap(3,1)) {
                        sample.SetPixel(0,0,Color.White); sample.SetPixel(1,0,Color.FromArgb(180,30,30)); sample.SetPixel(2,0,Color.Black);
                        using (var result = Filter(sample, Color.Red, 1f, sheetTint)) {
                            if (result.GetPixel(0,0).ToArgb() != result.GetPixel(1,0).ToArgb() || result.GetPixel(0,0).ToArgb() != sheetTint.ToArgb() || result.GetPixel(2,0).ToArgb() != Color.Black.ToArgb()) throw new Exception("Hidden red text differs from sheet background");
                        }
                    }
                    if (RecordingBuild) {
                        PostMessage(Handle, 0x8001, new IntPtr((int)(Keys.Control | Keys.Shift | Keys.R)), IntPtr.Zero);
                    }
                }
                if (stage == 9 && RecordingBuild) {
                    if (recordingPending || captureInProgress) return;
                    uint affinity;
                    if (!recordingMode || recordingSource.Size != SystemInformation.VirtualScreen.Size || !GetWindowDisplayAffinity(Handle, out affinity) || affinity != 0) throw new Exception("Recording shortcut did not enable visible capture mode");
                    using (var pixel = new Bitmap(1,1)) {
                        using (var graphics = Graphics.FromImage(pixel)) graphics.CopyFromScreen(PointToScreen(new Point(100,100)), Point.Empty, new Size(1,1));
                        if (pixel.GetPixel(0,0).ToArgb() != sheetTint.ToArgb()) throw new Exception("Sheet is not visible in screen capture: " + pixel.GetPixel(0,0));
                    }
                    backdrop.BackColor = Color.Blue; backdrop.Refresh();
                    PostMessage(Handle, 0x8001, new IntPtr((int)Keys.Down), IntPtr.Zero);
                }
                if (stage == 10 && RecordingBuild) {
                    Point offset = new Point(Left - filteredOrigin.X, Top - filteredOrigin.Y);
                    if (filtered.GetPixel(offset.X+100,offset.Y+100).ToArgb() != sheetTint.ToArgb()) throw new Exception("Recording snapshot changed with underlying content");
                    PostMessage(Handle, 0x8001, new IntPtr((int)(Keys.Control | Keys.Shift | Keys.R)), IntPtr.Zero);
                }
                if (stage == 11 && RecordingBuild) {
                    if (captureInProgress) return;
                    uint affinity;
                    if (recordingMode || recordingSource != null || !GetWindowDisplayAffinity(Handle, out affinity) || affinity != 0x11) throw new Exception("Recording exit did not restore capture exclusion");
                    Point offset = new Point(Left - filteredOrigin.X, Top - filteredOrigin.Y);
                    Color current = filtered.GetPixel(offset.X+100,offset.Y+100);
                    if (current.R > 5 || current.B < 150) throw new Exception("Live background did not resume after recording exit");
                }
                if ((!RecordingBuild && stage == 8) || (RecordingBuild && stage == 11)) {
                    File.WriteAllText("self-test-result.txt", "PASS: queued arrow commands, color conversion, expanded cache, cached margin during blocked worker, red background. " + (RecordingBuild ? "RECORDING: Ctrl+Shift+R enables screen capture visibility; sheet pixels captured; full-screen snapshot stays frozen while moving; exit restores capture exclusion and live content." : "Normal build verified.") + "\r\n");
                    filterGate.Dispose();
                    timer.Stop(); backdrop.Close(); Close();
                }
                stage++;
            } catch (Exception e) { filterGate.Set(); File.WriteAllText("self-test-result.txt", "FAIL: " + e); Environment.ExitCode = 1; timer.Stop(); if (backdrop != null) backdrop.Close(); Close(); }
        };
        timer.Start();
    }

    [STAThread] static void Main(string[] args)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool test = Array.IndexOf(args, "--self-test") >= 0;
        using (var sheet = new RedSheet(test)) {
            if (test) sheet.Shown += delegate { sheet.RunSelfTest(); };
            Application.Run(sheet);
        }
    }
}
