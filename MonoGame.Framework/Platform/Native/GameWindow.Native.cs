// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.Framework.Utilities;
using MonoGame.Interop;

namespace Microsoft.Xna.Framework;

internal class NativeGameWindow : GameWindow
{
    internal unsafe MGP_Window* _handle;

    private static readonly Dictionary<nint, NativeGameWindow> _windows = new Dictionary<nint, NativeGameWindow>();

    private NativeGamePlatform _platform;

    private bool _primaryWindow;

    private int _width;
    private int _height;
    private bool? _supportsTextComposition;
    private bool _textInputActive;

    public override unsafe bool SupportsTextComposition
    {
        get
        {
#if BROWSER
            return false;
#else
            if (!_supportsTextComposition.HasValue)
            {
                try { _supportsTextComposition = MGP.Window_SupportsTextComposition(_handle) != 0; }
                catch (EntryPointNotFoundException) { _supportsTextComposition = false; }
            }
            return _supportsTextComposition.Value;
#endif
        }
    }

    public override unsafe bool SetTextInputActive(bool active)
    {
#if BROWSER
        return false;
#else
        if (!SupportsTextComposition) return false;
        if (MGP.Window_SetTextInputActive(_handle, (byte)(active ? 1 : 0)) == 0) return false;
        _textInputActive = active;
        if (!active) OnTextEditing(string.Empty, 0, 0);
        return true;
#endif
    }

    public override unsafe bool SetTextInputRectangle(Rectangle rectangle)
    {
#if BROWSER
        return false;
#else
        if (!SupportsTextComposition) return false;
        var area = TextInputGeometry.ToWindowPoints(rectangle, Scale);
        return MGP.Window_SetTextInputRectangle(_handle, area.X, area.Y, area.Width, area.Height) != 0;
#endif
    }

    internal void CancelTextInputOnFocusLoss()
    {
        if (_textInputActive) SetTextInputActive(false);
    }

    /// <summary>
    /// Physical drawable pixels per logical point for this window (1 unless HiDPI/Retina). The
    /// window/ClientBounds are kept in points; the back buffer and viewport use physical pixels.
    /// Computed on demand from the live drawable-vs-point sizes: the HiDPI density can't be known
    /// while the window is still hidden/unrealized (SDL reports 1.0 then), so caching it at creation
    /// gave a wrong scale on Retina. Reading it live means it's correct once the window is realized.
    /// </summary>
    internal unsafe float Scale
    {
        get
        {
#if BROWSER
            return 1f;
#else
            if (_handle == null || _width <= 0)
                return 1f;
            MGP.Window_GetDrawableSize(_handle, out var drawableWidth, out var drawableHeight);
            return drawableWidth > 0 ? (float)drawableWidth / _width : 1f;
#endif
        }
    }

    public static NativeGameWindow FromHandle(nint handle)
    {
        if (_windows.TryGetValue(handle, out var window))
            return window;

        return null;
    }

    public override unsafe bool AllowUserResizing
    {
        get
        {
            return MGP.Window_GetAllowUserResizing(_handle) == 0 ? false : true;
        }

        set
        {
            MGP.Window_SetAllowUserResizing(_handle, (byte)(value ? 1 : 0));
        }
    }
    public override unsafe bool IsBorderless
    {
        get
        {
            return MGP.Window_GetIsBorderless(_handle) == 0 ? false : true;
        }

        set
        {
            MGP.Window_SetIsBorderless(_handle, (byte)(value ? 1 : 0));
        }
    }

    private bool _isFullScreen;

    public override bool IsFullScreen => _isFullScreen;

    public bool HardwareModeSwitch { get; private set; }

    public override DisplayOrientation CurrentOrientation { get; }

    public override IntPtr Handle { get; }

    public override string ScreenDeviceName { get; }

    public override unsafe Point Position
    {
        get
        {
            int x = 0, y = 0;

            if (!IsFullScreen)
            {
                MGP.Window_GetPosition(_handle, out x, out y);
            }

            return new Point(x, y);
        }
        set
        {
            MGP.Window_SetPosition(_handle, value.X, value.Y);
        }
    }

