// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.IO;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGame.Framework.Utilities;

namespace Microsoft.Xna.Framework
{
    internal partial class SdlGameWindow : GameWindow, IDisposable
    {
        public override bool AllowUserResizing
        {
            get { return !IsBorderless && _resizable; }
            set
            {
                var nonResizeableVersion = new Sdl.Version() { Major = 2, Minor = 0, Patch = 4 };

                if (Sdl.version > nonResizeableVersion)
                    Sdl.Window.SetResizable(_handle, value);
                else
                    throw new Exception("SDL " + nonResizeableVersion + " does not support changing resizable parameter of the window after it's already been created, please use a newer version of it.");

                _resizable = value;
            }
        }

        public override Rectangle ClientBounds
        {
            get
            {
                int x = 0, y = 0;
                Sdl.Window.GetPosition(Handle, out x, out y);
                return new Rectangle(x, y, _width, _height);
            }
        }

        public override Point Position
        {
            get
            {
                int x = 0, y = 0;

                if (!IsFullScreen)
                    Sdl.Window.GetPosition(Handle, out x, out y);

                return new Point(x, y);
            }
            set
            {
                Sdl.Window.SetPosition(Handle, value.X, value.Y);
                _wasMoved = true;
            }
        }

        public override DisplayOrientation CurrentOrientation
        {
            get { return DisplayOrientation.Default; }
        }

