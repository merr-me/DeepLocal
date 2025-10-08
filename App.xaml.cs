using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using WF = System.Windows.Forms;

namespace DeepLocal
{
    public partial class App : Application
    {
        public static bool AllowClose { get; private set; } = false;

        private static WF.NotifyIcon? _tray;
        private static Mutex? _singleInstanceMutex;

        private WF.ToolStripMenuItem? _miOpen;
        private WF.ToolStripMenuItem? _miHide;

        // ===== DPI helper =====
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private static double GetScale(Window w)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    uint dpi = GetDpiForWindow(hwnd);
                    return dpi / 96.0;
                }
            }
            catch { }
            return 1.0;
        }

        public static void PlaceWindowBottomRight(Window w, double marginDip = 16.0)
        {
            if (w == null) return;

            var helper = new WindowInteropHelper(w);
            var hwnd = helper.Handle;

            var screen = hwnd != IntPtr.Zero
                ? WF.Screen.FromHandle(hwnd)
                : WF.Screen.FromPoint(WF.Cursor.Position);

            var waPx = screen.WorkingArea; 
            double scale = GetScale(w);    

            double widthDip  = (w.ActualWidth  > 0) ? w.ActualWidth  : (!double.IsNaN(w.Width)  && w.Width  > 0 ? w.Width  : 900);
            double heightDip = (w.ActualHeight > 0) ? w.ActualHeight : (!double.IsNaN(w.Height) && w.Height > 0 ? w.Height : 600);

            int widthPx  = (int)Math.Round(widthDip  * scale);
            int heightPx = (int)Math.Round(heightDip * scale);
            int marginPx = (int)Math.Round(marginDip * scale);

            int leftPx = waPx.Right  - marginPx - widthPx;
            int topPx  = waPx.Bottom - marginPx - heightPx;

            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = leftPx / scale;
            w.Top  = topPx  / scale;

            double waLeftDip   = waPx.Left   / scale;
            double waTopDip    = waPx.Top    / scale;
            double waRightDip  = waPx.Right  / scale;
            double waBottomDip = waPx.Bottom / scale;

            if (w.Left < waLeftDip) w.Left = waLeftDip;
            if (w.Top  < waTopDip)  w.Top  = waTopDip;
            if (w.Left + widthDip  > waRightDip)  w.Left = Math.Max(waLeftDip,  waRightDip  - widthDip);
            if (w.Top  + heightDip > waBottomDip) w.Top  = Math.Max(waTopDip,   waBottomDip - heightDip);
        }

        public static void ResnapAfterFirstLayout(Window w)
        {
            EventHandler? handler = null;
            handler = (s, e) =>
            {
                if (w.ActualWidth <= 0 || w.ActualHeight <= 0) return;
                PlaceWindowBottomRight(w);
                w.LayoutUpdated -= handler!;
            };
            w.LayoutUpdated += handler!;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool createdNew = false;
            _singleInstanceMutex = new Mutex(true, "DeepLocal_OfflineTranslator_SingleInstance", out createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }

            if (_tray == null)
            {
                _tray = new WF.NotifyIcon
                {
                    Visible = true,
                    Text = "DeepLocal – Offline Translator",
                    Icon = LoadTrayIcon()
                };

                var menu = new WF.ContextMenuStrip();
                _miOpen = new WF.ToolStripMenuItem("Open", null, (_, __) => ShowMainWindow());
                _miHide = new WF.ToolStripMenuItem("Hide window", null, (_, __) => HideMainWindow());
                menu.Items.Add(_miOpen);
                menu.Items.Add("Translate from clipboard (Alt+T)", null, async (_, __) =>
                {
                    if (Current.MainWindow is MainWindow mw) await mw.TranslateClipboardAsync();
                });
                menu.Items.Add(_miHide);
                menu.Items.Add(new WF.ToolStripSeparator());
                menu.Items.Add("Exit", null, (_, __) =>
                {
                    AllowClose = true;
                    Current.Shutdown();
                });
                _tray.ContextMenuStrip = menu;

                _tray.DoubleClick += (_, __) => ToggleMainWindow();
            }

            var win = new MainWindow();
            this.MainWindow = win;

            PlaceWindowBottomRight(win);
            win.SourceInitialized += (_, __) => PlaceWindowBottomRight(win);
            win.Loaded += (_, __) => ResnapAfterFirstLayout(win);

            win.Show();
            win.Activate();

            UpdateMenuItems();
        }

        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string ico = Path.Combine(baseDir, "Assets", "DeepLocal_UserIcon_Framed.ico");
                if (File.Exists(ico)) return new System.Drawing.Icon(ico);

                string alt = Path.Combine(baseDir, "Assets", "deeplocal.ico");
                if (File.Exists(alt)) return new System.Drawing.Icon(alt);
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        private static Window? MainWin => Current?.MainWindow as Window;

        private void ShowMainWindow()
        {
            var w = MainWin;
            if (w == null) return;

            w.Dispatcher.Invoke(() =>
            {
                PlaceWindowBottomRight(w);
                ResnapAfterFirstLayout(w);

                if (!w.IsVisible) w.Show();
                w.ShowInTaskbar = true;
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
                UpdateMenuItems();
            });
        }

        private void HideMainWindow()
        {
            var w = MainWin;
            if (w == null) return;

            w.Dispatcher.Invoke(() =>
            {
                w.Hide();
                w.ShowInTaskbar = false;
                UpdateMenuItems();
            });
        }

        private void ToggleMainWindow()
        {
            var w = MainWin;
            if (w == null) return;

            bool visible = w.IsVisible && w.WindowState != WindowState.Minimized;
            if (visible) HideMainWindow();
            else ShowMainWindow();
        }

        private void UpdateMenuItems()
        {
            var w = MainWin;
            if (w == null) return;

            bool visible = w.IsVisible && w.WindowState != WindowState.Minimized;
            if (_miOpen != null) _miOpen.Enabled = !visible;
            if (_miHide != null) _miHide.Enabled = visible;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AllowClose = true;

            if (_tray != null)
            {
                try { _tray.Visible = false; } catch { }
                try { _tray.Dispose(); } catch { }
                _tray = null;
            }

            if (_singleInstanceMutex != null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            base.OnExit(e);
        }
    }
}
