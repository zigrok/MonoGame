// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

namespace Microsoft.Xna.Framework;

internal static class TextCompositionIndex
{
    // SDL selection offsets count Unicode scalars; managed editors index UTF-16 code units.
    internal static int ToUtf16(string text, int scalarIndex)
    {
        var offset = 0;
        for (var scalar = 0; scalar < scalarIndex && offset < text.Length; scalar++)
        {
            offset += char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length &&
                char.IsLowSurrogate(text[offset + 1]) ? 2 : 1;
        }
        return offset;
    }
}
