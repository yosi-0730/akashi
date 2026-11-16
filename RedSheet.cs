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
    readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyboardRedSheet", "settings.xml");
    readonly NotifyIcon tray;
    bool testing;
    bool autoFilter;
    bool picking;
    Color textColor = Color.Red;
    float strength = 1f;
    Bitmap filtered;
    Point filteredOrigin;
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
        menu.Items.Add("色を選ぶ（C / ダブルクリック）", null, delegate { PickColor(); });
        menu.Items.Add("画面から隠す文字色を拾う（3秒後）", null, delegate { PickScreenColor(); });
        var autoItem = new ToolStripMenuItem("文字を自動で隠す（F）");
        autoItem.Click += delegate { SetFilter(!autoFilter); };
        menu.Items.Add(autoItem);
        menu.Opening += delegate { autoItem.Checked = autoFilter; };
        var opacityMenu = new ToolStripMenuItem("不透明度");
        foreach (int percent in new int[] { 20, 35, 50, 55, 65, 80, 100 })
        {
            int value = percent;
            var item = new ToolStripMenuItem(percent + "%");
            item.Click += delegate { SetFilter(false); Opacity = value / 100.0; Invalidate(); };
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
        FormClosed += delegate { closing = true; refresh.Dispose(); if (filtered != null) filtered.Dispose(); if (!testing) SaveSettings(); tray.Dispose(); };
    }

    void ScheduleMovementFrame()
    {
        if (!autoFilter) return;
        lastMove = Environment.TickCount;
        refresh.Interval = 33;
        Invalidate();
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

    static bool IsTextColor(Color color)
    {
        int max = Math.Max(color.R, Math.Max(color.G, color.B));
        int min = Math.Min(color.R, Math.Min(color.G, color.B));
        return max >= 30 && max - min >= 20;
    }

    void SetFilter(bool enabled)
    {
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

    static unsafe Bitmap Filter(Bitmap source, Color selected, float amount)
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
                    if (chroma < 3) continue;
                    float distance = Math.Max(Math.Abs((red-min)/(float)chroma-targetR), Math.Max(Math.Abs((green-min)/(float)chroma-targetG), Math.Abs((blue-min)/(float)chroma-targetB)));
                    float weight = amount * Math.Max(0f, Math.Min(1f, (0.45f-distance)/0.25f));
                    row[x] = (byte)Math.Round(blue + (255-blue)*weight);
                    row[x+1] = (byte)Math.Round(green + (255-green)*weight);
                    row[x+2] = (byte)Math.Round(red + (255-red)*weight);
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
            Point origin = PointToScreen(Point.Empty);
            capture = new Bitmap(ClientSize.Width, ClientSize.Height);
            using (var graphics = Graphics.FromImage(capture)) graphics.CopyFromScreen(origin, Point.Empty, capture.Size);
            Color selected = textColor;
            float amount = strength;
            Bitmap ownedCapture = capture;
            Action beforeFilter = beforeFilterForTest;
            captureInProgress = true;
            Task.Factory.StartNew(delegate {
                using (ownedCapture) {
                    if (beforeFilter != null) beforeFilter();
                    return Filter(ownedCapture, selected, amount);
                }
            }).ContinueWith(task => {
                captureInProgress = false;
                if (task.IsFaulted) {
                    Exception error = task.Exception.GetBaseException();
                    if (!closing) { SetFilter(false); MessageBox.Show(this, error.Message, "色変換に失敗しました"); }
                    return;
                }
                Bitmap next = task.Result;
                if (closing || !autoFilter || picking || textColor != selected || strength != amount) { next.Dispose(); return; }
                Bitmap previous = filtered;
                filtered = next; filteredOrigin = origin;
                if (previous != null) previous.Dispose();
                Invalidate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
            capture = null;
        } catch (System.ComponentModel.Win32Exception) { SetFilter(false); }
        finally { if (capture != null) capture.Dispose(); }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        Keys modifiers = keyData & Keys.Modifiers;
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
        if (autoFilter) e.Graphics.Clear(Color.White);
        else base.OnPaintBackground(e);
    }

    void LoadSettings()
    {
        try {
            if (!File.Exists(settingsPath)) return;
            XElement s = XElement.Load(settingsPath);
            BackColor = Color.FromArgb((int)s.Attribute("color"));
            if (s.Attribute("textColor") != null) textColor = Color.FromArgb((int)s.Attribute("textColor"));
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
            new XElement("sheet", new XAttribute("color", BackColor.ToArgb()), new XAttribute("textColor", textColor.ToArgb()), new XAttribute("autoFilter", autoFilter), new XAttribute("strength", strength), new XAttribute("opacity", Opacity), new XAttribute("x", Left), new XAttribute("y", Top), new XAttribute("width", Width), new XAttribute("height", Height)).Save(settingsPath);
        } catch (Exception e) {
            if (!(e is IOException || e is UnauthorizedAccessException)) throw;
            MessageBox.Show("設定を保存できませんでした。\n" + e.Message, "赤シート");
        }
    }

    public void RunSelfTest()
    {
        Point initial = Location;
        int stage = 0;
        Form backdrop = null;
        var filterGate = new System.Threading.ManualResetEvent(false);
        var timer = new Timer { Interval = 250 };
        timer.Tick += delegate {
            try {
                if (stage == 0) PostMessage(Handle, 0x100, new IntPtr((int)Keys.Down), IntPtr.Zero);
                if (stage == 1) {
                    if (Top != initial.Y + 10 || Left != initial.X) throw new Exception("Down did not move the real window by 10px");
                    PostMessage(Handle, 0x100, new IntPtr((int)Keys.Up), IntPtr.Zero);
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
                    backdrop.Paint += delegate(object sender, PaintEventArgs e2) { e2.Graphics.FillRectangle(Brushes.Black, 20, 20, 50, 50); };
                    backdrop.Show(); BringToFront(); Activate();
                    SetFilter(true);
                    if (!autoFilter) throw new Exception("Capture exclusion unavailable");
                }
                if (stage == 4) {
                    refresh.Stop();
                    if (captureInProgress) return;
                    if (filtered == null || filtered.GetPixel(100,100).G < 250 || filtered.GetPixel(40,40).R > 5) {
                        if (filtered != null) filtered.Save("self-test-capture.png");
                        throw new Exception("Live capture failed: red=" + (filtered == null ? "null" : filtered.GetPixel(100,100).ToString()) + " black=" + (filtered == null ? "null" : filtered.GetPixel(40,40).ToString()));
                    }
                    beforeFilterForTest = delegate { if (!filterGate.WaitOne(5000)) throw new Exception("Input responsiveness test timed out"); };
                    CaptureFiltered();
                    PostMessage(Handle, 0x100, new IntPtr((int)Keys.Down), IntPtr.Zero);
                }
                if (stage == 5) {
                    if (!captureInProgress || Top != initial.Y + 10) throw new Exception("Input blocked by background filter");
                    beforeFilterForTest = null;
                    filterGate.Set();
                    PostMessage(Handle, 0x100, new IntPtr((int)Keys.Up), IntPtr.Zero);
                }
                if (stage == 6) {
                    refresh.Stop();
                    if (captureInProgress) return;
                    if (Location != initial) throw new Exception("Move back after background filtering failed");
                    CaptureFiltered();
                }
                if (stage == 7) {
                    refresh.Stop();
                    if (captureInProgress) return;
                    if (filteredOrigin != initial || filtered.GetPixel(100,100).G < 250 || filtered.GetPixel(40,40).R > 5) throw new Exception("Frame after movement is stale or incorrect");
                    File.WriteAllText("self-test-result.txt", "PASS: queued Up/Down moved actual window; Shift=1px; Ctrl=50px; opacity and color conversion; LIVE capture hides red and preserves black; keyboard movement while filter worker is blocked; correct frame after movement.\r\n");
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
