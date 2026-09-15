#if METAL
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NUnit.Framework;

namespace MonoGame.Tests.Framework.Platform;

internal unsafe partial class NativeSdl3SmokeTest
{
    [TestCase(0)]
    [TestCase(4)]
    public void MetalBackbuffer_ResumedPassesPreservePixels(int samples)
    {
        using var game = new BackbufferPreservationGame(samples);
        game.RunOneFrame();
        game.RunOneFrame();
        Assert.That(game.VerifiedFrames, Is.EqualTo(2));
    }

    private sealed class BackbufferPreservationGame : Game
    {
        private SpriteBatch _batch;
        private Texture2D _pixel;
        private RenderTarget2D _target;
        public int VerifiedFrames { get; private set; }

        public BackbufferPreservationGame(int samples)
        {
            var manager = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = 160,
                PreferredBackBufferHeight = 96,
                PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8,
                PreferMultiSampling = samples > 0
            };
            manager.PreparingDeviceSettings += (_, args) =>
            {
                args.GraphicsDeviceInformation.PresentationParameters.MultiSampleCount = samples;
                args.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage = RenderTargetUsage.PreserveContents;
            };
        }

        protected override void LoadContent()
        {
            _batch = new SpriteBatch(GraphicsDevice);
            _pixel = new Texture2D(GraphicsDevice, 1, 1);
            _pixel.SetData(new[] { Color.White });
            _target = new RenderTarget2D(GraphicsDevice, 8, 8);
        }

        protected override void Draw(GameTime gameTime)
        {
            // No initial Clear: newly acquired drawables must never load stale swapchain pixels.
            DrawBlock(0, Color.Red);
            GraphicsDevice.Clear(ClearOptions.DepthBuffer | ClearOptions.Stencil, Color.Magenta, 1, 0);
            DrawBlock(32, Color.Green);
            AssertPixels(Color.Red, Color.Green, Color.Black, Color.Black);

            var pixel = new Color[1];
            _pixel.GetData(pixel);
            Assert.That(pixel[0], Is.EqualTo(Color.White));
            DrawBlock(64, Color.Blue);
            AssertPixels(Color.Red, Color.Green, Color.Blue, Color.Black);

            GraphicsDevice.SetRenderTarget(_target);
            GraphicsDevice.Clear(Color.Cyan);
            GraphicsDevice.SetRenderTarget(null);
            DrawBlock(96, Color.White);
            AssertPixels(Color.Red, Color.Green, Color.Blue, Color.White);

            GraphicsDevice.Clear(ClearOptions.Target, Color.Yellow, 1, 0);
            AssertPixels(Color.Yellow, Color.Yellow, Color.Yellow, Color.Yellow);

            GraphicsDevice.Clear(Color.Black);
            DrawBlock(0, Color.Red, DepthStencilState.Default, 0.25f);
            AssertPixels(Color.Red, Color.Black, Color.Black, Color.Black);
            DrawBlock(0, Color.Green, DepthStencilState.Default, 0.75f);
            AssertPixels(Color.Red, Color.Black, Color.Black, Color.Black);
            GraphicsDevice.Clear(ClearOptions.Target, Color.Yellow, 1, 0);
            DrawBlock(0, Color.Green, DepthStencilState.Default, 0.75f);
            AssertPixels(Color.Yellow, Color.Yellow, Color.Yellow, Color.Yellow);
            GraphicsDevice.Clear(ClearOptions.DepthBuffer, Color.Magenta, 1, 0);
            DrawBlock(0, Color.Blue, DepthStencilState.Default, 0.75f);
            AssertPixels(Color.Blue, Color.Yellow, Color.Yellow, Color.Yellow);

            using var writeStencil = new DepthStencilState
            {
                DepthBufferEnable = false,
                StencilEnable = true,
                StencilFunction = CompareFunction.Always,
                StencilPass = StencilOperation.Replace,
                ReferenceStencil = 1
            };
            using var testStencil = new DepthStencilState
            {
                DepthBufferEnable = false,
                StencilEnable = true,
                StencilFunction = CompareFunction.Equal,
                ReferenceStencil = 0
            };
            DrawBlock(0, Color.Red, writeStencil);
            GraphicsDevice.Clear(ClearOptions.Target, Color.Yellow, 1, 0);
            DrawBlock(0, Color.Green, testStencil);
            DrawBlock(32, Color.Green, testStencil);
            AssertPixels(Color.Yellow, Color.Green, Color.Yellow, Color.Yellow);
            GraphicsDevice.Clear(ClearOptions.Stencil, Color.Magenta, 1, 0);
            DrawBlock(0, Color.Blue, testStencil);
            AssertPixels(Color.Blue, Color.Green, Color.Yellow, Color.Yellow);
            VerifiedFrames++;
        }

        private void DrawBlock(int x, Color color, DepthStencilState depthStencil = null, float depth = 0)
        {
            _batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp,
                depthStencil ?? DepthStencilState.None, RasterizerState.CullNone);
            _batch.Draw(_pixel, new Rectangle(x, 0, 32, 32), null, color, 0, Vector2.Zero,
                SpriteEffects.None, depth);
            _batch.End();
        }

        private void AssertPixels(params Color[] expected)
        {
            var pixels = new Color[128 * 32];
            GraphicsDevice.GetBackBufferData(new Rectangle(0, 0, 128, 32), pixels, 0, pixels.Length);
            for (var block = 0; block < expected.Length; block++)
                Assert.That(pixels[16 * 128 + block * 32 + 16], Is.EqualTo(expected[block]),
                    $"Frame {VerifiedFrames}, block {block}");
        }

        protected override void UnloadContent()
        {
            _target.Dispose();
            _pixel.Dispose();
            _batch.Dispose();
        }
    }
}
#endif
