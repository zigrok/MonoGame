using System;
using System.Diagnostics;
using System.IO;

namespace MonoGame.Effect
{
    internal sealed class BrowserShaderProfile : VulkanShaderProfile
    {
        public BrowserShaderProfile() : base("BrowserGL", 81) { }
        protected override bool UseGlLayout => true;

        protected override byte[] PrepareBytecode(byte[] bytecode, string outputPath)
        {
            var translator = Environment.GetEnvironmentVariable("MONOGAME_BROWSER_SHADER_TRANSLATOR");
            if (string.IsNullOrEmpty(translator) || !File.Exists(translator))
                throw new InvalidOperationException("Build native/browser shader tooling and set MONOGAME_BROWSER_SHADER_TRANSLATOR.");
            var input = Path.Combine(outputPath, Guid.NewGuid() + ".spv");
            var output = input + ".mggl";
            try
            {
                File.WriteAllBytes(input, bytecode);
                var start = new ProcessStartInfo(translator) { UseShellExecute = false };
                start.ArgumentList.Add(input);
                start.ArgumentList.Add(output);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start BrowserGL shader translator.");
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("BrowserGL shader translation failed; see SPIRV-Cross diagnostics.");
                return File.ReadAllBytes(output);
            }
            finally
            {
                File.Delete(input);
                File.Delete(output);
            }
        }
    }
}
