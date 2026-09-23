// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MonoGame.Interop;
using System.Threading;

namespace Microsoft.Xna.Framework;

partial class GamePlatform
{
    internal static GamePlatform PlatformCreate(Game game) => new NativeGamePlatform(game);
}

class NativeGamePlatform : GamePlatform
{
    private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    internal unsafe MGP_Platform* Handle;

    private static unsafe MGG_GraphicsSystem* _system;

    private NativeGameWindow _window;

    private readonly List<string> _dropList = new List<string>(64);

    private int _isExiting;

#if !BROWSER
    private MGP.LiveResizeCallback _liveResizeCallback;
    private LiveResizeFrameDispatcher _liveResizeFrames;
    private bool? _deferredDispose;
#endif

    public unsafe NativeGamePlatform(Game game) : base(game)
    {
        GameRunBehavior behavior;
        Handle = MGP.Platform_Create(out behavior);
        if (Handle == null)
        {
            throw new NoSuitableGraphicsDeviceException("Failed to initialize SDL platform!");
        }

        DefaultRunBehavior = behavior;

        _window = new NativeGameWindow(this, true);

        Window = _window;

        Mouse.WindowHandle = _window.Handle;
        MessageBox._window = _window._handle;
        GamePad.Handle = Handle;
        OnIsMouseVisibleChanged();
    }

    internal static unsafe MGG_GraphicsSystem* GraphicsSystem
    {
        get
        {
            if (_system == null)
            {
                _system = MGG.GraphicsSystem_Create();
                if (_system == null)
                {
                    throw new NoSuitableGraphicsDeviceException("Failed to initialize graphics system!");
                }
            }

            return _system;
        }
    }

    public override GameRunBehavior DefaultRunBehavior { get; }

    public override unsafe void Exit()
    {
        Interlocked.Increment(ref _isExiting);
    }

    public override unsafe void RunLoop()
    {
#if BROWSER
        throw new PlatformNotSupportedException("Browser games must run asynchronously using BrowserGameLoop.Tick from requestAnimationFrame.");
#else
        _window.Show(true);
        _window.Raise();

        StartLiveResize();
        try
        {
            while (true)
            {
                if (_liveResizeFrames != null)
                    _liveResizeFrames.IsPolling = true;
                try
                {
                    PollEvents();
                }
                finally
                {
                    if (_liveResizeFrames != null)
                        _liveResizeFrames.IsPolling = false;
                    if (_deferredDispose.HasValue)
                    {
                        var disposing = _deferredDispose.Value;
                        _deferredDispose = null;
                        Dispose(disposing);
                    }
                }

                _liveResizeFrames?.ThrowIfFaulted();
                if (_window == null || (_isExiting > 0 && ShouldExit()))
                    break;

                Game.Tick();

                Threading.Run();

                if (_isExiting > 0 && ShouldExit())
                    break;
                else
                    _isExiting = 0;
            }
        }
        finally
        {
            StopLiveResize();
        }
#endif
    }

#if !BROWSER
    private unsafe void StartLiveResize()
    {
        if (!IsMacOS)
            return;

        _liveResizeFrames = new LiveResizeFrameDispatcher();
        _liveResizeCallback = (handle, width, height) => _liveResizeFrames.TryRun(() =>
        {
            if (_isExiting > 0 || _window == null || handle != (nint)_window._handle)
                return;

            _window.ClientResize(width, height, liveResize: true);
            if (!_liveResizeFrames.IsStopped)
                Game.Tick(waitForNextFrame: false);
        });

        try
        {
            if (MGP.Platform_SetLiveResizeCallback(Handle, Marshal.GetFunctionPointerForDelegate(_liveResizeCallback)) != 0)
                return;
        }
        catch (EntryPointNotFoundException)
        {
            // Older native runtimes keep their ordinary post-drag event loop.
        }
        _liveResizeCallback = null;
        _liveResizeFrames = null;
    }

    private unsafe void StopLiveResize()
    {
        _liveResizeFrames?.Stop();
        if (_liveResizeCallback == null)
            return;
        if (Handle != null)
            MGP.Platform_SetLiveResizeCallback(Handle, 0);
        _liveResizeCallback = null;
    }
#endif

#if BROWSER
    private bool _browserRunning;

