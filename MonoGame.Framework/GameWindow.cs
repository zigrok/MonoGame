// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using System;
using System.ComponentModel;

namespace Microsoft.Xna.Framework
{
    /// <summary>
    /// The system window used by a <see cref="Game"/>.
    /// </summary>
	public abstract class GameWindow
	{
		#region Properties

	    /// <summary>
	    /// Indicates if users can resize this <see cref="GameWindow"/>.
	    /// </summary>
		[DefaultValue(false)]
		public abstract bool AllowUserResizing { get; set; }

	    /// <summary>
	    /// The client rectangle of the <see cref="GameWindow"/>.
	    /// </summary>
		public abstract Rectangle ClientBounds { get; }

	    internal bool _allowAltF4 = true;

        /// <summary>
        /// Gets or sets a bool that enables usage of Alt+F4 for window closing on desktop platforms. Value is true by default.
        /// </summary>
        public virtual bool AllowAltF4 { get { return _allowAltF4; } set { _allowAltF4 = value; } }

        /// <summary>
        /// The location of this window on the desktop, eg: global coordinate space
        /// which stretches across all screens.
        /// 
        /// May be zero on platforms where it is not supported.
        /// </summary>
        public abstract Point Position { get; set; }

	    /// <summary>
	    /// The display orientation on a mobile device.
	    /// </summary>
		public abstract DisplayOrientation CurrentOrientation { get; }

	    /// <summary>
	    /// The handle to the window used by the backend windowing service.
		///
		/// For WindowsDX this is the Win32 window handle (HWND).
		/// For DesktopGL this is the SDL window handle.
	    /// </summary>
		public abstract IntPtr Handle { get; }

	    /// <summary>
	    /// The operating system's own window object, as opposed to the windowing backend's:
	    /// <c>NSWindow*</c> on macOS, <c>HWND</c> on Windows, the X11 window id on Linux.
	    /// <para>
	    /// <see cref="Handle"/> is whatever the backend uses - an <c>SDL_Window*</c> under SDL - so
	    /// it is the wrong thing to hand to an OS API. This is what a platform integration that
	    /// talks to the operating system rather than to the backend needs: an accessibility
	    /// adapter, a native menu, an IME panel.
	    /// </para>
	    /// <para>
	    /// <see cref="IntPtr.Zero"/> when the backend does not expose one or there is no real
	    /// window, which is the normal answer under a headless runtime or a dummy video driver.
	    /// Callers must handle that rather than assume a window exists.
	    /// </para>
	    /// </summary>
		public virtual IntPtr PlatformHandle => IntPtr.Zero;

	    /// <summary>
	    /// Whether the window is on screen.
	    /// <para>
	    /// <see cref="Game.Run()"/> shows the window itself, so a normal game never sets this. It
	    /// exists for a host that drives the loop with <see cref="Game.RunOneFrame"/> or
	    /// <see cref="Game.Tick"/> instead: those never enter the run loop, so without this the
	    /// window stays hidden for the life of the process and the game appears not to start.
	    /// </para>
	    /// <para>
	    /// Backends that cannot hide or show a window report true and ignore writes.
	    /// </para>
	    /// </summary>
		public virtual bool IsVisible
		{
			get { return true; }
			set { }
		}

	    /// <summary>
	    /// The name of the screen the window is currently on.
	    /// </summary>
		public abstract string ScreenDeviceName { get; }

		private string _title;
        /// <summary>
        /// Gets or sets the title of the game window.
        /// </summary>
        public string Title {
			get { return _title; }
			set {
				if (_title != value) {
					SetTitle(value);
					_title = value;
				}
			}
		}

        /// <summary>
        /// Determines whether the border of the window is visible. Currently only supported on the WindowsDX and DesktopGL platforms.
        /// </summary>
        /// <exception cref="System.NotImplementedException">
        /// Thrown when trying to use this property on a platform other than WinowsDX or DesktopGL.
        /// </exception>
        public virtual bool IsBorderless
        {
            get
            {
                return false;
            }
            set
            {
                throw new NotImplementedException();
            }
        }

		/// <summary>
		/// Gets whether the platform window is currently fullscreen.
		/// </summary>
		public virtual bool IsFullScreen
		{
			get { return false; }
		}

