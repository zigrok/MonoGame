import { dotnet } from "./_framework/dotnet.js";
const runtime = await dotnet.withModuleConfig({ canvas: document.querySelector("canvas") }).create();
const exports = await runtime.getAssemblyExports("RendererProof");
await runtime.runMain();
const proof = exports.MonoGame.Browser.Conformance.Proof;
document.querySelector("#audio").addEventListener("click", () => proof.Audio());
function frame() {
    try {
        if (proof.Tick()) requestAnimationFrame(frame);
        if (proof.GraphicsPassed()) document.querySelector("#result").textContent = "PASS: SpriteBatch, BasicEffect, Alpha8, scissor, render-target orientation and readback";
    } catch (error) {
        document.querySelector("#result").textContent = `FAIL: ${error}`;
        throw error;
    }
}
requestAnimationFrame(frame);
