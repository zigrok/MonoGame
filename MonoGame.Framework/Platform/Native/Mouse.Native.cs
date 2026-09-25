// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using MonoGame.Interop;
using System;

namespace Microsoft.Xna.Framework.Input;

public static partial class Mouse
{
    private static IntPtr PlatformGetWindowHandle()
    {
        // TODO: Multiple window support.

        // Zero rather than a throw when there is no window. GetState() already null-checks
        // PrimaryWindow; this did not, so asking for the handle after the last window closed threw
        // a NullReferenceException instead of answering "none". Callers already treat Zero as "no
        // window to route input to", and a host that tears one Game down while another is still
        // running -- a test suite, a tool that reopens its window -- has nothing else to ask.
        return PrimaryWindow?.Handle ?? IntPtr.Zero;
    }

    private static void PlatformSetWindowHandle(IntPtr windowHandle)
    {
        // TODO: Multiple window support.
    }

    private static unsafe MouseState PlatformGetState(GameWindow window)
    {
        // Mouse events keep this updated for each window.
        return window.MouseState;
    }

    private static unsafe void PlatformSetPosition(int x, int y)
    {
        // TODO: Multiple window support.

        if (PrimaryWindow == null) return;

        PrimaryWindow.MouseState.X = x;
        PrimaryWindow.MouseState.Y = y;

        var window = PrimaryWindow as NativeGameWindow;

        MGP.Mouse_WarpPosition(window._handle, x, y);
    }

    private static unsafe void PlatformSetCursor(MouseCursor cursor)
    {
        // TODO: Multiple window support?

        var window = PrimaryWindow as NativeGameWindow;
        MGP.Window_SetCursor(window._handle, (MGP_Cursor*)cursor.Handle);
    }
}
