// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NUnit.Framework;

namespace MonoGame.Tests.Graphics
{
    [NonParallelizable]
    [RunOnUiTestFixture]
    internal class EffectTest : GraphicsDeviceTestFixtureBase
    {
        [Test]
        public void EffectConstructorShouldAllowIndexAndCount()
        {
            byte[] mgfxo = EffectResource.BasicEffect.Bytecode;
            var index = 100000;
            var byteArray = new byte[index + mgfxo.Length];
            mgfxo.CopyTo(byteArray, index);
            Effect effect = null;
            Assert.DoesNotThrow(() => { effect = new Effect(game.GraphicsDevice, byteArray, index, mgfxo.Length); });
            effect.Dispose();
        }

        [Test]
        public void EffectPassShouldSetTexture()
        {
            var texture = new Texture2D(game.GraphicsDevice, 1, 1, false, SurfaceFormat.Color);
            game.GraphicsDevice.Textures[0] = null;

            var effect = new BasicEffect(game.GraphicsDevice);
            effect.TextureEnabled = true;
            effect.Texture = texture;

            Assert.That(game.GraphicsDevice.Textures[0], Is.Null);

            var effectPass = effect.CurrentTechnique.Passes[0];
            effectPass.Apply();

            Assert.That(game.GraphicsDevice.Textures[0], Is.SameAs(texture));

            texture.Dispose();
            effect.Dispose();
        }

        [Test]
        public void EffectPassShouldSetTextureOnSubsequentCalls()
        {
            var texture = new Texture2D(game.GraphicsDevice, 1, 1, false, SurfaceFormat.Color);
            game.GraphicsDevice.Textures[0] = null;

            var effect = new BasicEffect(game.GraphicsDevice);
            effect.TextureEnabled = true;
            effect.Texture = texture;

            Assert.That(game.GraphicsDevice.Textures[0], Is.Null);

            var effectPass = effect.CurrentTechnique.Passes[0];
            effectPass.Apply();

            Assert.That(game.GraphicsDevice.Textures[0], Is.SameAs(texture));

            game.GraphicsDevice.Textures[0] = null;

            effectPass = effect.CurrentTechnique.Passes[0];
            effectPass.Apply();

            Assert.That(game.GraphicsDevice.Textures[0], Is.SameAs(texture));

            texture.Dispose();
            effect.Dispose();
        }

        [Test]
        public void EffectPassShouldSetTextureEvenIfNull()
        {
            var texture = new Texture2D(game.GraphicsDevice, 1, 1, false, SurfaceFormat.Color);
            game.GraphicsDevice.Textures[0] = texture;

            var effect = new BasicEffect(game.GraphicsDevice);
            effect.TextureEnabled = true;
            effect.Texture = null;

            Assert.That(game.GraphicsDevice.Textures[0], Is.SameAs(texture));

            var effectPass = effect.CurrentTechnique.Passes[0];
            effectPass.Apply();

            Assert.That(game.GraphicsDevice.Textures[0], Is.Null);

            texture.Dispose();
            effect.Dispose();
        }

        [Test]
        public void EffectPassShouldOverrideTextureIfNotExplicitlySet()
        {
            var texture = new Texture2D(game.GraphicsDevice, 1, 1, false, SurfaceFormat.Color);
            game.GraphicsDevice.Textures[0] = texture;

            var effect = new BasicEffect(game.GraphicsDevice);
            effect.TextureEnabled = true;

            Assert.That(game.GraphicsDevice.Textures[0], Is.SameAs(texture));

            var effectPass = effect.CurrentTechnique.Passes[0];
            effectPass.Apply();

            Assert.That(game.GraphicsDevice.Textures[0], Is.Null);

            texture.Dispose();
            effect.Dispose();
        }

        [Test]
        public void DisposedEffectConstantBuffersMustNotRemainBoundForDefaultSpriteBatch()
        {
            var device = game.GraphicsDevice;
            using var texture = new Texture2D(device, 1, 1);
            using var target = new RenderTarget2D(device, 8, 8, false, SurfaceFormat.Color,
                DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            texture.SetData(new[] { Color.White });
            var pixels = new Color[64];
            try
            {
                using (var effect = new AlphaTestEffect(device)
                {
                    Projection = Matrix.CreateOrthographicOffCenter(0, 8, 8, 0, 0, 1)
                })
                using (var batch = new SpriteBatch(device))
                {
                    device.SetRenderTarget(target);
                    device.Clear(Color.Transparent);
                    batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp,
                        DepthStencilState.None, RasterizerState.CullNone, effect);
                    batch.Draw(texture, new Rectangle(0, 0, 8, 8), Color.White);
                    batch.End();
                    device.SetRenderTarget(null);
                    target.GetData(pixels);
                    Assert.That(pixels, Is.All.EqualTo(Color.White));
                }

                using var nextBatch = new SpriteBatch(device);
                device.SetRenderTarget(target);
                device.Clear(Color.Transparent);
                nextBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp,
                    DepthStencilState.None, RasterizerState.CullNone);
                nextBatch.Draw(texture, new Rectangle(0, 0, 8, 8), Color.CornflowerBlue);
                nextBatch.End();
                device.SetRenderTarget(null);
                target.GetData(pixels);
                Assert.That(pixels, Is.All.EqualTo(Color.CornflowerBlue));
            }
            finally
            {
                device.SetRenderTarget(null);
                device.Textures[0] = null;
            }
        }

        [Test]
#if DESKTOPGL
        [Ignore("Fails under OpenGL!")]
#endif
        public void EffectParameterShouldBeSetIfSetByNameAndGetByIndex()
        {
            // This relies on the parameters permanently being on the same index.
            // Should be no problem except when adding parameters.
            var texture = new Texture2D(game.GraphicsDevice, 1, 1, false, SurfaceFormat.Color);
            game.GraphicsDevice.Textures[0] = texture;

            var effect = new BasicEffect(game.GraphicsDevice);
            effect.TextureEnabled = true;
            effect.Texture = null;
            effect.Parameters["DiffuseColor"].SetValue(Color.HotPink.ToVector3());
            effect.Parameters["FogColor"].SetValue(Color.Honeydew.ToVector3());

            Assert.That(effect.Parameters[0].GetValueVector3().Equals(Color.HotPink.ToVector3()));
            Assert.That(effect.Parameters[14].GetValueVector3().Equals(Color.Honeydew.ToVector3()));

            texture.Dispose();
            effect.Dispose();
        }
    }
}
