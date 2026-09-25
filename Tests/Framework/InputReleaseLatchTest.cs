// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NUnit.Framework;

namespace MonoGame.Tests.Framework;

class InputReleaseLatchTest
{
    [Test]
    public void ReleasesOfSameDrainPressesAreDeferred()
    {
        var latch = new InputReleaseLatch();
        latch.BeginDrain();
        Assert.IsFalse(latch.DefersButtonUp(0));
        Assert.IsFalse(latch.DefersKeyUp(Keys.Enter));
        latch.ButtonDown(0);
        latch.KeyDown(Keys.Enter);
        Assert.IsTrue(latch.DefersButtonUp(0));
        Assert.IsTrue(latch.DefersKeyUp(Keys.Enter));
        Assert.IsFalse(latch.DefersButtonUp(2));
        Assert.IsFalse(latch.DefersKeyUp(Keys.Space));
    }

    [Test]
    public void PressesFromEarlierDrainsReleaseImmediately()
    {
        var latch = new InputReleaseLatch();
        latch.BeginDrain();
        latch.ButtonDown(4);
        latch.KeyDown(Keys.A);
        latch.BeginDrain();
        Assert.IsFalse(latch.DefersButtonUp(4));
        Assert.IsFalse(latch.DefersKeyUp(Keys.A));
    }
}
