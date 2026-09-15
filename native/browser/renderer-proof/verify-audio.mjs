import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';

const [playwrightPath, url] = process.argv.slice(2);
if (!playwrightPath || !url) throw new Error('Usage: verify-audio.mjs PLAYWRIGHT_MODULE PROOF_URL');
const engines = await import(pathToFileURL(playwrightPath));
for (const name of ['chromium', 'firefox', 'webkit']) {
    const browser = await engines[name].launch();
    try {
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', (error) => errors.push(error.message));
        await page.goto(url);
        await page.waitForFunction(() => document.querySelector('#result').textContent.startsWith('PASS'));
        const invoke = (method, argument) => page.evaluate(async ({ method, argument }) => {
            const exports = await globalThis.getDotnetRuntime(0).getAssemblyExports('RendererProof');
            return exports.MonoGame.Browser.Conformance.Proof[method](argument);
        }, { method, argument });
        await invoke('SuspendAudio', true);
        await page.getByRole('button', { name: 'Test native audio (quiet two-second tone)' }).click();
        await page.waitForTimeout(200);
        assert.equal(await invoke('AudioPositionSeconds'), 0);
        await invoke('SuspendAudio', false);
        await page.waitForTimeout(350);
        await invoke('SuspendAudio', true);
        const paused = await invoke('AudioPositionSeconds');
        assert.ok(paused > 0.1);
        await page.waitForTimeout(450);
        const after = await invoke('AudioPositionSeconds');
        assert.ok(Math.abs(after - paused) < 0.025, `${paused} -> ${after} advanced while suspended`);
        await invoke('SuspendAudio', false);
        await page.waitForTimeout(350);
        const resumed = await invoke('AudioPositionSeconds');
        assert.ok(resumed > after + 0.1, `${after} -> ${resumed} failed to resume`);
        assert.deepEqual(errors, []);
        console.log(`${name}: native FAudio pre-initialization suspension, frozen voices and resume PASS (${paused}, ${after}, ${resumed})`);
    } finally {
        await browser.close();
    }
}
