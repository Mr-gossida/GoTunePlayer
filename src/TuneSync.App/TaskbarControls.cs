using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;
using TuneSync.App.ViewModels;

namespace TuneSync.App;

/// <summary>The window's taskbar button: its thumbnail shows the playing song's album cover
/// (not a shrunken window), with previous / play-pause / next buttons under it (like Windows
/// Media Player and Spotify) plus full/mini player switching, and the playing song as the window
/// title, which is what the taskbar and Alt+Tab show.</summary>
public static class TaskbarControls
{
    /// <summary>A fourth button that switches between the full and the mini player.</summary>
    public sealed record SwitchButton(string Description, SwitchIcon Icon, Action Click);

    public enum SwitchIcon { ToMini, ToFull }

    public static void Attach(Window window, PlaybackViewModel playback, SwitchButton? switchButton = null)
    {
        var previous = new ThumbButtonInfo { Description = "이전 곡", Command = playback.PlayPreviousCommand };
        var playPause = new ThumbButtonInfo { Command = playback.TogglePlayPauseCommand };
        var next = new ThumbButtonInfo { Description = "다음 곡", Command = playback.PlayNextCommand };
        window.TaskbarItemInfo = new TaskbarItemInfo { ThumbButtonInfos = [previous, playPause, next] };
        ThumbButtonInfo? toggle = null;
        if (switchButton is not null)
        {
            toggle = new ThumbButtonInfo { Description = switchButton.Description };
            toggle.Click += (_, _) => switchButton.Click();
            window.TaskbarItemInfo.ThumbButtonInfos.Add(toggle);
        }

        // the icons in the taskbar's own colour: dark on a light taskbar, white on a dark one
        void Refresh()
        {
            var icons = Icons.For(Icons.TaskbarIsLight());
            previous.ImageSource = icons.Previous;
            next.ImageSource = icons.Next;
            playPause.ImageSource = playback.IsPlaying ? icons.Pause : icons.Play;
            playPause.Description = playback.IsPlaying ? "일시정지" : "재생";
            if (toggle is not null) toggle.ImageSource = switchButton!.Icon == SwitchIcon.ToMini ? icons.ToMini : icons.ToFull;
        }
        Refresh();
        // Windows switched between light and dark (설정 › 개인 설정 › 색)
        UserPreferenceChangedEventHandler themeChanged = (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) window.Dispatcher.BeginInvoke(Refresh);
        };
        SystemEvents.UserPreferenceChanged += themeChanged;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= themeChanged;

        window.SetBinding(Window.TitleProperty, new Binding(nameof(PlaybackViewModel.CurrentFile))
        {
            Source = playback,
            Converter = new TitleConverter(),
        });

