// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using NUnit.Framework;

namespace MonoGame.Tests.Audio
{
    [Category("Audio")]
    [Explicit("Requires the rebuilt native PCM clock ABI and a working audio device; playback is muted.")]
    class NativePcmStreamingTest
    {
        private static ulong Read(DynamicSoundEffectInstance instance, string property)
        {
            var info = typeof(DynamicSoundEffectInstance).GetProperty(property);
            if (info == null)
                Assert.Ignore("This platform does not expose the native PCM streaming contract.");
            return (ulong)info.GetValue(instance);
        }

        private static void Until(Func<bool> predicate)
        {
            var timer = Stopwatch.StartNew();
            while (!predicate() && timer.ElapsedMilliseconds < 10000)
            {
                FrameworkDispatcher.Update();
                Thread.Sleep(5);
            }
            Assert.IsTrue(predicate(), "Native audio condition did not complete.");
        }

        [Test]
        public void SourceFrameClockAdvancesInsideBufferAndSurvivesPauseFlushAndStarvation()
        {
            FrameworkDispatcher.Update();
            using (var instance = new DynamicSoundEffectInstance(8000, AudioChannels.Mono))
            {
                instance.Volume = 0;
                var pcm = new byte[16000];
                instance.SubmitBuffer(pcm);
                instance.Play();
                Until(() => Read(instance, "ConsumedSampleFrames") > 0);
                Assert.Less(Read(instance, "ConsumedSampleFrames"), 8000UL);
                Assert.AreEqual(1, instance.PendingBufferCount);
                instance.Pause();
                Thread.Sleep(50);
                var paused = Read(instance, "ConsumedSampleFrames");
                Thread.Sleep(50);
                Assert.AreEqual(paused, Read(instance, "ConsumedSampleFrames"));
                instance.Resume();
                Until(() => instance.PendingBufferCount == 0);
                Assert.AreEqual(8000UL, Read(instance, "ConsumedSampleFrames"));
                Thread.Sleep(50);
                Assert.AreEqual(8000UL, Read(instance, "ConsumedSampleFrames"));
                instance.Stop();
                Until(() => instance.PendingBufferCount == 0);
                Assert.AreEqual(8000UL, Read(instance, "ConsumedSampleFrames"));
                instance.SubmitBuffer(pcm, 0, 2000);
                instance.Play();
                Until(() => instance.PendingBufferCount == 0);
                Assert.AreEqual(9000UL, Read(instance, "ConsumedSampleFrames"));
            }
        }

        [Test]
        public void RepeatedFlushesReuseExactlyThreeNativeCopiesAndPreservePcmCapacity()
        {
            FrameworkDispatcher.Update();
            var baseline = Read(null, "LivePcmBufferBytes");
            using (var instance = new DynamicSoundEffectInstance(44100, AudioChannels.Stereo))
            {
                instance.Volume = 0;
                var pcm = new byte[256 * 1024];
                var capacity = 0;
                for (var cycle = 0; cycle < 100; cycle++)
                {
                    var submittedBytes = cycle % 2 == 0 ? 4096 : pcm.Length;
                    capacity = Math.Max(capacity, submittedBytes);
                    for (var buffer = 0; buffer < 3; buffer++)
                        instance.SubmitBuffer(pcm, 0, submittedBytes);
                    Assert.AreEqual(3, instance.PendingBufferCount);
                    Assert.AreEqual(3UL * (ulong)capacity, Read(instance, "AllocatedPcmBufferBytes"));
                    Assert.AreEqual(baseline + 3UL * (ulong)capacity,
                        Read(null, "LivePcmBufferBytes"));
                    Assert.AreEqual(3UL * (ulong)submittedBytes, Read(instance, "QueuedPcmBufferBytes"));
                    instance.Stop();
                    Until(() => instance.PendingBufferCount == 0);
                    Assert.AreEqual(0UL, Read(instance, "QueuedPcmBufferBytes"));
                    Assert.AreEqual(3UL * (ulong)capacity, Read(instance, "AllocatedPcmBufferBytes"));
                    Assert.AreEqual(baseline + 3UL * (ulong)capacity,
                        Read(null, "LivePcmBufferBytes"));
                    Assert.AreEqual(0UL, Read(instance, "ConsumedSampleFrames"));
                }
                instance.Dispose();
                Assert.AreEqual(baseline, Read(null, "LivePcmBufferBytes"));
                foreach (var property in new[] { "AllocatedPcmBufferBytes", "QueuedPcmBufferBytes" })
                {
                    var error = Assert.Throws<TargetInvocationException>(() => Read(instance, property));
                    Assert.IsInstanceOf<ObjectDisposedException>(error.InnerException);
                }
            }
        }
    }
}
