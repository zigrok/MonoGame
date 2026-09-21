// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

#pragma once

// Cocoa sends a pending raw key immediately before directly interpreted text,
// but clears that key before committing marked text.
struct MGP_TextInputState
{
    bool shortcut_text = false;
    bool space_down = false;
    bool space_suppressed = false;
    bool release_space = false;

    bool KeyDown(bool macos, bool commandOrControl, bool modifier, bool space)
    {
        shortcut_text = macos && commandOrControl && !modifier;
        release_space = space && shortcut_text && space_down;
        if (space)
        {
            space_down = !shortcut_text;
            space_suppressed = shortcut_text;
        }
        return !(shortcut_text && space);
    }

    bool KeyUp(bool space)
    {
        Clear();
        if (!space)
            return true;
        const bool accepted = !space_suppressed;
        space_down = false;
        space_suppressed = false;
        return accepted;
    }

    void Reset()
    {
        Clear();
        space_down = false;
        space_suppressed = false;
        release_space = false;
    }

    void Clear()
    {
        shortcut_text = false;
    }

    bool Commit()
    {
        const bool accepted = !shortcut_text;
        Clear();
        return accepted;
    }
};