    internal bool TickBrowserFrame()
    {
        if (!_browserRunning)
            return false;
        if (MonoGame.Framework.BrowserGameLoop.IsContextLost())
        {
            _browserRunning = false;
            throw new InvalidOperationException("The WebGL2 context was lost. Restart the page to recreate the native graphics resources.");
        }
        PollEvents();
        if (_isExiting > 0 && ShouldExit())
        {
            _browserRunning = false;
            RaiseAsyncRunLoopEnded();
            return false;
        }
        _isExiting = 0;
        Microsoft.Xna.Framework.Media.Song.PumpBrowserAudio();
        Game.Tick();
        Threading.Run();
        return true;
    }
#endif

#if !BROWSER
    /// <summary>
    /// Runs one iteration of what <see cref="RunLoop"/> does, for a host that owns the loop itself.
    /// </summary>
    /// <remarks>
    /// <see cref="Game.Tick"/> alone is not a frame. The window system is serviced by PollEvents,
    /// which only RunLoop calls, so a host that drives Tick directly gets a window that is never
    /// mapped, never resized and never told anything by the OS -- it exists in the window server at
    /// zero size and nothing is ever drawn where a person can see it.
    /// </remarks>
    /// <returns>False once the game has exited.</returns>
    internal bool TickHostedFrame()
    {
        PollEvents();

        if (_window == null || (_isExiting > 0 && ShouldExit())) return false;

        Game.Tick();
        Threading.Run();

        if (_isExiting > 0 && ShouldExit()) return false;

        _isExiting = 0;
        return true;
    }
#endif

    private unsafe void PollEvents()
    {
        MGP_Event event_;
        while (MGP.Platform_PollEvent(Handle, out event_) != 0)
        {
#if !BROWSER
            if (_liveResizeFrames?.IsStopped == true)
                break;
#endif
            switch (event_.Type)
            {
                case EventType.Quit:
                    Game.Exit();
                    break;

                case EventType.WindowGainedFocus:
                    IsActive = true;
                    break;

                case EventType.WindowLostFocus:
                    Keyboard.Keys.Clear();
                    NativeGameWindow.FromHandle(event_.Window.Window)?.CancelTextInputOnFocusLoss();
                    IsActive = false;
                    break;

                case EventType.WindowResized:
                { 
                    var window = NativeGameWindow.FromHandle(event_.Window.Window);
                    if (window != null)
                        window.ClientResize(event_.Window.Data1, event_.Window.Data2);
                    break;
                }

                case EventType.WindowClose:
                { 
                    var window = NativeGameWindow.FromHandle(event_.Window.Window);
                    if (Window == window)
                        Game.Exit();
                    break;
                }

                case EventType.KeyDown:
                {
                    var window = NativeGameWindow.FromHandle(event_.Key.Window);
                    var key = event_.Key.Key;
                    var character = (char)event_.Key.Character;

                    if (!TextInputKeyState.TrackKeyDown(Keyboard.Keys, key, window?.HasTextComposition == true, IsMacOS))
                        break;

                    if (window != null)
                    { 
                        window.OnKeyDown(new InputKeyEventArgs(key));

                        if (window.IsTextInputHandled && char.IsControl(character)
                            && !TextInputKeyState.IsMacOSCommand(Keyboard.Keys, IsMacOS))
                            window.OnTextInput(new TextInputEventArgs(character, key));
                    }

                    break;
                }

                case EventType.KeyUp:
                {
                    var window = NativeGameWindow.FromHandle(event_.Key.Window);
                    var key = event_.Key.Key;

                    Keyboard.Keys.Remove(key);

                    if (window != null)
                        window.OnKeyUp(new InputKeyEventArgs(key));

                    break;
                }

                case EventType.TextInput:
                {
                    // The native bridge filters direct shortcut text before either text event path.
                    var window = NativeGameWindow.FromHandle(event_.Key.Window);
                    if (window != null && window.IsTextInputHandled)
                    {
                        var key = event_.Key.Key;
                        var character = (char)event_.Key.Character;
                        window.OnTextInput(new TextInputEventArgs(character, key));
                    }
                    break;
                }

#if !BROWSER
                case EventType.TextEditing:
                case EventType.TextCommit:
                {
                    var window = NativeGameWindow.FromHandle(event_.Window.Window);
                    if (window == null) break;
                    var text = Marshal.PtrToStringUTF8(MGP.Platform_GetTextEvent(Handle)) ?? string.Empty;
                    if (event_.Type == EventType.TextEditing)
                        window.OnTextEditing(text, event_.Window.Data1, event_.Window.Data2);
                    else
                        // Filtering by held modifiers here would discard genuine IME commits.
                        window.OnTextCommitted(text);
                    break;
                }
#endif

                case EventType.MouseMove:
                {
                    var window = NativeGameWindow.FromHandle(event_.MouseMove.Window);
                    if (window != null)
                    {
                        // SDL reports the cursor in logical points; scale to physical pixels so
                        // hit-testing matches the (high-DPI) back buffer. Scale is 1 off HiDPI.
                        window.MouseState.X = (int)(event_.MouseMove.X * window.Scale);
                        window.MouseState.Y = (int)(event_.MouseMove.Y * window.Scale);
                    }
                    break;
                }

                case EventType.MouseWheel:
                {
                    var window = NativeGameWindow.FromHandle(event_.MouseWheel.Window);
                    if (window != null)
                    {
                        window.MouseState.ScrollWheelValue += event_.MouseWheel.Scroll;
                        window.MouseState.HorizontalScrollWheelValue += event_.MouseWheel.ScrollH;
                    }
                    break;
                }

                case EventType.MouseButtonUp:
                case EventType.MouseButtonDown:
                {
                    var window = NativeGameWindow.FromHandle(event_.MouseButton.Window);
                    if (window != null)
                    {
                        window.MouseState.X = (int)(event_.MouseButton.X * window.Scale);
                        window.MouseState.Y = (int)(event_.MouseButton.Y * window.Scale);
                        var state = event_.Type == EventType.MouseButtonDown ? ButtonState.Pressed : ButtonState.Released;

                        switch (event_.MouseButton.Button)
                        {
                            case MouseButton.Left:
                                window.MouseState.LeftButton = state;
                                break;
                            case MouseButton.Right:
                                window.MouseState.RightButton = state;
                                break;
                            case MouseButton.Middle:
                                window.MouseState.MiddleButton = state;
                                break;
                            case MouseButton.X1:
                                window.MouseState.XButton1 = state;
                                break;
                            case MouseButton.X2:
                                window.MouseState.XButton2 = state;
                                break;
                         }
                    }
                    break;
                }

                case EventType.ControllerAdded:
                {
                    GamePad.Add(event_.Controller.Id);
                    break;
                }

                case EventType.ControllerRemoved:
                {
                    GamePad.Remove(event_.Controller.Id);
                    break;
                }

                case EventType.ControllerStateChange:
                {
                    GamePad.ChangeState(event_.Controller.Id, event_.Timestamp, event_.Controller.Input, event_.Controller.Value);
                    break;
                }

                case EventType.DropFile:
                {
                    var file = Marshal.PtrToStringUTF8(event_.Drop.File);
                    _dropList.Add(file);
                    break;
                }

                case EventType.DropComplete:
                {
                    var window = NativeGameWindow.FromHandle(event_.Drop.Window);
                    if (window != null )
                        window.OnFileDrop(new FileDropEventArgs(_dropList.ToArray()));
                    _dropList.Clear();
                    break;
                }
            }
        }
    }

