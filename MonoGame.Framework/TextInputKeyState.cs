// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;

namespace Microsoft.Xna.Framework;

internal static class TextInputKeyState
{
    internal static bool IsMacOSCommand(ICollection<Keys> pressedKeys, bool macos)
    {
        return macos && (pressedKeys.Contains(Keys.LeftControl) || pressedKeys.Contains(Keys.RightControl)
            || pressedKeys.Contains(Keys.LeftWindows) || pressedKeys.Contains(Keys.RightWindows));
    }

    internal static bool TrackKeyDown(ICollection<Keys> pressedKeys, Keys key, bool composing, bool macos = false)
    {
        // Older native binaries do not reserve input-source/Spotlight Space before polling.
        if (key == Keys.Space && IsMacOSCommand(pressedKeys, macos))
        {
            pressedKeys.Remove(key);
            return false;
        }

        var modifier = key is Keys.LeftShift or Keys.RightShift or Keys.LeftControl or Keys.RightControl
            or Keys.LeftAlt or Keys.RightAlt or Keys.LeftWindows or Keys.RightWindows;
        // Keep held modifiers after an IME commit, but never leak candidate navigation into polling.
        if ((!composing || modifier) && !pressedKeys.Contains(key))
            pressedKeys.Add(key);
        return !composing;
    }
}
