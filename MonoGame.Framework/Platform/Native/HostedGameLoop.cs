// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using Microsoft.Xna.Framework;

namespace MonoGame.Framework;

/// <summary>
/// Drives a game one frame at a time from a host that owns the loop, rather than handing control to
/// <see cref="Game.Run()"/>.
/// </summary>
/// <remarks>
/// <para>
/// The desktop counterpart of <c>BrowserGameLoop</c>, and it exists for the same reason: something
/// other than MonoGame decides when a frame happens. A UI test runner is the motivating case — it
/// wants to step the game, assert, and step again.
/// </para>
/// <para>
/// <see cref="Game.Tick"/> on its own is not enough. The window system is serviced inside the run
/// loop, so a host that only calls Tick gets a window the OS never maps: present in the window
/// server at zero size, showing nothing. <see cref="Tick"/> here does the whole iteration.
/// </para>
/// </remarks>
public static class HostedGameLoop
{
    /// <summary>
    /// Shows the game's window. <see cref="Game.Run()"/> does this itself; a host driving the loop
    /// has to ask, or the window stays hidden for the life of the process.
    /// </summary>
    public static void Show(Game game)
    {
        if (game == null) throw new ArgumentNullException(nameof(game));
        game.Window.IsVisible = true;

        // Raised as well as shown, matching what the run loop does. Showing alone leaves the window
        // unordered: the window server knows about it but never gives it a size or puts it on a
        // screen, which looks from the outside exactly like the window not existing.
        ((NativeGameWindow)game.Window).Raise();
    }

    /// <summary>
    /// Services the window system, then runs one update and draw.
    /// </summary>
    /// <returns>False once the game has exited and should not be ticked again.</returns>
    public static bool Tick(Game game)
    {
        if (game == null) throw new ArgumentNullException(nameof(game));
        return ((NativeGamePlatform)game.Platform).TickHostedFrame();
    }
}
