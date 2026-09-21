// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Threading;
using Microsoft.Xna.Framework;
using NUnit.Framework;

namespace MonoGame.Tests.Framework;

class LiveResizeFrameDispatcherTest
{
    [Test]
    public void FramesRunOnlyDuringTheOwningThreadsPoll()
    {
        var dispatcher = new LiveResizeFrameDispatcher();
        int frames = 0;
        dispatcher.TryRun(() => frames++);
        Assert.AreEqual(0, frames);
        dispatcher.IsPolling = true;
        var otherThread = new Thread(() => dispatcher.TryRun(() => frames++));
        otherThread.Start();
        otherThread.Join();
        Assert.AreEqual(0, frames);
        dispatcher.TryRun(() => frames++);
        dispatcher.TryRun(() => frames++);
        Assert.AreEqual(2, frames);
        dispatcher.IsPolling = false;
        dispatcher.TryRun(() => frames++);
        Assert.AreEqual(2, frames);
    }

    [Test]
    public void NestedFramesCannotReenterUpdateOrDraw()
    {
        var dispatcher = new LiveResizeFrameDispatcher { IsPolling = true };
        int frames = 0;
        dispatcher.TryRun(() =>
        {
            frames++;
            dispatcher.TryRun(() => frames++);
        });
        Assert.AreEqual(1, frames);
        dispatcher.TryRun(() => frames++);
        Assert.AreEqual(2, frames);
    }

    [Test]
    public void DisposalDuringCallbackStopsFurtherFramesWithoutUnwindingNativeCode()
    {
        var dispatcher = new LiveResizeFrameDispatcher { IsPolling = true };
        dispatcher.TryRun(dispatcher.Stop);
        Assert.IsTrue(dispatcher.IsStopped);
        dispatcher.TryRun(() => Assert.Fail("A disposed game must not receive a frame."));
        Assert.DoesNotThrow(dispatcher.ThrowIfFaulted);
    }

    [Test]
    public void CallbackFailureIsRethrownOnlyAfterReturningToManagedLoop()
    {
        var dispatcher = new LiveResizeFrameDispatcher { IsPolling = true };
        var expected = new InvalidOperationException("draw failed");
        Assert.DoesNotThrow(() => dispatcher.TryRun(() => throw expected));
        Assert.IsTrue(dispatcher.IsStopped);
        dispatcher.TryRun(() => Assert.Fail("A failed frame must not be retried."));
        dispatcher.IsPolling = false;
        Assert.AreSame(expected, Assert.Throws<InvalidOperationException>(dispatcher.ThrowIfFaulted));
    }
}
