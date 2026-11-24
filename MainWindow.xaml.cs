using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace dionishuesos
{
    public partial class MainWindow : Window
    {
        private readonly string saveFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppDock", "appdata.json");

        private readonly string shortcutsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppDock", "Shortcuts");

        private readonly string iconsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppDock", "Icons");

        private const string DefaultGearUrl =
            "https://upload.wikimedia.org/wikipedia/commons/9/96/Gear-icon-transparent-background.png";

        private const string DefaultCloseUrl =
            "https://images.icon-icons.com/2367/PNG/512/x_close_icon_143474.png";

        private string currentColor = "#202020";
        private string settingsImagePath = "";
        private string closeImagePath = "";
        private string backgroundImagePath = "";
        private Dictionary<string, string> customIcons =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Button draggedButton = null;
        private int originalRow;
        private int originalColumn;
        private Point dragStartPoint;
        private bool isDragging = false;
        private bool suppressTrayHide = false;
        private bool allowRealClose = false;
        private System.Windows.Forms.NotifyIcon trayIcon;

        // Глобальное отслеживание Alt+Tab. Потеря фокуса от обычного клика
        // больше не сворачивает AppDock в трей.
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_TAB = 0x09;
        private const int VK_MENU = 0x12;

        private delegate IntPtr LowLevelKeyboardProc(
            int nCode, IntPtr wParam, IntPtr lParam);

        private LowLevelKeyboardProc? keyboardHookCallback;
        private IntPtr keyboardHookHandle = IntPtr.Zero;
        private long lastAltTabTimestamp = 0;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook, LowLevelKeyboardProc callback,
            IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath, uint dwFileAttributes, out SHFILEINFO psfi,
            uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(
            uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHCNE_UPDATEITEM = 0x00002000;
        private const uint SHCNE_ATTRIBUTES = 0x00000800;
        private const uint SHCNF_PATHW = 0x00000005;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private class AppButtonData
        {
            public string ApplicationPath { get; set; } = "";
            public string ShortcutPath { get; set; } = "";
            public string StoredShortcutPath { get; set; } = "";
            public bool HiddenByAppDock { get; set; }
            public int OriginalAttributes { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();

            // При 15 или меньшем количестве элементов прокрутка полностью отключена.
            AppsScrollViewer.PreviewMouseWheel += AppsScrollViewer_PreviewMouseWheel;

            Directory.CreateDirectory(shortcutsFolder);
            Directory.CreateDirectory(iconsFolder);

            Left = Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
            Top = Math.Max(0, (SystemParameters.WorkArea.Height - Height) / 2);

            trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "AppDock",
                Visible = false
            };

            var iconUri = new Uri(
                "pack://application:,,,/appdock.ico",
                UriKind.Absolute
            );

            var iconStream = System.Windows.Application
                .GetResourceStream(iconUri)?.Stream;

            if (iconStream != null)
            {
                using (iconStream)
                using (var tempIcon = new System.Drawing.Icon(iconStream))
                {
                    trayIcon.Icon = (System.Drawing.Icon)tempIcon.Clone();
                }
            }
            else
            {
                trayIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            trayIcon.DoubleClick += (s, e) => RestoreFromTray(); ;

            var trayMenu = new System.Windows.Forms.ContextMenuStrip();
            trayMenu.Items.Add("Открыть AppDock", null, (s, e) => RestoreFromTray());
            trayMenu.Items.Add("Выход", null, (s, e) => ExitApplication());
            trayIcon.ContextMenuStrip = trayMenu;

            InstallKeyboardHook();

            LoadData();
            EnsureDefaultInterfaceImages();
            RefreshInterfaceImages();
            ApplyTheme();
            UpdateFolderTitle();

        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            if (suppressTrayHide || allowRealClose || !IsVisible)
                return;

            // Сворачиваем только если непосредственно перед потерей фокуса
            // пользователь нажал Alt+Tab. Обычный клик по другому окну игнорируем.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (suppressTrayHide || allowRealClose || !IsVisible)
                    return;

                long elapsedTicks = Stopwatch.GetTimestamp() - lastAltTabTimestamp;
                double elapsedSeconds =
                    elapsedTicks / (double)Stopwatch.Frequency;

                if (elapsedSeconds >= 0 && elapsedSeconds <= 3.0 && !IsActive)
                {
                    lastAltTabTimestamp = 0;
                    MinimizeToTray();
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void InstallKeyboardHook()
        {
            keyboardHookCallback = KeyboardHookCallback;
            keyboardHookHandle = SetWindowsHookEx(
                WH_KEYBOARD_LL,
                keyboardHookCallback,
                IntPtr.Zero,
                0);
        }

        private IntPtr KeyboardHookCallback(
            int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 &&
                (wParam == (IntPtr)WM_KEYDOWN ||
                 wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int key = Marshal.ReadInt32(lParam);

                if (key == VK_TAB &&
                    (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                {
                    lastAltTabTimestamp = Stopwatch.GetTimestamp();
                }
            }

            return CallNextHookEx(
                keyboardHookHandle, nCode, wParam, lParam);
        }

        private void RemoveKeyboardHook()
        {
            if (keyboardHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(keyboardHookHandle);
                keyboardHookHandle = IntPtr.Zero;
            }

            keyboardHookCallback = null;
        }

        private void MinimizeToTray()
        {
            if (allowRealClose) return;
            SaveData();
            trayIcon.Visible = true;
            Hide();
        }

        private void RestoreFromTray()
        {
            trayIcon.Visible = false;
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        private void ExitApplication()
        {
            allowRealClose = true;
            trayIcon.Visible = false;
            SaveData();
            Close();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;

            if (SettingsPanel.Visibility == Visibility.Visible &&
                !IsInsideElement(source, SettingsPanel) &&
                !IsInsideElement(source, SettingsButton))
            {
                SettingsPanel.Visibility = Visibility.Collapsed;
                SaveData();
            }

            if (IsInsideButton(source)) return;
            if (IsInsideElement(source, SettingsPanel)) return;

            try { DragMove(); } catch { }
        }

        private bool IsInsideButton(DependencyObject source)
        {
            while (source != null)
            {
                if (source is Button) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private bool IsInsideElement(DependencyObject source, DependencyObject parent)
        {
            while (source != null)
            {
                if (source == parent) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (string path in paths)
            {
                if (File.Exists(path) || Directory.Exists(path))
                    AddApplication(path);
            }
        }

        private bool IsOnDesktop(string path)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                string fullDesktop = Path.GetFullPath(desktop).TrimEnd(Path.DirectorySeparatorChar);

                return fullPath.Equals(fullDesktop, StringComparison.OrdinalIgnoreCase) ||
                       fullPath.StartsWith(fullDesktop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private int HideFromDesktop(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                int original = (int)attributes;

                if ((attributes & FileAttributes.Hidden) != 0) return original;

                File.SetAttributes(path, attributes | FileAttributes.Hidden);
                RefreshExplorer(path);
                return original;
            }
            catch { return -1; }
        }

        private bool RestoreFromDesktop(string path, int originalAttributes)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return false;

                FileAttributes attributes = originalAttributes >= 0
                    ? (FileAttributes)originalAttributes
                    : File.GetAttributes(path);

                attributes &= ~FileAttributes.Hidden;
                File.SetAttributes(path, attributes);
                RefreshExplorer(path);
                return true;
            }
            catch { return false; }
        }

        private void RefreshExplorer(string path)
        {
            try
            {
                SHChangeNotify(SHCNE_ATTRIBUTES | SHCNE_UPDATEITEM, SHCNF_PATHW,
                    IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        private void AddApplication(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            bool isFile = File.Exists(path);
            bool isDirectory = Directory.Exists(path);
            if (!isFile && !isDirectory) return;

            bool isShortcut = isFile && Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);

            string applicationPath = path;
            string shortcutPath = "";
            string storedShortcutPath = "";
            bool hiddenByAppDock = false;
            int originalAttributes = 0;

            if (isShortcut)
            {
                string resolvedPath = ResolveShortcut(path);
                if (string.IsNullOrEmpty(resolvedPath)) return;
                if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath)) return;

                applicationPath = resolvedPath;
                shortcutPath = path;

                string storedName = Guid.NewGuid().ToString("N") + ".lnk";
                storedShortcutPath = Path.Combine(shortcutsFolder, storedName);

                try
                {
                    File.Move(path, storedShortcutPath);
                }
                catch
                {
                    MessageBox.Show("Не удалось перенести ярлык в AppDock.", "AppDock",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else if (IsOnDesktop(path))
            {
                int attributes = HideFromDesktop(path);
                if (attributes >= 0)
                {
                    hiddenByAppDock = true;
                    originalAttributes = attributes;
                }
            }

            AddApplicationFromData(applicationPath, GetDisplayName(path), -1, -1,
                shortcutPath, storedShortcutPath, hiddenByAppDock, originalAttributes);

            bool added = AppsPanel.Children.OfType<Button>().Any(button =>
            {
                AppButtonData data = GetButtonData(button);
                return data != null && string.Equals(data.ApplicationPath, applicationPath, StringComparison.OrdinalIgnoreCase);
            });

            if (!added)
            {
                if (hiddenByAppDock) RestoreFromDesktop(path, originalAttributes);
                if (isShortcut && File.Exists(storedShortcutPath)) RestoreShortcut(shortcutPath, storedShortcutPath);
                return;
            }

            SaveData();
        }

        private string GetDisplayName(string path)
        {
            try
            {
                string cleanPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Directory.Exists(path))
                {
                    string directoryName = Path.GetFileName(cleanPath);
                    return string.IsNullOrEmpty(directoryName) ? path : directoryName;
                }

                // Для файлов скрываем расширение: app.exe -> app, link.url -> link.
                string fileName = Path.GetFileNameWithoutExtension(cleanPath);
                return string.IsNullOrEmpty(fileName) ? path : fileName;
            }
            catch { return path; }
        }

        private string GetSavedDisplayName(AppItem app)
        {
            string sourcePath = !string.IsNullOrEmpty(app.ShortcutPath)
                ? app.ShortcutPath
                : app.Path;

            string expectedFileName = Path.GetFileName(sourcePath ?? "");
            if (string.IsNullOrWhiteSpace(app.Name) ||
                string.Equals(app.Name, expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                return GetDisplayName(sourcePath);
            }

            // Не трогаем пользовательское имя, если оно уже было переименовано вручную.
            return app.Name;
        }

        private string ResolveShortcut(string shortcutPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;

                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                return shortcut.TargetPath;
            }
            catch { return null; }
        }

        private bool RestoreShortcut(string originalPath, string storedPath)
        {
            try
            {
                if (string.IsNullOrEmpty(storedPath) || !File.Exists(storedPath)) return false;

                string destination = originalPath;
                if (string.IsNullOrEmpty(destination))
                {
                    destination = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        Path.GetFileName(storedPath));
                }

                string directory = Path.GetDirectoryName(destination);
                if (string.IsNullOrEmpty(directory)) return false;
                Directory.CreateDirectory(directory);

                if (File.Exists(destination))
                {
                    string directoryPath = Path.GetDirectoryName(destination);
                    string fileName = Path.GetFileNameWithoutExtension(destination);
                    string extension = Path.GetExtension(destination);
                    int number = 2;
                    do
                    {
                        destination = Path.Combine(directoryPath, fileName + " (" + number + ")" + extension);
                        number++;
                    } while (File.Exists(destination));
                }

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;

                dynamic shell = Activator.CreateInstance(shellType);
                dynamic oldShortcut = shell.CreateShortcut(storedPath);
                dynamic newShortcut = shell.CreateShortcut(destination);

                newShortcut.TargetPath = oldShortcut.TargetPath;
                newShortcut.Arguments = oldShortcut.Arguments;
                newShortcut.WorkingDirectory = oldShortcut.WorkingDirectory;
                newShortcut.IconLocation = oldShortcut.IconLocation;
                newShortcut.Description = oldShortcut.Description;
                newShortcut.Save();

                if (!File.Exists(destination)) return false;
                File.Delete(storedPath);
                RefreshExplorer(destination);
                return true;
            }
            catch { return false; }
        }

        private AppButtonData GetButtonData(Button button) => button.Tag as AppButtonData;

        private void AddApplicationFromData(string file, string name, int row, int column,
            string shortcutPath = "", string storedShortcutPath = "",
            bool hiddenByAppDock = false, int originalAttributes = 0)
        {
            if (!File.Exists(file) && !Directory.Exists(file)) return;

            foreach (Button existingButton in AppsPanel.Children.OfType<Button>())
            {
                AppButtonData existingData = GetButtonData(existingButton);
                if (existingData != null && string.Equals(existingData.ApplicationPath, file, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            Button appButton = CreateApplicationButton(file, name);
            appButton.Tag = new AppButtonData
            {
                ApplicationPath = file,
                ShortcutPath = shortcutPath,
                StoredShortcutPath = storedShortcutPath,
                HiddenByAppDock = hiddenByAppDock,
                OriginalAttributes = originalAttributes
            };

            if (row >= 0 && column >= 0 && column < 5 && GetButtonAtCell(row, column) == null)
            {
                EnsureGridRow(row);
                Grid.SetRow(appButton, row);
                Grid.SetColumn(appButton, column);
                AppsPanel.Children.Add(appButton);
            }
            else
            {
                TryPlaceApplication(appButton);
            }

            AnimateAppear(appButton);
            UpdateScrollBehavior();
        }

        private Button CreateApplicationButton(string file, string name)
        {
            Button appButton = new Button
            {
                Width = 90,
                Height = 100,
                Margin = new Thickness(5),
                Style = (Style)FindResource("AppButtonStyle"),
                ToolTip = file
            };

            StackPanel content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            ImageSource customSource = GetCustomIconSource(file);
            if (customSource != null)
            {
                AddImageToPanel(content, customSource, 48, 48, 5);
            }
            else
            {
                SHFILEINFO shinfo;
                SHGetFileInfo(file, 0, out shinfo, (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    SHGFI_ICON | SHGFI_LARGEICON);

                if (shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        ImageSource icon = Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        AddImageToPanel(content, icon, 48, 48, 5);
                    }
                    finally { DestroyIcon(shinfo.hIcon); }
                }
            }

            TextBlock text = new TextBlock
            {
                Text = name,
                Foreground = Brushes.White,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 85
            };

            content.Children.Add(text);
            appButton.Content = content;

            appButton.MouseRightButtonUp += AppButton_MouseRightButtonUp;
            appButton.PreviewMouseLeftButtonDown += AppButton_MouseLeftButtonDown;
            appButton.PreviewMouseMove += AppButton_MouseMove;
            appButton.PreviewMouseLeftButtonUp += AppButton_MouseLeftButtonUp;

            return appButton;
        }

        private void AddImageToPanel(StackPanel panel, ImageSource source, double width, double height, double bottomMargin)
        {
            panel.Children.Add(new Image
            {
                Source = source,
                Width = width,
                Height = height,
                Margin = new Thickness(0, 0, 0, bottomMargin),
                Stretch = Stretch.Uniform
            });
        }

        private ImageSource GetCustomIconSource(string path)
        {
            if (customIcons.TryGetValue(path, out string iconPath) && File.Exists(iconPath))
                return LoadImage(iconPath);
            return null;
        }

        private BitmapImage LoadImage(string path)
        {
            try
            {
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }

        private void AppButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Button button = (Button)sender;
            if (e.ClickCount > 1) return;

            draggedButton = button;
            TranslateTransform initialTranslate = GetTranslateTransform(button);
            initialTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            initialTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            initialTranslate.X = 0;
            initialTranslate.Y = 0;
            originalRow = Grid.GetRow(button);
            originalColumn = Grid.GetColumn(button);
            dragStartPoint = e.GetPosition(AppsPanel);
            isDragging = false;
            button.CaptureMouse();
            e.Handled = true;
        }

        private void AppButton_MouseMove(object sender, MouseEventArgs e)
        {
            if (draggedButton == null) return;

            Point currentPoint = e.GetPosition(AppsPanel);
            double deltaX = currentPoint.X - dragStartPoint.X;
            double deltaY = currentPoint.Y - dragStartPoint.Y;

            if (!isDragging)
            {
                if (Math.Abs(deltaX) < 5 && Math.Abs(deltaY) < 5) return;
                isDragging = true;
                draggedButton.BeginAnimation(OpacityProperty, null);
                draggedButton.Opacity = 0.78;
                Panel.SetZIndex(draggedButton, 100);
            }

            TranslateTransform translate = GetTranslateTransform(draggedButton);
            translate.X = deltaX;
            translate.Y = deltaY;

            int targetColumn = Math.Max(0, Math.Min(4, (int)(currentPoint.X / 100)));
            int targetRow = Math.Max(0, (int)(currentPoint.Y / 110));
            EnsureGridRow(targetRow);
            HighlightCell(targetRow, targetColumn);
        }

        private void AppButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (draggedButton == null) return;

            Button button = draggedButton;
            button.ReleaseMouseCapture();

            bool wasDragging = isDragging;
            if (wasDragging)
            {
                Point currentPoint = e.GetPosition(AppsPanel);
                int targetColumn = Math.Max(0, Math.Min(4, (int)(currentPoint.X / 100)));
                int targetRow = Math.Max(0, (int)(currentPoint.Y / 110));
                EnsureGridRow(targetRow);
                MoveApplication(button, targetRow, targetColumn);
            }

            TranslateTransform translate = GetTranslateTransform(button);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            DoubleAnimation returnX = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            DoubleAnimation returnY = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            translate.BeginAnimation(TranslateTransform.XProperty, returnX, HandoffBehavior.SnapshotAndReplace);
            translate.BeginAnimation(TranslateTransform.YProperty, returnY, HandoffBehavior.SnapshotAndReplace);

            button.BeginAnimation(OpacityProperty, null);
            button.Opacity = 1;
            Panel.SetZIndex(button, 1);
            ClearCellHighlight();

            draggedButton = null;
            isDragging = false;
            SaveData();

            if (!wasDragging)
                OpenApplication(button);

            e.Handled = true;
        }

        private void OpenApplication(Button button)
        {
            AppButtonData? data = GetButtonData(button);
            if (data == null || string.IsNullOrWhiteSpace(data.ApplicationPath)) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = data.ApplicationPath,
                    UseShellExecute = true
                });
            }
            catch
            {
                MessageBox.Show("Не удалось открыть файл или папку.", "AppDock",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private TranslateTransform GetTranslateTransform(Button button)
        {
            TransformGroup group = button.RenderTransform as TransformGroup;
            if (group == null)
            {
                group = new TransformGroup();
                group.Children.Add(new ScaleTransform(1, 1));
                group.Children.Add(new TranslateTransform(0, 0));
                button.RenderTransform = group;
            }

            TranslateTransform translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (translate == null)
            {
                translate = new TranslateTransform(0, 0);
                group.Children.Add(translate);
            }
            return translate;
        }

        private void MoveApplication(Button button, int targetRow, int targetColumn)
        {
            int oldRow = Grid.GetRow(button);
            int oldColumn = Grid.GetColumn(button);
            if (oldRow == targetRow && oldColumn == targetColumn) return;

            Button otherButton = GetButtonAtCell(targetRow, targetColumn);
            if (otherButton != null && otherButton != button)
            {
                Grid.SetRow(otherButton, oldRow);
                Grid.SetColumn(otherButton, oldColumn);
                AnimateMove(otherButton);
            }

            Grid.SetRow(button, targetRow);
            Grid.SetColumn(button, targetColumn);
            AnimateMove(button);
        }

        private void AnimateMove(Button button)
        {
            button.Opacity = 0.7;
            button.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.7,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        private void HighlightCell(int row, int column)
        {
            Grid.SetRow(DropHighlight, row);
            Grid.SetColumn(DropHighlight, column);

            if (DropHighlight.Visibility != Visibility.Visible)
            {
                DropHighlight.Visibility = Visibility.Visible;
                DropHighlight.BeginAnimation(OpacityProperty, new DoubleAnimation
                {
                    From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(120)
                });
            }
        }

        private void ClearCellHighlight()
        {
            DoubleAnimation fadeOut = new DoubleAnimation
            {
                From = DropHighlight.Opacity,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(120)
            };
            fadeOut.Completed += (s, e) => DropHighlight.Visibility = Visibility.Collapsed;
            DropHighlight.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void EnsureGridRow(int row)
        {
            while (AppsPanel.RowDefinitions.Count <= row)
            {
                AppsPanel.RowDefinitions.Add(
                    new RowDefinition { Height = new GridLength(110) });
            }
        }

        private void UpdateScrollBehavior()
        {
            int appCount = AppsPanel.Children.OfType<Button>().Count();
            bool needsScrolling = appCount > 15;

            AppsScrollViewer.VerticalScrollBarVisibility = needsScrolling
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
            AppsScrollViewer.PanningMode = needsScrolling
                ? PanningMode.VerticalOnly
                : PanningMode.None;

            if (!needsScrolling)
                AppsScrollViewer.ScrollToVerticalOffset(0);
        }

        private void AppsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (AppsPanel.Children.OfType<Button>().Count() <= 15)
            {
                e.Handled = true;
                AppsScrollViewer.ScrollToVerticalOffset(0);
            }
        }

        private void CompactGridRows()
        {
            var buttons = AppsPanel.Children.OfType<Button>()
                .OrderBy(b => Grid.GetRow(b))
                .ThenBy(b => Grid.GetColumn(b))
                .ToList();

            int index = 0;
            foreach (Button item in buttons)
            {
                Grid.SetRow(item, index / 5);
                Grid.SetColumn(item, index % 5);
                index++;
            }

            int neededRows = Math.Max(3, (int)Math.Ceiling(buttons.Count / 5.0));
            while (AppsPanel.RowDefinitions.Count < neededRows)
                AppsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(110) });
            while (AppsPanel.RowDefinitions.Count > neededRows)
                AppsPanel.RowDefinitions.RemoveAt(AppsPanel.RowDefinitions.Count - 1);
            UpdateScrollBehavior();
            AppsScrollViewer.ScrollToTop();
        }

        private Button GetButtonAtCell(int row, int column)
        {
            foreach (Button button in AppsPanel.Children.OfType<Button>())
            {
                if (button == draggedButton) continue;
                if (Grid.GetRow(button) == row && Grid.GetColumn(button) == column) return button;
            }
            return null;
        }

        private void TryPlaceApplication(Button button)
        {
            int row = 0;
            while (true)
            {
                EnsureGridRow(row);
                for (int column = 0; column < 5; column++)
                {
                    if (GetButtonAtCell(row, column) == null)
                    {
                        Grid.SetRow(button, row);
                        Grid.SetColumn(button, column);
                        AppsPanel.Children.Add(button);
                        return;
                    }
                }
                row++;
            }
        }

        private void AnimateAppear(Button button)
        {
            button.Opacity = 0;
            button.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(200)
            });
        }

        private void AppButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            Button button = (Button)sender;
            AppButtonData data = GetButtonData(button);
            ContextMenu menu = new ContextMenu();

            MenuItem rename = new MenuItem { Header = "✏ Переименовать" };
            MenuItem delete = new MenuItem { Header = "🗑 Удалить" };

            rename.Click += (s, args) => StartRename(button);
            delete.Click += (s, args) => RemoveApplication(button);

            menu.Items.Add(rename);

            if (data != null && Path.GetExtension(data.ApplicationPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                MenuItem changeIcon = new MenuItem { Header = "🖼 Поменять иконку" };
                changeIcon.Click += (s, args) => ChangeApplicationIcon(button);
                menu.Items.Add(changeIcon);
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(delete);
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void StartRename(Button button)
        {
            if (!(button.Content is StackPanel panel)) return;

            TextBlock oldText = panel.Children.OfType<TextBlock>().FirstOrDefault();
            if (oldText == null) return;

            TextBox textBox = new TextBox
            {
                Text = oldText.Text,
                Width = 85,
                Height = 30,
                FontSize = 12,
                TextAlignment = TextAlignment.Center
            };

            panel.Children.Remove(oldText);
            panel.Children.Add(textBox);
            textBox.Focus();
            textBox.SelectAll();

            bool saved = false;
            void SaveRename()
            {
                if (saved) return;
                saved = true;

                string newName = textBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(newName)) newName = oldText.Text;

                panel.Children.Remove(textBox);
                oldText.Text = newName;
                panel.Children.Add(oldText);
                SaveData();
            }

            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) SaveRename();
                else if (e.Key == Key.Escape)
                {
                    saved = true;
                    panel.Children.Remove(textBox);
                    panel.Children.Add(oldText);
                }
            };
            textBox.LostFocus += (s, e) => SaveRename();
        }

        private void RemoveApplication(Button button)
        {
            AppButtonData buttonData = GetButtonData(button);
            DoubleAnimation animation = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180)
            };

            animation.Completed += (s, e) =>
            {
                bool success = true;

                if (buttonData != null && !string.IsNullOrEmpty(buttonData.StoredShortcutPath))
                {
                    success = RestoreShortcut(buttonData.ShortcutPath, buttonData.StoredShortcutPath);
                }
                else if (buttonData != null && buttonData.HiddenByAppDock)
                {
                    success = RestoreFromDesktop(buttonData.ApplicationPath, buttonData.OriginalAttributes);
                }

                if (success)
                {
                    AppsPanel.Children.Remove(button);
                    CompactGridRows();
                    SaveData();
                }
                else
                {
                    button.Opacity = 1;
                    SaveData();
                    MessageBox.Show("Не удалось вернуть объект на рабочий стол.", "AppDock",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            button.BeginAnimation(OpacityProperty, animation);
        }

        private void ChangeApplicationIcon(Button button)
        {
            AppButtonData data = GetButtonData(button);
            if (data == null) return;

            string selected = PickImageWithPreview("Выберите иконку для EXE", GetCustomIconPath(data.ApplicationPath));
            if (string.IsNullOrEmpty(selected)) return;

            string stored = CopyIconToAppData(selected, "app");
            if (string.IsNullOrEmpty(stored)) return;

            customIcons[data.ApplicationPath] = stored;
            RebuildApplicationButton(button);
            SaveData();
        }

        private string GetCustomIconPath(string applicationPath)
        {
            return customIcons.TryGetValue(applicationPath, out string path) && File.Exists(path)
                ? path : "";
        }

        private void RebuildApplicationButton(Button oldButton)
        {
            int row = Grid.GetRow(oldButton);
            int column = Grid.GetColumn(oldButton);
            AppButtonData data = GetButtonData(oldButton);
            if (data == null) return;

            string name = data.ApplicationPath;
            if (oldButton.Content is StackPanel panel)
            {
                TextBlock text = panel.Children.OfType<TextBlock>().FirstOrDefault();
                if (text != null) name = text.Text;
            }

            Button newButton = CreateApplicationButton(data.ApplicationPath, name);
            newButton.Tag = data;
            Grid.SetRow(newButton, row);
            Grid.SetColumn(newButton, column);

            int index = AppsPanel.Children.IndexOf(oldButton);
            AppsPanel.Children.RemoveAt(index);
            AppsPanel.Children.Insert(index, newButton);
            AnimateAppear(newButton);
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
            RefreshInterfaceImages();
            ApplyTheme();
            e.Handled = true;
        }

        private void SettingsCollapse_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            SaveData();
            e.Handled = true;
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            UpdateFolderTitle();
            SettingsPanel.Visibility = Visibility.Collapsed;
            ApplyTheme();
            SaveData();
        }

        private void UpdateFolderTitle()
        {
            string name = string.IsNullOrWhiteSpace(FolderNameBox?.Text)
                ? "Мои приложения" : FolderNameBox.Text.Trim();
            if (FolderTitle != null) FolderTitle.Text = name;
            Title = name;
        }

        private void ApplyBackgroundOpacity()
        {
            if (OpacitySlider == null || MainBorder == null) return;
            double opacity = Math.Max(0, Math.Min(1, OpacitySlider.Value));

            if (TopBar != null && TopBar.Background is SolidColorBrush topBrush)
                topBrush.Opacity = Math.Min(1.0, opacity + 0.10);

            if (!string.IsNullOrWhiteSpace(backgroundImagePath) && File.Exists(backgroundImagePath))
            {
                BitmapImage? image = LoadImage(backgroundImagePath);
                if (image != null)
                {
                    MainBorder.Background = new ImageBrush(image)
                    {
                        Stretch = Stretch.UniformToFill,
                        Opacity = opacity
                    };
                    return;
                }
            }

            Color color = (Color)ColorConverter.ConvertFromString(currentColor);
            MainBorder.Background = new SolidColorBrush(color) { Opacity = opacity };
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ApplyBackgroundOpacity();
        }

        private void SetBackgroundColor(string hex)
        {
            currentColor = hex;
            ApplyTheme();
            ApplyBackgroundOpacity();
            SaveData();
        }

        private void DarkColor_Click(object sender, RoutedEventArgs e) => SetBackgroundColor("#202020");
        private void LightColor_Click(object sender, RoutedEventArgs e) => SetBackgroundColor("#E8E8E8");

        private void CustomColor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                suppressTrayHide = true;
                using (var dialog = new System.Windows.Forms.ColorDialog())
                {
                    dialog.FullOpen = true;
                    dialog.AnyColor = true;
                    dialog.Color = System.Drawing.ColorTranslator.FromHtml(currentColor);
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        string hex = System.Drawing.ColorTranslator.ToHtml(dialog.Color);
                        SetBackgroundColor(hex);
                    }
                }
            }
            finally
            {
                suppressTrayHide = false;
                Activate();
            }
        }

        private void ApplyTheme()
        {
            if (MainBorder == null) return;

            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(currentColor); }
            catch { c = Color.FromRgb(32, 32, 32); currentColor = "#202020"; }

            double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            bool light = luminance > 0.58;
            Color panelColor = light
                ? Color.FromRgb((byte)Math.Max(0, c.R - 28), (byte)Math.Max(0, c.G - 28), (byte)Math.Max(0, c.B - 28))
                : Color.FromRgb((byte)Math.Min(255, c.R + 18), (byte)Math.Min(255, c.G + 18), (byte)Math.Min(255, c.B + 18));

            Brush panelBrush = new SolidColorBrush(panelColor);
            Brush textBrush = light ? Brushes.Black : Brushes.White;

            if (TopBar != null) TopBar.Background = new SolidColorBrush(panelColor)
            { Opacity = Math.Min(1.0, (OpacitySlider?.Value ?? 0.87) + 0.10) };
            if (SettingsPanel != null) SettingsPanel.Background = new SolidColorBrush(panelColor) { Opacity = 1.0 };
            ApplyButtonTheme(SettingsPanel, panelColor, light);
            if (FolderTitle != null) FolderTitle.Foreground = textBrush;
            if (SettingsButton != null) SettingsButton.Background = new SolidColorBrush(panelColor);
            if (CloseButton != null) CloseButton.Background = new SolidColorBrush(panelColor);
            if (SettingsCollapseButton != null) SettingsCollapseButton.Background = new SolidColorBrush(panelColor);

            Color thumbColor = Color.FromArgb(175, panelColor.R, panelColor.G, panelColor.B);
            Resources["ScrollbarThumbBrush"] = new SolidColorBrush(thumbColor);
            Resources["OpacityTrackBrush"] = new SolidColorBrush(
                light ? Color.FromRgb(170, 170, 170) : Color.FromRgb(85, 85, 85));

            ApplyBackgroundOpacity();
        }

        private void ApplyButtonTheme(DependencyObject? parent, Color panelColor, bool light)
        {
            if (parent == null) return;

            if (parent is Button button && button != SettingsCollapseButton)
            {
                button.Background = new SolidColorBrush(light
                    ? Color.FromRgb((byte)Math.Max(0, panelColor.R - 18), (byte)Math.Max(0, panelColor.G - 18), (byte)Math.Max(0, panelColor.B - 18))
                    : Color.FromRgb((byte)Math.Min(255, panelColor.R + 18), (byte)Math.Min(255, panelColor.G + 18), (byte)Math.Min(255, panelColor.B + 18)));
                button.Foreground = light ? Brushes.Black : Brushes.White;
            }

            try
            {
                int count = VisualTreeHelper.GetChildrenCount(parent);
                for (int i = 0; i < count; i++)
                    ApplyButtonTheme(VisualTreeHelper.GetChild(parent, i), panelColor, light);
            }
            catch
            {
                // Некоторые текстовые элементы не имеют дочернего visual tree.
            }
        }

        private void FolderNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateFolderTitle();
        }

        private void ChangeBackgroundImage_Click(object sender, RoutedEventArgs e)
        {
            string selected = PickImageWithPreview("Выберите фоновое изображение", backgroundImagePath);
            if (string.IsNullOrEmpty(selected)) return;

            string stored = CopyIconToAppData(selected, "background");
            if (string.IsNullOrEmpty(stored)) return;

            backgroundImagePath = stored;
            RefreshBackgroundImage();
            SaveData();
        }

        private void RemoveBackgroundImage_Click(object sender, RoutedEventArgs e)
        {
            backgroundImagePath = "";
            RefreshBackgroundImage();
            SaveData();
        }

        private void RefreshBackgroundImage()
        {
            BitmapImage? image = LoadImage(backgroundImagePath);
            BackgroundPreview.Source = image;
            ApplyBackgroundOpacity();
        }

        private void ChangeGearImage_Click(object sender, RoutedEventArgs e)
        {
            string selected = PickImageWithPreview("Выберите изображение для шестерёнки", settingsImagePath);
            if (string.IsNullOrEmpty(selected)) return;

            string stored = CopyIconToAppData(selected, "settings");
            if (string.IsNullOrEmpty(stored)) return;

            settingsImagePath = stored;
            RefreshInterfaceImages();
            SaveData();
        }

        private void ChangeCloseImage_Click(object sender, RoutedEventArgs e)
        {
            string selected = PickImageWithPreview("Выберите изображение для крестика", closeImagePath);
            if (string.IsNullOrEmpty(selected)) return;

            string stored = CopyIconToAppData(selected, "close");
            if (string.IsNullOrEmpty(stored)) return;

            closeImagePath = stored;
            RefreshInterfaceImages();
            SaveData();
        }

        private string PickImageWithPreview(string title, string initialPath)
        {
            Window picker = new Window
            {
                Title = title,
                Width = 420,
                Height = 410,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32))
            };

            Grid grid = new Grid { Margin = new Thickness(18) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border previewBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(15)
            };

            Image preview = new Image { Stretch = Stretch.Uniform };
            if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
                preview.Source = LoadImage(initialPath);
            previewBorder.Child = preview;
            Grid.SetRow(previewBorder, 0);
            grid.Children.Add(previewBorder);

            Button chooseButton = new Button
            {
                Content = "Выбрать файл",
                Height = 35,
                Margin = new Thickness(0, 12, 0, 8)
            };
            Grid.SetRow(chooseButton, 1);
            grid.Children.Add(chooseButton);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Button cancelButton = new Button { Content = "Отмена", Width = 90, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
            Button okButton = new Button { Content = "Сохранить", Width = 100, Height = 32, IsDefault = true };
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(okButton);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            string selectedPath = initialPath;
            chooseButton.Click += (s, e) =>
            {
                OpenFileDialog dialog = new OpenFileDialog
                {
                    Title = "Выберите изображение",
                    Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|Все файлы|*.*",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog(picker) == true)
                {
                    BitmapImage image = LoadImage(dialog.FileName);
                    if (image != null)
                    {
                        selectedPath = dialog.FileName;
                        preview.Source = image;
                    }
                    else
                    {
                        MessageBox.Show("Не удалось открыть это изображение.", "AppDock",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            };

            string result = null;
            okButton.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(selectedPath) || !File.Exists(selectedPath))
                {
                    MessageBox.Show("Сначала выберите изображение.", "AppDock",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                result = selectedPath;
                picker.DialogResult = true;
            };

            cancelButton.Click += (s, e) => picker.DialogResult = false;
            picker.Content = grid;
            suppressTrayHide = true;
            try
            {
                picker.ShowDialog();
            }
            finally
            {
                suppressTrayHide = false;
                Activate();
            }
            return result;
        }

        private string CopyIconToAppData(string source, string prefix)
        {
            try
            {
                Directory.CreateDirectory(iconsFolder);
                string extension = Path.GetExtension(source);
                if (string.IsNullOrEmpty(extension)) extension = ".png";

                string destination = Path.Combine(
                    iconsFolder,
                    prefix + "_" + Guid.NewGuid().ToString("N") + extension);

                File.Copy(source, destination, true);
                return destination;
            }
            catch
            {
                MessageBox.Show("Не удалось сохранить изображение в AppDock.", "AppDock",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return "";
            }
        }

        private void EnsureDefaultInterfaceImages()
        {
            try
            {
                if (string.IsNullOrEmpty(settingsImagePath) || !File.Exists(settingsImagePath))
                {
                    string path = Path.Combine(iconsFolder, "default_gear.png");
                    DownloadIfMissing(DefaultGearUrl, path);
                    if (File.Exists(path)) settingsImagePath = path;
                }

                if (string.IsNullOrEmpty(closeImagePath) || !File.Exists(closeImagePath))
                {
                    string path = Path.Combine(iconsFolder, "default_close.png");
                    DownloadIfMissing(DefaultCloseUrl, path);
                    if (File.Exists(path)) closeImagePath = path;
                }
            }
            catch { }
        }

        private void DownloadIfMissing(string url, string destination)
        {
            if (File.Exists(destination)) return;

            try
            {
                using (WebClient client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "AppDock/1.0");
                    client.DownloadFile(url, destination);
                }
            }
            catch { }
        }

        private void RefreshInterfaceImages()
        {
            BitmapImage gear = LoadImage(settingsImagePath);
            BitmapImage close = LoadImage(closeImagePath);

            SettingsButtonImage.Source = gear;
            CloseButtonImage.Source = close;
            GearPreview.Source = gear;
            ClosePreview.Source = close;

            if (gear == null) SettingsButton.Content = "⚙";
            else SettingsButton.Content = SettingsButtonImage;

            if (close == null) CloseButton.Content = "×";
            else CloseButton.Content = CloseButtonImage;
        }

        private void SaveData()
        {
            try
            {
                string? directory = Path.GetDirectoryName(saveFile);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                AppData data = new AppData
                {
                    FolderName = FolderNameBox?.Text ?? "Мои приложения",
                    Opacity = OpacitySlider?.Value ?? 0.87,
                    Color = currentColor,
                    WindowLeft = double.IsNaN(Left) ? (double?)null : Left,
                    WindowTop = double.IsNaN(Top) ? (double?)null : Top,
                    BackgroundImagePath = backgroundImagePath,
                    SettingsImagePath = settingsImagePath,
                    CloseImagePath = closeImagePath,
                    CustomIcons = new Dictionary<string, string>(customIcons, StringComparer.OrdinalIgnoreCase),
                    Apps = new List<AppItem>()
                };

                foreach (Button button in AppsPanel.Children.OfType<Button>())
                {
                    AppButtonData buttonData = GetButtonData(button);
                    if (buttonData == null || string.IsNullOrEmpty(buttonData.ApplicationPath)) continue;

                    string name = "";
                    if (button.Content is StackPanel panel)
                    {
                        TextBlock text = panel.Children.OfType<TextBlock>().FirstOrDefault();
                        if (text != null) name = text.Text;
                    }

                    data.Apps.Add(new AppItem
                    {
                        Path = buttonData.ApplicationPath,
                        Name = name,
                        Row = Grid.GetRow(button),
                        Column = Grid.GetColumn(button),
                        ShortcutPath = buttonData.ShortcutPath,
                        StoredShortcutPath = buttonData.StoredShortcutPath,
                        HiddenByAppDock = buttonData.HiddenByAppDock,
                        OriginalAttributes = buttonData.OriginalAttributes
                    });
                }

                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(saveFile, json);
            }
            catch { }
        }

        private void LoadData()
        {
            try
            {
                if (!File.Exists(saveFile)) return;

                string json = File.ReadAllText(saveFile);
                AppData data = JsonSerializer.Deserialize<AppData>(json);
                if (data == null) return;

                FolderNameBox.Text = data.FolderName;
                UpdateFolderTitle();
                OpacitySlider.Value = Math.Max(0, Math.Min(1, data.Opacity));
                currentColor = string.IsNullOrEmpty(data.Color) ? "#202020" : data.Color;

                if (data.WindowLeft.HasValue && data.WindowTop.HasValue)
                {
                    double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width;
                    double maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height;
                    Left = Math.Max(SystemParameters.VirtualScreenLeft, Math.Min(data.WindowLeft.Value, maxLeft));
                    Top = Math.Max(SystemParameters.VirtualScreenTop, Math.Min(data.WindowTop.Value, maxTop));
                }

                if (!string.IsNullOrEmpty(data.BackgroundImagePath)) backgroundImagePath = data.BackgroundImagePath;
                if (!string.IsNullOrEmpty(data.SettingsImagePath)) settingsImagePath = data.SettingsImagePath;
                if (!string.IsNullOrEmpty(data.CloseImagePath)) closeImagePath = data.CloseImagePath;

                customIcons = data.CustomIcons ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                ApplyTheme();
                RefreshBackgroundImage();

                foreach (AppItem app in data.Apps ?? new List<AppItem>())
                {
                    AddApplicationFromData(app.Path, GetSavedDisplayName(app), app.Row, app.Column,
                        app.ShortcutPath, app.StoredShortcutPath,
                        app.HiddenByAppDock, app.OriginalAttributes);
                }

                // Keep the original three-row layout for 15 or fewer apps.
                // Extra rows are retained only when the collection needs them.
                CompactGridRows();
            }
            catch { }
        }

        private void CloseApp_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveData();
            RemoveKeyboardHook();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            base.OnClosing(e);
        }
    }
}