        internal MouseState MouseState;
	    internal TouchPanelState TouchPanelState;

	    /// <summary>
	    /// Create a <see cref="GameWindow"/>.
	    /// </summary>
        protected GameWindow()
        {
            TouchPanelState = new TouchPanelState(this);
        }

		#endregion Properties

		#region Events

	    /// <summary>
	    /// Raised when the user resized the window or the window switches from fullscreen mode to
	    /// windowed mode or vice versa.
	    /// </summary>
		public event EventHandler<EventArgs> ClientSizeChanged;

	    /// <summary>
	    /// Raised when <see cref="CurrentOrientation"/> changed.
	    /// </summary>
		public event EventHandler<EventArgs> OrientationChanged;

	    /// <summary>
	    /// Raised when <see cref="ScreenDeviceName"/> changed.
	    /// </summary>
		public event EventHandler<EventArgs> ScreenDeviceNameChanged;

#if WINDOWS || DESKTOPGL|| ANGLE || NATIVE

        /// <summary>
		/// Use this event to user text input.
		/// 
		/// This event is not raised by noncharacter keys except control characters such as backspace, tab, carriage return and escape.
		/// This event also supports key repeat.
		/// </summary>
		/// <remarks>
		/// This event is only supported on desktop platforms.
		/// </remarks>
		public event EventHandler<TextInputEventArgs> TextInput;

        internal bool IsTextInputHandled { get { return TextInput != null; } }

        /// <summary>Whether this window provides native preedit, atomic commits and candidate placement.</summary>
        public virtual bool SupportsTextComposition => false;

        /// <summary>Complete committed text, delivered once before the legacy per-character TextInput events.</summary>
        public event Action<string> TextCommitted;

        /// <summary>Uncommitted marked text with a UTF-16 selection range. Empty text cancels composition.</summary>
        public event Action<string, int, int> TextEditing;

        /// <summary>Opts into native composition and starts or stops the focused text-input session.</summary>
        public virtual bool SetTextInputActive(bool active) => false;

        /// <summary>Positions native candidates at a caret rectangle in drawable pixels, not window points.</summary>
        public virtual bool SetTextInputRectangle(Rectangle rectangle) => false;

        internal bool HasTextComposition { get; private set; }

        internal void OnTextEditing(string text, int scalarStart, int scalarLength)
        {
            text = text ?? string.Empty;
            HasTextComposition = text.Length != 0;
            var start = TextCompositionIndex.ToUtf16(text, scalarStart < 0 ? int.MaxValue : scalarStart);
            var end = scalarStart < 0 ? start : TextCompositionIndex.ToUtf16(text,
                (int)Math.Min(int.MaxValue, (long)scalarStart + Math.Max(0, scalarLength)));
            TextEditing?.Invoke(text, start, Math.Max(0, end - start));
        }

        internal void OnTextCommitted(string text)
        {
            HasTextComposition = false;
            TextCommitted?.Invoke(text);
            // Existing character subscribers retain their contract, including UTF-16 surrogate pairs.
            foreach (var character in text)
                OnTextInput(new TextInputEventArgs(character, Keys.None));
        }

        /// <summary>
        /// Buffered keyboard KeyDown event.
        /// </summary>
		public event EventHandler<InputKeyEventArgs> KeyDown;

        /// <summary>
        /// Buffered keyboard KeyUp event.
        /// </summary>
        public event EventHandler<InputKeyEventArgs> KeyUp;

#endif

        /// <summary>
        /// This event is raised when user drops a file into the game window
        /// </summary>
        /// <remarks>
        /// This event is only supported on desktop platforms.
        /// </remarks>
        public event EventHandler<FileDropEventArgs> FileDrop;

        #endregion Events

        /// <summary>
        /// Called before a game switches from windowed to full screen mode or vice versa.
        /// </summary>
        /// <param name="willBeFullScreen">Indicates what mode the game will switch to.</param>
        public abstract void BeginScreenDeviceChange (bool willBeFullScreen);

	    /// <summary>
	    /// Called when a transition from windowed to full screen or vice versa ends, or when
	    /// the <see cref="Graphics.GraphicsDevice"/> is reset.
	    /// </summary>
	    /// <param name="screenDeviceName">Name of the screen to move the window to.</param>
	    /// <param name="clientWidth">The new width of the client rectangle.</param>
	    /// <param name="clientHeight">The new height of the client rectangle.</param>
		public abstract void EndScreenDeviceChange (
			string screenDeviceName, int clientWidth, int clientHeight);

