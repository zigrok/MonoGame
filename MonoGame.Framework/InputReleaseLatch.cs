// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;

namespace Microsoft.Xna.Framework;

/// <summary>
/// Tracks presses seen during one event drain so a release drained in the same frame can be
/// held until the next frame. Polled state would otherwise show neither the press nor the release.
/// </summary>
internal sealed class InputReleaseLatch
{
    private readonly HashSet<Keys> _pressedKeys = new HashSet<Keys>();
    private int _pressedButtons;

    internal void BeginDrain()
    {
        _pressedKeys.Clear();
        _pressedButtons = 0;
    }

    internal void KeyDown(Keys key) => _pressedKeys.Add(key);

    internal void ButtonDown(int button) => _pressedButtons |= 1 << button;

    internal bool DefersKeyUp(Keys key) => _pressedKeys.Contains(key);

    internal bool DefersButtonUp(int button) => (_pressedButtons & 1 << button) != 0;
}
