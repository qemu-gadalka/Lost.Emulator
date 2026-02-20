using dotNES.Controllers;
using dotNES.Renderers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using static System.Windows.Forms.AxHost;

namespace dotNES
{
    public partial class UI : Form
    {
        private bool _rendererRunning = true;
        private Thread _renderThread;
        private IController _controller = new NES001Controller();

        public const int GameWidth = 256;
        public const int GameHeight = 240;
        public uint[] rawBitmap = new uint[GameWidth * GameHeight];
        public bool ready;
        public IRenderer _renderer;

        public enum FilterMode
        {
            NearestNeighbor, Linear
        }

        public FilterMode _filterMode = FilterMode.Linear;

        class SeparatorItem : MenuItem { public SeparatorItem() : base("-") { } }
        class Item : MenuItem
        {
            public Item(string title, Action<Item> build = null) : base(title) { build?.Invoke(this); }
            public void Add(MenuItem item) => MenuItems.Add(item);
        }
        class RadioItem : Item
        {
            public RadioItem(string title, Action<Item> build = null) : base(title, build) { RadioCheck = true; }
        }

        private int[] speeds = { 1, 2, 4, 8, 16 };
        private int activeSpeed = 1;
        private string[] sizes = { "1x", "2x", "4x", "8x" };
        private string activeSize = "2x";
        private Emulator emu;
        private bool suspended;
        public bool gameStarted;

        private Type[] possibleRenderers = { typeof(SoftwareRenderer), typeof(Direct3DRenderer) };
        private List<IRenderer> availableRenderers = new List<IRenderer>();

        public UI()
        {
            InitializeComponent();
            Debug.WriteLine("Initial scale set to: " + activeSize);

            FindRenderers();
            if (availableRenderers.Count > 0)
                SetRenderer(availableRenderers.Last());
        }

        private void SetRenderer(IRenderer renderer)
        {
            if (_renderer == renderer) return;

            if (_renderer != null)
            {
                var oldCtrl = (Control)renderer;
                oldCtrl.MouseClick -= UI_MouseClick;
                oldCtrl.KeyUp -= UI_KeyUp;
                oldCtrl.KeyDown -= UI_KeyDown;
                oldCtrl.PreviewKeyDown -= UI_PreviewKeyDown;
                _renderer.EndRendering();
                Controls.Remove(oldCtrl);
            }
            _renderer = renderer;
            var ctrl = (Control)renderer;
            ctrl.Dock = DockStyle.Fill;
            ctrl.TabStop = false;
            ctrl.MouseClick += UI_MouseClick;
            ctrl.KeyUp += UI_KeyUp;
            ctrl.KeyDown += UI_KeyDown;
            ctrl.PreviewKeyDown += UI_PreviewKeyDown;
            Controls.Add(ctrl);
            renderer.InitRendering(this);
        }

        private void FindRenderers()
        {
            foreach (var renderType in possibleRenderers)
            {
                try
                {
                    var renderer = (IRenderer)Activator.CreateInstance(renderType);
                    renderer.InitRendering(this);
                    renderer.EndRendering();
                    availableRenderers.Add(renderer);
                }
                catch (Exception)
                {
                    Console.WriteLine($"{renderType} failed to initialize");
                }
            }
        }

        private void BootEmbedded(string romName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(r => r.EndsWith(romName, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrEmpty(resourceName)) { MessageBox.Show("File not found!"); return; }

                using (Stream s = assembly.GetManifestResourceStream(resourceName))
                {
                    byte[] header = new byte[16];
                    s.Read(header, 0, 16);
                    int mapper = (header[7] & 0xF0) | (header[6] >> 4);

                    s.Position = 0;
                    string tempPath = Path.Combine(Path.GetTempPath(), romName + "_boot.nes");
                    using (FileStream fs = File.Create(tempPath)) { s.CopyTo(fs); }
                    BootCartridge(tempPath);
                }
            }
            catch (Exception ex) { MessageBox.Show("Error ROM: " + ex.Message); }
        }
        private void BootCartridge(string rom)
        {
            if (_renderThread != null)
            {
                _rendererRunning = false;
                _renderThread.Join(500);
            }

            _rendererRunning = true;
            emu = new Emulator(rom, _controller);

            _renderThread = new Thread(() =>
            {
                gameStarted = true;
                Stopwatch frameTimer = new Stopwatch();

                while (_rendererRunning)
                {
                    if (suspended)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    frameTimer.Restart();

                    emu.PPU.ProcessFrame();

                    rawBitmap = emu.PPU.RawBitmap;
                    if (!IsDisposed && IsHandleCreated)
                    {
                        try
                        {
                            BeginInvoke((MethodInvoker)_renderer.Draw);
                        }
                        catch { }
                    }

                    int targetMs = 17;

                    while (frameTimer.ElapsedMilliseconds < targetMs)
                    {
                        Thread.Yield();
                    }
                }
            });

            _renderThread.IsBackground = true;
            _renderThread.Priority = ThreadPriority.AboveNormal;
            _renderThread.Start();
        }
        private void UI_Load(object sender, EventArgs e)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
                BootCartridge(args[1]);
        }

