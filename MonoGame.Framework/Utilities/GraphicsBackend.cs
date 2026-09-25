// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

namespace MonoGame.Framework.Utilities
{
    /// <summary>
    /// Type of the underlying graphics backend.
    /// </summary>
    public enum GraphicsBackend
    {
        /// <summary>
        /// Represents the Microsoft DirectX 11 graphics backend.
        /// </summary>
        DirectX,

        /// <summary>
        /// Represents the OpenGL graphics backend.
        /// </summary>
        OpenGL,

        /// <summary>
        /// Represents the Vulkan graphics backend.
        /// </summary>
        Vulkan,

        /// <summary>
        /// Represents the Apple Metal graphics backend.
        /// </summary>
        Metal,

        /// <summary>
        /// Represents the Microsoft DirectX 12 graphics backend.
        /// </summary>
        DirectX12,

        /// <summary>
        /// Represents the native browser WebGL2 backend using BrowserGL effect bytecode.
        /// </summary>
        WebGL = 5,

        /// <summary>
        /// Represents the headless backend, which accepts the full graphics API without a GPU, a
        /// driver, or a display server. Draw calls are discarded and nothing is rasterized, so it is
        /// intended for automated tests and other runs where the draw path should execute but its
        /// output is not inspected.
        /// </summary>
        Headless = 6
    }
}
