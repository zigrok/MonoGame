// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using Microsoft.Xna.Framework;
using NUnit.Framework;

namespace MonoGame.Tests
{
    [TestFixture]
    [Category("GameTest")]
    public class GameStepFrameTest
    {
        private sealed class CountingGame : Game
        {
            public int Updates, Draws, Inits;
            public TimeSpan LastElapsed, LastTotal;

            public CountingGame()
            {
                new GraphicsDeviceManager(this);
            }

            protected override void Initialize() { Inits++; base.Initialize(); }
            protected override void Update(GameTime gameTime)
            {
                Updates++;
                LastElapsed = gameTime.ElapsedGameTime;
                LastTotal = gameTime.TotalGameTime;
                base.Update(gameTime);
            }
            protected override void Draw(GameTime gameTime) { Draws++; base.Draw(gameTime); }
        }

        [Test]
        public void OneCallRunsOneUpdateAndOneDraw()
        {
            using var game = new CountingGame();
            game.StepFrame();
            Assert.That(game.Inits, Is.EqualTo(1));
            Assert.That(game.Updates, Is.EqualTo(1));
            Assert.That(game.Draws, Is.EqualTo(1));
            Assert.That(game.LastElapsed, Is.EqualTo(game.TargetElapsedTime));
        }

        [Test]
        public void TotalGameTimeAdvancesByExactlyElapsed()
        {
            using var game = new CountingGame();
            var step = TimeSpan.FromTicks(123457);
            game.StepFrame(step);
            game.StepFrame(step);
            game.StepFrame(step);
            Assert.That(game.LastElapsed, Is.EqualTo(step));
            Assert.That(game.LastTotal, Is.EqualTo(TimeSpan.FromTicks(step.Ticks * 3)));
        }

        [Test]
        public void RepeatedCallsNeverRunExtraUpdates()
        {
            using var game = new CountingGame();
            for (var i = 1; i <= 10; i++)
            {
                game.StepFrame();
                Assert.That(game.Updates, Is.EqualTo(i));
                Assert.That(game.Draws, Is.EqualTo(i));
            }
            Assert.That(game.Inits, Is.EqualTo(1));
        }

        [Test]
        public void LargeElapsedDoesNotCatchUp()
        {
            using var game = new CountingGame();
            game.StepFrame(TimeSpan.FromSeconds(60));
            Assert.That(game.Updates, Is.EqualTo(1));
            Assert.That(game.Draws, Is.EqualTo(1));
            Assert.That(game.LastElapsed, Is.EqualTo(TimeSpan.FromSeconds(60)));
        }
    }
}
