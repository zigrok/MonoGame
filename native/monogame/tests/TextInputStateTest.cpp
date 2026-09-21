// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

#include "../sdl/MGP_TextInputState.h"
#include <cassert>
#include <string>

int main()
{
    MGP_TextInputState state;

    // Control+Option+Space, Control+Space and Command+Space must not reach games or text.
    for (int repeat = 0; repeat < 3; ++repeat)
    {
        assert(!state.KeyDown(true, true, false, true));
        assert(!state.Commit());
        assert(!state.KeyUp(true));
    }

    // Other command/control keys still reach the application's shortcut/navigation handler.
    assert(state.KeyDown(true, true, false, false));
    assert(!state.Commit());

    assert(state.KeyDown(true, false, false, true));
    assert(state.Commit());
    assert(!state.KeyDown(true, true, false, true));
    assert(state.release_space);
    assert(!state.Commit());
    assert(!state.KeyUp(true));

    for (const auto& text : {std::string(" "), std::string("?"), std::string("/"),
        std::string("å"), std::string("e\xcc\x81"), std::string("日😀")})
    {
        // Plain and Shift/Option-generated text does not have Command or Control set.
        assert(state.KeyDown(true, false, false, text == " "));
        assert(state.Commit());
    }

    // Windows AltGr (Control+Alt) and other non-Cocoa input retain their original semantics.
    assert(state.KeyDown(false, true, false, true));
    assert(state.Commit());
    assert(state.KeyUp(true));
    assert(state.KeyDown(false, true, false, false));
    assert(state.Commit());

    // Marked text clears Cocoa's pending key; empty preedit precedes its atomic commit.
    assert(state.KeyDown(true, true, false, false));
    state.Clear();
    assert(state.Commit());
    assert(state.KeyDown(true, true, true, false));
    assert(state.Commit());

    assert(!state.KeyDown(true, true, false, true));
    assert(state.KeyUp(false));
    assert(state.Commit());
    assert(!state.KeyDown(true, true, false, true));
    state.Reset();
    assert(state.Commit());
    assert(!state.space_down && !state.space_suppressed);

    // Session changes and draining the queue end direct-key provenance.
    assert(!state.KeyDown(true, true, false, true));
    state.Clear();
    assert(state.Commit());

    assert(!state.KeyDown(true, true, false, true));
    MGP_TextInputState otherWindow;
    assert(otherWindow.Commit());
    assert(!state.Commit());
    assert(state.Commit());
}
