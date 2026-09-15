using System;
using System.Runtime.InteropServices;

Console.WriteLine($"SDL_VERSION={Native.Version()}");
int result = Native.Init();
Console.WriteLine($"SDL_WEBGL2_INIT={result}");
if (result != 0)
    throw new InvalidOperationException(Marshal.PtrToStringUTF8(Native.Error()));
Console.WriteLine("SDL_STATIC_NATIVE_PROOF=PASS");

internal static partial class Native
{
    [DllImport("libbrowserproof", EntryPoint = "BrowserProof_Version")]
    internal static extern int Version();
    [DllImport("libbrowserproof", EntryPoint = "BrowserProof_Init")]
    internal static extern int Init();
    [DllImport("libbrowserproof", EntryPoint = "BrowserProof_Error")]
    internal static extern IntPtr Error();
}