        public override IntPtr Handle
        {
            get { return _handle; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Resolved through SDL2's <c>SDL_GetWindowWMInfo</c>. Only macOS is wired up so far;
        /// everywhere else this reports zero and callers degrade, which is the contract.
        /// </remarks>
        public override IntPtr PlatformHandle
        {
            get { return GetPlatformNativeWindow(); }
        }

        /// <summary>
        /// The operating system's window object, or zero when this platform has no implementation.
        /// Defined per platform in a partial; this is the fallback for the ones that do not.
        /// </summary>
        private partial IntPtr GetPlatformNativeWindow();

        public override string ScreenDeviceName
        {
            get { return _screenDeviceName; }
        }

        public override bool IsBorderless
        {
            get { return _borderless; }
            set
            {
                Sdl.Window.SetBordered(_handle, value ? 0 : 1);
                _borderless = value;
            }
        }

        public static GameWindow Instance;
        public uint? Id;
        private bool _isSdlFullScreen;

        public override bool IsFullScreen
        {
            get { return _isSdlFullScreen || IsMacNativeFullscreen(); }
        }

        internal readonly Game _game;
        private IntPtr _handle, _icon;
        private bool _disposed;
        private bool _resizable, _borderless, _willBeFullScreen, _mouseVisible, _hardwareSwitch;
        private string _screenDeviceName;
        private int _width, _height;
        private bool _wasMoved, _supressMoved;
        private float _scale = 1f;

        // High-DPI is opt-in (GraphicsDeviceManager.AllowHighDpi) and only meaningful where the OS
        // distinguishes logical points from physical pixels — currently macOS. Everywhere else, or
        // when not opted in, this is false and the window behaves exactly as before.
        private bool HighDpiEnabled =>
            CurrentPlatform.OS == OS.MacOSX &&
            _game.graphicsDeviceManager != null &&
            _game.graphicsDeviceManager.AllowHighDpi;

        /// <summary>
        /// Ratio of the GL drawable (physical pixels) to the window (logical points). 1 unless a
        /// high-DPI back buffer is active; used to convert between the window's point-space and the
        /// back buffer's pixel-space (mouse coordinates, window sizing).
        /// </summary>
        internal float Scale => _scale;

        public SdlGameWindow(Game game)
        {
            _game = game;
            _screenDeviceName = "";

            Instance = this;

            _width = GraphicsDeviceManager.DefaultBackBufferWidth;
            _height = GraphicsDeviceManager.DefaultBackBufferHeight;

            Sdl.SetHint("SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS", "0");
            Sdl.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");

            // when running NUnit tests entry assembly can be null
            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly != null)
            {
                using (
                    var stream =
                        entryAssembly.GetManifestResourceStream(entryAssembly.GetName().Name + ".Icon.bmp") ??
                        entryAssembly.GetManifestResourceStream("Icon.bmp") ??
                        typeof(SdlGameWindow).Assembly.GetManifestResourceStream("MonoGame.bmp"))
                {
                    if (stream != null)
                        using (var br = new BinaryReader(stream))
                        {
                            try
                            {
                                var src = Sdl.RwFromMem(br.ReadBytes((int)stream.Length), (int)stream.Length);
                                _icon = Sdl.LoadBMP_RW(src, 1);
                            }
                            catch { }
                        }
                }
            }

            _handle = Sdl.Window.Create("", 0, 0,
                GraphicsDeviceManager.DefaultBackBufferWidth, GraphicsDeviceManager.DefaultBackBufferHeight,
                Sdl.Window.State.Hidden | Sdl.Window.State.FullscreenDesktop);
        }

        internal void CreateWindow()
        {
            var initflags =
                Sdl.Window.State.OpenGL |
                Sdl.Window.State.Hidden |
                Sdl.Window.State.InputFocus |
                Sdl.Window.State.MouseFocus;

            // Opt-in only: request a high-DPI/Retina drawable so the GL framebuffer is sized in
            // physical pixels rather than logical points. The back buffer/viewport are synced to
            // the drawable size (see ClientResize) so rendering is crisp instead of OS-upscaled.
            if (HighDpiEnabled)
                initflags |= Sdl.Window.State.AllowHighDPI;

            if (_handle != IntPtr.Zero)
                Sdl.Window.Destroy(_handle);

            var winx = Sdl.Window.PosCentered;
            var winy = Sdl.Window.PosCentered;

            // if we are on Linux, start on the current screen
            if (CurrentPlatform.OS == OS.Linux)
            {
                winx |= GetMouseDisplay();
                winy |= GetMouseDisplay();
            }

            _width = GraphicsDeviceManager.DefaultBackBufferWidth;
            _height = GraphicsDeviceManager.DefaultBackBufferHeight;

            _handle = Sdl.Window.Create(
                Title == null ? AssemblyHelper.GetDefaultWindowTitle() : Title,
                winx, winy, _width, _height, initflags
            );

            Id = Sdl.Window.GetWindowId(_handle);

            // Measure the backing scale (physical pixels per logical point). Stays 1 unless a
            // high-DPI drawable was granted, so all the conversions below become no-ops by default.
            _scale = 1f;
            if (HighDpiEnabled)
            {
                Sdl.Window.GetSize(_handle, out var winW, out _);
                Sdl.GL.GetDrawableSize(_handle, out var drawW, out _);
                if (winW > 0 && drawW > 0)
                    _scale = (float)drawW / winW;
            }

            if (_icon != IntPtr.Zero)
                Sdl.Window.SetIcon(_handle, _icon);

            Sdl.Window.SetBordered(_handle, _borderless ? 0 : 1);
            Sdl.Window.SetResizable(_handle, _resizable);

            SetCursorVisible(_mouseVisible);
        }

        ~SdlGameWindow()
        {
            Dispose(false);
        }

        private static int GetMouseDisplay()
        {
            var rect = new Sdl.Rectangle();

            int x, y;
            Sdl.Mouse.GetGlobalState(out x, out y);

            var displayCount = Sdl.Display.GetNumVideoDisplays();
            for (var i = 0; i < displayCount; i++)
            {
                Sdl.Display.GetBounds(i, out rect);

                if (x >= rect.X && x < rect.X + rect.Width &&
                    y >= rect.Y && y < rect.Y + rect.Height)
                {
                    return i;
                }
            }

            return 0;
        }

        public void SetCursorVisible(bool visible)
        {
            _mouseVisible = visible;
            Sdl.Mouse.ShowCursor(visible ? 1 : 0);
        }

        public override void BeginScreenDeviceChange(bool willBeFullScreen)
        {
            _willBeFullScreen = willBeFullScreen;
        }

        public override void EndScreenDeviceChange(string screenDeviceName, int clientWidth, int clientHeight)
        {
            _screenDeviceName = screenDeviceName;

            var prevBounds = ClientBounds;
            var displayIndex = Sdl.Window.GetDisplayIndex(Handle);

            Sdl.Rectangle displayRect;
            Sdl.Display.GetBounds(displayIndex, out displayRect);

            var isNativeMacFullscreen = IsMacNativeFullscreen();
            var wasFullScreen = _isSdlFullScreen || isNativeMacFullscreen;
            var fullScreenChanged = _willBeFullScreen != wasFullScreen;
            var hardwareSwitchChanged = _hardwareSwitch != _game.graphicsDeviceManager.HardwareModeSwitch;
            _hardwareSwitch = _game.graphicsDeviceManager.HardwareModeSwitch;

            if (!_willBeFullScreen && isNativeMacFullscreen)
                ToggleMacNativeFullscreen();

            // set fullscreen to windowed mode
            if (!_willBeFullScreen && fullScreenChanged)
                Sdl.Window.SetFullscreen(Handle, 0);

            // set fullscreen to desktop fullscreen
            if (_willBeFullScreen && !isNativeMacFullscreen && !_hardwareSwitch && (fullScreenChanged || hardwareSwitchChanged))
                Sdl.Window.SetFullscreen(Handle, Sdl.Window.State.FullscreenDesktop);

            // If going to exclusive full-screen mode, force the window to minimize on focus loss (Windows only)
            if (CurrentPlatform.OS == OS.Windows)
            {
                Sdl.SetHint("SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS", _willBeFullScreen && _hardwareSwitch ? "1" : "0");
            }

            if (!_willBeFullScreen || _hardwareSwitch)
            {
                Sdl.Window.SetSize(Handle, clientWidth, clientHeight);
                _width = clientWidth;
                _height = clientHeight;
            }
            else
            {
                _width = displayRect.Width;
                _height = displayRect.Height;
            }

            // set fullscreen to hardware fullscreen
            if (_willBeFullScreen && !isNativeMacFullscreen && _hardwareSwitch  && (fullScreenChanged || hardwareSwitchChanged))
                Sdl.Window.SetFullscreen(Handle, Sdl.Window.State.Fullscreen);

            int ignore, minx = 0, miny = 0;
            Sdl.Window.GetBorderSize(_handle, out miny, out minx, out ignore, out ignore);

            var centerX = Math.Max(prevBounds.X + ((prevBounds.Width - clientWidth) / 2), minx);
            var centerY = Math.Max(prevBounds.Y + ((prevBounds.Height - clientHeight) / 2), miny);

            if (wasFullScreen && !_willBeFullScreen)
            {
                // We need to get the display information again in case
                // the resolution of it was changed.
                Sdl.Display.GetBounds (displayIndex, out displayRect);

                // This centering only occurs when exiting fullscreen
                // so it should center the window on the current display.
                centerX = displayRect.X + displayRect.Width / 2 - clientWidth / 2;
                centerY = displayRect.Y + displayRect.Height / 2 - clientHeight / 2;
            }

            // If this window is resizable, there is a bug in SDL 2.0.4 where
            // after the window gets resized, window position information
            // becomes wrong (for me it always returned 10 8). Solution is
            // to not try and set the window position because it will be wrong.
            if ((Sdl.version > new Sdl.Version() { Major = 2, Minor = 0, Patch = 4 }  || !AllowUserResizing) && !_wasMoved)
                Sdl.Window.SetPosition(Handle, centerX, centerY);

            _isSdlFullScreen = _willBeFullScreen && !isNativeMacFullscreen;
            ClientResize(_width, _height);

            _supressMoved = true;
        }

        internal void Moved()
        {
            if (_supressMoved)
            {
                _supressMoved = false;
                return;
            }

            _wasMoved = true;
        }

        public void ClientResize(int width, int height)
        {
            // The SDL event reports the window size in logical points. With a high-DPI drawable the
            // back buffer must instead match the GL drawable in physical pixels, or the OS upscales
            // a too-small framebuffer and everything looks blurry. Off HiDPI this is a no-op.
            var bbWidth = width;
            var bbHeight = height;
            if (HighDpiEnabled)
            {
                Sdl.GL.GetDrawableSize(Handle, out var dw, out var dh);
                if (dw > 0 && dh > 0)
                {
                    bbWidth = dw;
                    bbHeight = dh;
                    if (width > 0)
                        _scale = (float)dw / width; // refresh if the window moved to another display
                }
            }

            // SDL reports many resize events even if the Size didn't change.
            // Only call the code below if it actually changed.
            if (_game.GraphicsDevice.PresentationParameters.BackBufferWidth == bbWidth &&
                _game.GraphicsDevice.PresentationParameters.BackBufferHeight == bbHeight) {
                return;
            }

            if (_game.GraphicsDevice.RasterizerState.ScissorTestEnable && _game.GraphicsDevice.ScissorRectangle == _game.GraphicsDevice.Viewport.Bounds)
                _game.GraphicsDevice.ScissorRectangle = new Rectangle(0, 0, bbWidth, bbHeight);

            _game.GraphicsDevice.PresentationParameters.BackBufferWidth = bbWidth;
            _game.GraphicsDevice.PresentationParameters.BackBufferHeight = bbHeight;
            _game.GraphicsDevice.Viewport = new Viewport(0, 0, bbWidth, bbHeight);

            // Keep _width/_height in logical points so ClientBounds and window positioning stay correct.
            Sdl.Window.GetSize(Handle, out _width, out _height);

            OnClientSizeChanged();
        }

        protected internal override void SetSupportedOrientations(DisplayOrientation orientations)
        {
            // Nothing to do here
        }

        protected override void SetTitle(string title)
        {
            Sdl.Window.SetTitle(_handle, title);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            Sdl.Window.Destroy(_handle);
            _handle = IntPtr.Zero;

            if (_icon != IntPtr.Zero)
                Sdl.FreeSurface(_icon);

            _disposed = true;
        }
    }
}
