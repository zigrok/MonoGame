// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Runtime.InteropServices;
using MonoGame.Framework.Utilities;

namespace Microsoft.Xna.Framework
{
    internal partial class SdlGameWindow
    {
        private bool IsMacNativeFullscreen()
        {
            if (CurrentPlatform.OS != OS.MacOSX)
                return false;

            try
            {
                var nsWindow = GetMacNativeWindow();
                if (nsWindow == IntPtr.Zero)
                    return false;

                var styleMask = objc_msgSend_nuint(nsWindow, sel_registerName("styleMask"));
                const ulong nsWindowStyleMaskFullScreen = 1UL << 14;
                return (styleMask.ToUInt64() & nsWindowStyleMaskFullScreen) != 0;
            }
            catch
            {
                return false;
            }
        }

        private void ToggleMacNativeFullscreen()
        {
            if (CurrentPlatform.OS != OS.MacOSX)
                return;

            try
            {
                var nsWindow = GetMacNativeWindow();
                if (nsWindow != IntPtr.Zero)
                    objc_msgSend_performSelectorOnMainThread(
                        nsWindow,
                        sel_registerName("performSelectorOnMainThread:withObject:waitUntilDone:"),
                        sel_registerName("toggleFullScreen:"),
                        IntPtr.Zero,
                        1);
            }
            catch
            {
                // Best effort. SDL will still receive the user's native resize events and recover.
            }
        }

        /// <summary>
        /// The window's <c>NSWindow*</c>, which is what an OS integration needs -- an accessibility
        /// adapter, a native menu, an IME panel. Zero on any other platform.
        /// </summary>
        private partial IntPtr GetPlatformNativeWindow()
        {
            return CurrentPlatform.OS == OS.MacOSX ? GetMacNativeWindow() : IntPtr.Zero;
        }

        private IntPtr GetMacNativeWindow()
        {
            var info = new Sdl.Window.SDL_SysWMinfo { version = Sdl.version };
            if (!Sdl.Window.GetWindowWMInfo(Handle, ref info) || info.subsystem != Sdl.Window.SysWMType.Cocoa)
                return IntPtr.Zero;

            return info.window;
        }

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern UIntPtr objc_msgSend_nuint(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_performSelectorOnMainThread(
            IntPtr receiver,
            IntPtr selector,
            IntPtr selectorToPerform,
            IntPtr argument,
            byte waitUntilDone);
    }
}