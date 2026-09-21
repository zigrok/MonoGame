// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;

namespace Microsoft.Xna.Framework;

internal static class TextInputGeometry
{
    internal static Rectangle ToWindowPoints(Rectangle pixels, float scale)
    {
        var left = (int)Math.Floor(pixels.Left / scale);
        var top = (int)Math.Floor(pixels.Top / scale);
        var right = (int)Math.Ceiling(pixels.Right / scale);
        var bottom = (int)Math.Ceiling(pixels.Bottom / scale);
        return new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }
}
