import { dotnet } from "./_framework/dotnet.js";
const { runMain } = await dotnet
    .withModuleConfig({ canvas: document.querySelector("canvas") })
    .create();
await runMain();