        var thumbnail = new CoverThumbnail(window, playback);
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackViewModel.IsPlaying)) Refresh();
            if (e.PropertyName is nameof(PlaybackViewModel.CurrentCover) or nameof(PlaybackViewModel.CurrentFile)) thumbnail.Invalidate();
        };
        playback.PropertyChanged += handler;
        window.Closed += (_, _) => playback.PropertyChanged -= handler;
    }

    /// <summary>The thumbnail toolbar's icons, drawn as filled shapes on a 16-unit grid (the font
    /// icons they replace were thin outlines that read as hollow boxes at this size). Each sits
    /// centered in the same square, so the row looks even. Two sets: white for a dark taskbar,
    /// near-black for a light one (white on a light taskbar leaves only the buttons' frames).</summary>
    internal sealed class Icons
    {
        private static readonly Icons Dark = new(Brushes.White);
        private static readonly Icons Light = new(Frozen(Color.FromRgb(0x1C, 0x1C, 0x1C)));

        public static Icons For(bool lightTaskbar) => lightTaskbar ? Light : Dark;

        /// <summary>Whether the taskbar is light: it follows Windows' own mode (설정 › 개인 설정 ›
        /// 색 › 기본 Windows 모드), not the apps' one.</summary>
        public static bool TaskbarIsLight()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
            }
            catch
            {
                return false;
            }
        }

        public ImageSource Play { get; }
        public ImageSource Pause { get; }
        public ImageSource Previous { get; }
        public ImageSource Next { get; }
        public ImageSource ToMini { get; }
        public ImageSource ToFull { get; }

        private readonly Brush _brush;

        private Icons(Brush brush)
        {
            _brush = brush;
            Play = Make(Filled("M 5,2.6 L 13.4,8 L 5,13.4 Z"));
            Pause = Make(Filled("M 4,2.5 h 2.8 v 11 h -2.8 Z M 9.2,2.5 h 2.8 v 11 h -2.8 Z"));
            Previous = Make(Filled("M 2.8,3 h 2 v 10 h -2 Z M 13.2,3 L 5.6,8 L 13.2,13 Z"));
            Next = Make(Filled("M 11.2,3 h 2 v 10 h -2 Z M 2.8,3 L 10.4,8 L 2.8,13 Z"));
            // a window with the small player in its corner / a small window opening out to full size
            ToMini = Make(
                Outline("M 2,3.5 h 12 v 9 h -12 Z"),
                Filled("M 8,8.2 h 4.4 v 2.9 h -4.4 Z"));
            ToFull = Make(
                Outline("M 2,3.5 h 12 v 9 h -12 Z"),
                Outline("M 6.5,9.5 L 10.5,5.5 M 7.6,5.5 H 10.5 V 8.4"));
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private GeometryDrawing Filled(string path) =>
            new(_brush, new Pen(_brush, 0.6) { LineJoin = PenLineJoin.Round }, Geometry.Parse(path));

        private GeometryDrawing Outline(string path) =>
            new(null, new Pen(_brush, 1.4) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Geometry.Parse(path));

        private static ImageSource Make(params GeometryDrawing[] parts)
        {
            var group = new DrawingGroup();
            // the same square for every icon, so each sits centered at the same size
            group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            foreach (var part in parts) group.Children.Add(part);
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }
    }

    /// <summary>Hands Windows the album cover as the taskbar thumbnail (DWM "iconic" thumbnail):
    /// the window says it has a picture of its own, Windows asks for it at the size it wants, and
    /// gets the cover fitted into it (a music note on a dark square when the song has none).
    /// The full-screen preview while hovering the thumbnail (Aero Peek) is turned off - it would
    /// show the window, not the cover.</summary>
    internal sealed class CoverThumbnail
    {
        private const int WmDwmSendIconicThumbnail = 0x0323;
        private const int WmDwmSendIconicLivePreviewBitmap = 0x0326;
        private const int DwmwaForceIconicRepresentation = 7;
        private const int DwmwaHasIconicBitmap = 10;
        private const int DwmwaDisallowPeek = 11;

        private readonly Window _window;
        private readonly PlaybackViewModel _playback;
        private IntPtr _handle;

        public CoverThumbnail(Window window, PlaybackViewModel playback)
        {
            _window = window;
            _playback = playback;
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Hook();
            else window.SourceInitialized += (_, _) => Hook();
        }

        /// <summary>The thumbnail Windows gets, last made (for checking).</summary>
        public int LastSide { get; private set; }

        private void Hook()
        {
            _handle = new WindowInteropHelper(_window).Handle;
            if (_handle == IntPtr.Zero) return;
            var on = 1;
            DwmSetWindowAttribute(_handle, DwmwaForceIconicRepresentation, ref on, sizeof(int));
            DwmSetWindowAttribute(_handle, DwmwaHasIconicBitmap, ref on, sizeof(int));
            DwmSetWindowAttribute(_handle, DwmwaDisallowPeek, ref on, sizeof(int));
            HwndSource.FromHwnd(_handle)?.AddHook(WndProc);
        }

        /// <summary>The cover changed: Windows asks again the next time it shows the thumbnail.</summary>
        public void Invalidate()
        {
            if (_handle != IntPtr.Zero) DwmInvalidateIconicBitmaps(_handle);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmDwmSendIconicThumbnail)
            {
                var size = lParam.ToInt64();
                int width = (int)((size >> 16) & 0xFFFF), height = (int)(size & 0xFFFF);
                SetThumbnail(width, height);
                handled = true;
            }
            else if (msg == WmDwmSendIconicLivePreviewBitmap)
            {
                handled = true; // peek is off: nothing to give
            }
            return IntPtr.Zero;
        }

        private void SetThumbnail(int maxWidth, int maxHeight)
        {
            if (maxWidth <= 0 || maxHeight <= 0) return;
            // a square cover, as large as fits
            var side = Math.Min(maxWidth, maxHeight);
            var bitmap = CreateBitmap(side, side, Render(side));
            if (bitmap == IntPtr.Zero) return;
            try
            {
                DwmSetIconicThumbnail(_handle, bitmap, 0);
                LastSide = side;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }

        /// <summary>The cover drawn into a square of this many pixels (premultiplied BGRA).</summary>
        public byte[] Render(int side)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var area = new Rect(0, 0, side, side);
                var corner = Math.Max(4, side * 0.04);
                if (_playback.CurrentCover is { } cover)
                {
                    // fill the square (cut to the middle if it isn't one), with softly rounded corners
                    dc.PushClip(new RectangleGeometry(area, corner, corner));
                    var scale = Math.Max(side / cover.Width, side / cover.Height);
                    double w = cover.Width * scale, h = cover.Height * scale;
                    dc.DrawImage(cover, new Rect((side - w) / 2, (side - h) / 2, w, h));
                    dc.Pop();
                }
                else
                {
                    var tint = Application.Current.TryFindResource(PlayerPalette.AccentKey) as Brush ?? Brushes.SlateBlue;
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x36)), null, area, corner, corner);
                    var note = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), side * 0.4, tint, 1.0);
                    dc.DrawText(note, new Point((side - note.Width) / 2, (side - note.Height) / 2));
                }
            }
            var target = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var pixels = new byte[side * side * 4];
            target.CopyPixels(pixels, side * 4, 0);
            return pixels;
        }

        private static IntPtr CreateBitmap(int width, int height, byte[] pixels)
        {
            var info = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // top-down
                Planes = 1,
                BitCount = 32,
            };
            var bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            return bitmap;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ClrUsed;
            public int ClrImportant;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr bitmap, int flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);
    }

    private sealed class TitleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is LibraryFileViewModel file
                ? string.IsNullOrWhiteSpace(file.Artist) ? $"{file.DisplayTitle} · GoTune" : $"{file.DisplayTitle} - {file.Artist} · GoTune"
                : "GoTune Player";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
