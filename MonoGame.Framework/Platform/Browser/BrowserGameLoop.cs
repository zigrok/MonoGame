using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;

namespace MonoGame.Framework;

/// <summary>Advances a native SDL3 browser game from the host's single animation-frame scheduler.</summary>
public static class BrowserGameLoop
{
    [DllImport("mgruntime", EntryPoint = "MGG_Browser_IsContextLost")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool IsContextLost();

    [DllImport("mgruntime", EntryPoint = "MGA_Browser_SetAudioSuspended")]
    private static extern unsafe void SetNativeAudioSuspended(MonoGame.Interop.MGA_System* system,
        [MarshalAs(UnmanagedType.I1)] bool suspended);

    /// <summary>Suspends native voice processing without changing individual playback or volume settings.</summary>
    public static unsafe void SetAudioSuspended(bool suspended)
    {
        var system = Microsoft.Xna.Framework.Audio.SoundEffect.System;
        SetNativeAudioSuspended(system, suspended);
    }

    /// <summary>Runs one nonblocking frame after <see cref="Game.Run()"/>. Returns false after exit.</summary>
    public static bool Tick(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return ((NativeGamePlatform)game.Platform).TickBrowserFrame();
    }

    /// <summary>Updates the drawable pixel size. The host retains ownership of canvas CSS size and DPR.</summary>
    public static void Resize(Game game, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (pixelWidth <= 0 || pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        var manager = game.Services.GetService<IGraphicsDeviceManager>() as GraphicsDeviceManager
            ?? throw new InvalidOperationException("Browser resizing requires GraphicsDeviceManager.");
        manager.PreferredBackBufferWidth = pixelWidth;
        manager.PreferredBackBufferHeight = pixelHeight;
        manager.ApplyChanges();
    }

    private static readonly Queue<(bool Commit, string Text, int Start, int Length)> PendingText = new();

    /// <summary>
    /// Routes browser text through composition-aware commits. SDL's Emscripten backend only reports
    /// per-key text, so an IME needs the host page's editable proxy to call <see cref="ComposeText"/>
    /// and <see cref="CommitText"/>. Read once when the UI binds its text adapter.
    /// </summary>
    public static bool TextCompositionEnabled { get; set; } = true;

    /// <summary>Whether the focused game control currently accepts text.</summary>
    public static bool TextInputActive { get; private set; }

    /// <summary>The focused caret rectangle in drawable pixels, for placing the host's candidate window.</summary>
    public static Rectangle? TextInputRectangle { get; private set; }

    internal static void SetTextInputState(bool active, Rectangle? rectangle)
    {
        TextInputActive = active;
        TextInputRectangle = rectangle;
        if (!active) PendingText.Clear();
    }

    /// <summary>Queues marked (preedit) text; the selection is in Unicode scalars. Empty text cancels.</summary>
    public static void ComposeText(string text, int scalarStart, int scalarLength) =>
        PendingText.Enqueue((false, text ?? string.Empty, scalarStart, scalarLength));

    /// <summary>Queues committed text, delivered once as a commit on the next frame.</summary>
    public static void CommitText(string text)
    {
        if (!string.IsNullOrEmpty(text)) PendingText.Enqueue((true, text, 0, 0));
    }

    internal static void DispatchText(GameWindow window)
    {
        while (PendingText.Count > 0)
        {
            var (commit, text, start, length) = PendingText.Dequeue();
            if (!TextInputActive) continue;
            if (commit) window.OnTextCommitted(text);
            else window.OnTextEditing(text, start, length);
        }
    }
}