    private bool ShouldExit()
    {
        if (    Keyboard.Keys.Contains(Keys.F4) &&
                (   Keyboard.Keys.Contains(Keys.LeftAlt) ||
                    Keyboard.Keys.Contains(Keys.RightAlt)))
        {
            return Window.AllowAltF4;
        }

        return true;
    }

    public override void Present()
    {
        if (Game.GraphicsDevice != null)
            Game.GraphicsDevice.Present();
    }

    public override unsafe void StartRunLoop()
    {
#if BROWSER
        MGP.Platform_StartRunLoop(Handle);
        _browserRunning = true;
#else
        MGP.Platform_StartRunLoop(Handle);
#endif
    }

    public override unsafe void BeforeInitialize()
    {
        var gdm = Game.graphicsDeviceManager;
        if (gdm == null)
        {
            // TODO: ???
        }
        else
        {
            var pp = Game.GraphicsDevice.PresentationParameters;
            _window.OnPresentationChanged(pp);
        }

        base.BeforeInitialize();        
    }

    public override unsafe bool BeforeRun()
    {        
        return MGP.Platform_BeforeRun(Handle) == 0 ? false : true;
    }

    public override unsafe bool BeforeUpdate(GameTime gameTime)
    {
        return MGP.Platform_BeforeUpdate(Handle) == 0 ? false : true;
    }

    public override unsafe bool BeforeDraw(GameTime gameTime)
    {
        var canDraw = MGP.Platform_BeforeDraw(Handle) != 0;

        // The native swapchain may have been recreated to match the surface (e.g. after a fullscreen
        // aspect change). Sync the managed back buffer/viewport to that actual size so rendering
        // fills the surface correctly instead of being stretched.
        if (canDraw)
            Game.GraphicsDevice?.SyncBackBufferToSwapchain();

        return canDraw;
    }

    public override unsafe void EnterFullScreen()
    {
    }

    public override unsafe void ExitFullScreen()
    {
    }

    public override void BeginScreenDeviceChange(bool willBeFullScreen)
    {
    }
    public override void EndScreenDeviceChange(string screenDeviceName, int clientWidth, int clientHeight)
    {

    }

    internal override void OnPresentationChanged(PresentationParameters pp)
    {
        _window.OnPresentationChanged(pp);
    }

    protected override unsafe void OnIsMouseVisibleChanged()
    {
        MGP.Mouse_SetVisible(Handle, (byte)(IsMouseVisible ? 1 : 0));
    }

    protected unsafe override void Dispose(bool disposing)
    {
#if !BROWSER
        if (_liveResizeFrames?.IsPolling == true)
        {
            // Cocoa/SDL still owns the window and platform until the outer native poll unwinds.
            _liveResizeFrames.Stop();
            _deferredDispose = (_deferredDispose ?? false) || disposing;
            return;
        }
        StopLiveResize();
#endif

        if (_window != null)
        {
            _window.Destroy();
            _window = null;
            Window = null;
        }
        
        if (_system != null)
        {
            MGG.GraphicsSystem_Destroy(_system);
            _system = null;
        }

        if (Handle != null)
        {
            MGP.Platform_Destroy(Handle);
            Handle = null;
        }

        base.Dispose(disposing);
    }
}
