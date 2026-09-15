using System;
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
}