        private void HideAll()
        {
            label1.Hide();
            label2.Hide();
            button1.Hide();
            button2.Hide();
            button3.Hide();
            button4.Hide();
        }

        private void Screenshot()
        {
            var bitmap = new Bitmap(GameWidth, GameHeight, PixelFormat.Format32bppArgb);
            for (int y = 0; y < GameHeight; y++)
                for (int x = 0; x < GameWidth; x++)
                    bitmap.SetPixel(x, y, Color.FromArgb((int)(rawBitmap[y * GameWidth + x] | 0xff000000)));
            Clipboard.SetImage(bitmap);
        }

        private void UI_FormClosing(object sender, FormClosingEventArgs e)
        {
            _rendererRunning = false;
            _renderThread?.Abort();
            emu?.Save();
            try { File.Delete(Path.Combine(Path.GetTempPath(), "mario_boot.nes")); } catch { }
        }

        private void UI_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.F12: Screenshot(); break;
                case Keys.F2: suspended = false; break;
                case Keys.F3: suspended = true; break;
                default: _controller.PressKey(e); break;
            }
        }

        private void UI_KeyUp(object sender, KeyEventArgs e) { _controller.ReleaseKey(e); }

        private void UI_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            ContextMenu cm = new ContextMenu();

            var rendererItem = new Item("Renderer");
            foreach (var r in availableRenderers)
                rendererItem.Add(new RadioItem(r.RendererName, y =>
                {
                    y.Checked = r == _renderer;
                    y.Click += delegate { SetRenderer(r); };
                }));
            cm.MenuItems.Add(rendererItem);

            var filterItem = new Item("Filter");
            filterItem.Add(new RadioItem("None", y => { y.Checked = _filterMode == FilterMode.NearestNeighbor; y.Click += delegate { _filterMode = FilterMode.NearestNeighbor; }; }));
            filterItem.Add(new RadioItem("Linear", y => { y.Checked = _filterMode == FilterMode.Linear; y.Click += delegate { _filterMode = FilterMode.Linear; }; }));
            cm.MenuItems.Add(filterItem);

            cm.MenuItems.Add(new SeparatorItem());
            cm.MenuItems.Add(new Item("Screenshot (F12)", x => x.Click += delegate { Screenshot(); }));
            cm.MenuItems.Add(new Item(suspended ? "&Play (F2)" : "&Pause (F3)", x => x.Click += delegate { suspended ^= true; }));

            var speedItem = new Item("&Speed");
            foreach (var s in speeds)
                speedItem.Add(new RadioItem($"{s}x", y => { y.Checked = s == activeSpeed; y.Click += delegate { activeSpeed = s; }; }));
            cm.MenuItems.Add(speedItem);

            cm.Show(this, new Point(e.X, e.Y));
        }

        private void UI_DragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                try { BootCartridge(files[0]); AllowDrop = false; }
                catch { MessageBox.Show("Error loading ROM file"); }
            }
        }
        private void UI_DragEnter(object sender, DragEventArgs e) { e.Effect = DragDropEffects.Copy; }
        private void UI_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e) { e.IsInputKey = true; }
        private void KeysShow()
        {
            MessageBox.Show("Keys: F2/F3 = Start / Pause\r\nZ = Jump\r\nX = Attack\r\nMove: Arrow Keys\r\nF12: Screenshot (May not work)\r\nP.S: If you want a different game, use Drag & Drop into the window with the .rom file");
        }
        private void button1_Click(object sender, EventArgs e)
        {
            HideAll();
            KeysShow();
            BootEmbedded("MarioRom.nes");
        }

        private void button2_Click(object sender, EventArgs e)
        {
            HideAll();
            KeysShow();
            BootEmbedded("Mario3Rom.nes");
        }

        private void button3_Click(object sender, EventArgs e)
        {
            HideAll();
            KeysShow();
            BootEmbedded("Mario2Rom.nes");
        }

        private void button4_Click(object sender, EventArgs e)
        {
            HideAll();
            KeysShow();
            BootEmbedded("Contra.nes");
        }

        private void button5_Click(object sender, EventArgs e)
        {
            Process.Start("cmd.exe", "/c start https://t.me/lostemurequestimg");
        }

        private void button6_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.InitialDirectory = AppDomain.CurrentDomain.BaseDirectory;
                openFileDialog.Filter = "NES ROMs (*.nes)|*.nes|All files (*.*)|*.*";
                openFileDialog.FilterIndex = 1;
                openFileDialog.RestoreDirectory = true;

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    string filePath = openFileDialog.FileName;

                    HideAll();
                    KeysShow();

                    try
                    {
                        BootCartridge(filePath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Error opening rom: " + ex.Message);
                    }
                }
            }
        }
    }
}