    public override Rectangle ClientBounds
    {
        get
        {
            var position = Position;
            return new Rectangle(position.X, position.Y, _width, _height);
        }
    }

    public unsafe NativeGameWindow(NativeGamePlatform platform, bool primaryWindow)
    {
        _platform = platform;
        _primaryWindow = primaryWindow;

        // Assume the backbuffer size as the default client size.
        _width = GraphicsDeviceManager.DefaultBackBufferWidth;
        _height = GraphicsDeviceManager.DefaultBackBufferHeight;

        var title = Title == null ? AssemblyHelper.GetDefaultWindowTitle() : Title;

        // Create the window which size may be changed by the platform.
        _handle = MGP.Window_Create(platform.Handle, ref _width, ref _height, title);
        if (_handle == null)
        {
            throw new NoSuitableGraphicsDeviceException("Failed to initialize SDL window!");
        }

        _windows[(nint)_handle] = this;

        var icon = AssemblyHelper.GetDefaultWindowIcon();
        if (icon != null)
        {
            fixed(byte* i = icon)
                MGP.Window_SetIconBitmap(_handle, i, icon.Length);
        }

        Handle = MGP.Window_GetNativeHandle(_handle);

        // NB: the HiDPI backing scale is read on demand via the Scale property (below), not cached
        // here — the density isn't known while the window is still hidden/unrealized.
    }

    internal unsafe void Destroy()
    {
        if (_handle != null)
        {
            _windows.Remove((nint)_handle);
            MGP.Window_Destroy(_handle);
            _handle = null;
        }
    }

    public override void BeginScreenDeviceChange(bool willBeFullScreen)
    {
    }

    public override void EndScreenDeviceChange(string screenDeviceName, int clientWidth, int clientHeight)
    {
    }

    protected internal override void SetSupportedOrientations(DisplayOrientation orientations)
    {
    }

    public unsafe void OnPresentationChanged(PresentationParameters pp)
    {
        if (pp.IsFullScreen && pp.HardwareModeSwitch && IsFullScreen && HardwareModeSwitch)
        {
            // Nothing changed... what do we do here?
        }
        else if (pp.IsFullScreen && (!IsFullScreen || pp.HardwareModeSwitch != HardwareModeSwitch))
        {
            _isFullScreen = pp.IsFullScreen;
            HardwareModeSwitch = pp.HardwareModeSwitch;

            MGP.Window_EnterFullScreen(_handle, (byte)(HardwareModeSwitch ? 1 : 0));
        }
        else if (!pp.IsFullScreen && IsFullScreen)
        {
            _isFullScreen = pp.IsFullScreen;

            MGP.Window_ExitFullScreen(_handle);
        }

        // pp.BackBufferWidth/Height are in physical pixels (the GraphicsDeviceManager scales the
        // requested point size by Scale). The window itself is sized in logical points, so convert
        // back. On non-HiDPI displays Scale is 1, so this is the original behaviour.
        var scale = Scale;
        var pointsWidth = (int)(pp.BackBufferWidth / scale);
        var pointsHeight = (int)(pp.BackBufferHeight / scale);

        if (_width == pointsWidth && _height == pointsHeight)
            return;

        _width = pointsWidth;
        _height = pointsHeight;

        MGP.Window_SetClientSize(_handle, pointsWidth, pointsHeight);
    }

    public unsafe void ClientResize(int width, int height, bool liveResize = false)
    {
        bool changed = _width != width || _height != height;

        _width = width;
        _height = height;

        if (liveResize)
            _platform.Game.GraphicsDevice?.SyncBackBufferToSwapchain();

        if (!changed)
            return;

#if !BROWSER
        if (!liveResize)
            MGP.Window_SetClientSize(_handle, width, height);
#endif

        OnClientSizeChanged();
    }

    protected override unsafe void SetTitle(string title)
    {
        MGP.Window_SetTitle(_handle, title);
    }

    internal unsafe void Show(bool show)
    {
        MGP.Window_Show(_handle, (byte)(show ? 1 : 0));
    }

    internal unsafe void Raise()
    {
        MGP.Window_Raise(_handle);
    }
}