	    /// <summary>
	    /// Called when a transition from windowed to full screen or vice versa ends, or when
	    /// the <see cref="Graphics.GraphicsDevice"/> is reset.
	    /// </summary>
	    /// <param name="screenDeviceName">Name of the screen to move the window to.</param>
		public void EndScreenDeviceChange (string screenDeviceName)
		{
			EndScreenDeviceChange(screenDeviceName, ClientBounds.Width, ClientBounds.Height);
		}

	    /// <summary>
	    /// Called when the window gains focus.
	    /// </summary>
		protected void OnActivated ()
		{
		}

		internal void OnClientSizeChanged ()
		{
            EventHelpers.Raise(this, ClientSizeChanged, EventArgs.Empty);
		}

	    /// <summary>
	    /// Called when the window loses focus.
	    /// </summary>
		protected void OnDeactivated ()
		{
		}
         
	    /// <summary>
	    /// Called when <see cref="CurrentOrientation"/> changed. Raises the <see cref="OnOrientationChanged"/> event.
	    /// </summary>
		protected void OnOrientationChanged ()
		{
            EventHelpers.Raise(this, OrientationChanged, EventArgs.Empty);
		}

        /// <summary>
        /// Called when the window needs to be painted.
        /// </summary>
		protected void OnPaint ()
		{
		}

	    /// <summary>
	    /// Called when <see cref="ScreenDeviceName"/> changed. Raises the <see cref="ScreenDeviceNameChanged"/> event.
	    /// </summary>
		protected void OnScreenDeviceNameChanged ()
		{
            EventHelpers.Raise(this, ScreenDeviceNameChanged, EventArgs.Empty);
		}

#if WINDOWS || DESKTOPGL || ANGLE || NATIVE

	    /// <summary>
	    /// Called when the window receives text input. Raises the <see cref="TextInput"/> event.
	    /// </summary>
	    /// <param name="e">Parameters to the <see cref="TextInput"/> event.</param>
		internal void OnTextInput(TextInputEventArgs e)
		{
            EventHelpers.Raise(this, TextInput, e);
		}
        internal void OnKeyDown(InputKeyEventArgs e)
	    {
            EventHelpers.Raise(this, KeyDown, e);
	    }
        internal void OnKeyUp(InputKeyEventArgs e)
	    {
            EventHelpers.Raise(this, KeyUp, e);
	    }
#endif

        internal void OnFileDrop(FileDropEventArgs e)
        {
            EventHelpers.Raise(this, FileDrop, e);
        }

        /// <summary>
        /// Sets the supported display orientations.
        /// </summary>
        /// <param name="orientations">Supported display orientations</param>
        protected internal abstract void SetSupportedOrientations (DisplayOrientation orientations);

	    /// <summary>
	    /// Set the title of this window to the given string.
	    /// </summary>
	    /// <param name="title">The new title of the window.</param>
		protected abstract void SetTitle (string title);

#if DIRECTX && WINDOWS
        /// <summary>
        /// Create an additional window. Only available in WindowsDX.
        /// </summary>
        /// <param name="game">a reference to the game class.</param>
        /// <param name="width">The width of the new window.</param>
        /// <param name="height">The height of the new window.</param>
        /// <param name="show">Display the window imediately. Optional<see cref="Show"/></param>
        /// <remarks> The visibility default follows the upstream defaults. </remarks>
        public static GameWindow Create(Game game, int width, int height, bool? show = null)
        {
            var window = new MonoGame.Framework.WinFormsGameWindow((MonoGame.Framework.WinFormsGamePlatform)game.Platform);
            window.Initialize(width, height);
            if(show.HasValue)
                window.Form.Visible = show.Value;

            return window;
        }
        
        /// <summary>
        /// Make a new window visible. Only available in WindowsDX.
        /// </summary>
        public void Show()
        {
            // sanity check for cast
            if (this is MonoGame.Framework.WinFormsGameWindow)
            {
                ((MonoGame.Framework.WinFormsGameWindow)this).Form.Visible = true;
            }
        }
#endif
    }
}
