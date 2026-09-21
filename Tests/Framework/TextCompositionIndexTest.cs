using Microsoft.Xna.Framework;
using NUnit.Framework;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;

namespace MonoGame.Tests.Framework;

class TextCompositionIndexTest
{
    [TestCase("a😀日", 0, 0)]
    [TestCase("a😀日", 1, 1)]
    [TestCase("a😀日", 2, 3)]
    [TestCase("a😀日", 3, 4)]
    [TestCase("a😀日", int.MaxValue, 4)]
    [TestCase("e\u0301", 1, 1)]
    [TestCase("e\u0301", 2, 2)]
    [TestCase("", 1, 0)]
    [TestCase("text", -1, 0)]
    public void SdlScalarOffsetsMapToUtf16WithoutSplittingSurrogates(string text, int scalar, int expected)
    {
        Assert.AreEqual(expected, TextCompositionIndex.ToUtf16(text, scalar));
    }

    [TestCase(1f, 11, 21, 1, 19)]
    [TestCase(2f, 5, 10, 1, 10)]
    [TestCase(1.5f, 7, 14, 1, 13)]
    public void CandidateRectangleConvertsDrawablePixelsToCoveringWindowPoints(
        float scale, int x, int y, int width, int height)
    {
        Assert.AreEqual(new Rectangle(x, y, width, height),
            TextInputGeometry.ToWindowPoints(new Rectangle(11, 21, 1, 19), scale));
    }

#if !MONOMAC && (WINDOWS || DESKTOPGL || ANGLE || NATIVE || METAL || VULKAN)
    [TestCase(Keys.LeftControl)]
    [TestCase(Keys.RightControl)]
    [TestCase(Keys.LeftWindows)]
    [TestCase(Keys.RightWindows)]
    public void MacCommandSpaceDoesNotReachKeyboardPolling(Keys command)
    {
        var pressed = new List<Keys> { command, Keys.LeftAlt };
        Assert.IsFalse(TextInputKeyState.TrackKeyDown(pressed, Keys.Space, composing: false, macos: true));
        Assert.IsFalse(TextInputKeyState.TrackKeyDown(pressed, Keys.Space, composing: false, macos: true));
        CollectionAssert.AreEqual(new[] { command, Keys.LeftAlt }, pressed);
        pressed.Remove(command);
        Assert.IsTrue(TextInputKeyState.TrackKeyDown(pressed, Keys.Space, composing: false, macos: true));
        Assert.Contains(Keys.Space, pressed);
    }

    [Test]
    public void MacShortcutRepeatReleasesPreviouslyPolledSpace()
    {
        var pressed = new List<Keys> { Keys.Space, Keys.RightControl };
        Assert.IsFalse(TextInputKeyState.TrackKeyDown(pressed, Keys.Space, composing: false, macos: true));
        CollectionAssert.AreEqual(new[] { Keys.RightControl }, pressed);
    }

    [TestCase(Keys.None, true)]
    [TestCase(Keys.LeftShift, true)]
    [TestCase(Keys.LeftAlt, true)]
    [TestCase(Keys.RightAlt, true)]
    [TestCase(Keys.LeftControl, false)]
    public void OrdinaryOptionAndNonMacAltGrSpacesRemainAvailable(Keys modifier, bool macos)
    {
        var pressed = new List<Keys> { modifier, Keys.RightAlt };
        Assert.IsTrue(TextInputKeyState.TrackKeyDown(pressed, Keys.Space, composing: false, macos: macos));
        Assert.Contains(Keys.Space, pressed);
        Assert.IsFalse(TextInputKeyState.IsMacOSCommand(pressed, macos));
    }

    [TestCase(" ")]
    [TestCase("?")]
    [TestCase("/")]
    [TestCase("å")]
    [TestCase("e\u0301")]
    [TestCase("日😀")]
    public void CommittedTextIsNotReclassifiedByHeldShortcutModifiers(string text)
    {
        var window = new MockWindow();
        var pressed = new List<Keys> { Keys.LeftControl, Keys.LeftAlt };
        string committed = null;
        var legacy = new List<char>();
        window.TextCommitted += value => committed = value;
        window.TextInput += (_, args) => legacy.Add(args.Character);
        window.OnTextEditing("draft", 0, 0);
        window.OnTextEditing(string.Empty, 0, 0);
        Assert.IsTrue(TextInputKeyState.IsMacOSCommand(pressed, macos: true));
        window.OnTextCommitted(text);
        Assert.AreEqual(text, committed);
        CollectionAssert.AreEqual(text.ToCharArray(), legacy);
    }

    [TestCase(Keys.LeftShift)]
    [TestCase(Keys.RightShift)]
    [TestCase(Keys.LeftControl)]
    [TestCase(Keys.RightControl)]
    [TestCase(Keys.LeftAlt)]
    [TestCase(Keys.RightAlt)]
    [TestCase(Keys.LeftWindows)]
    [TestCase(Keys.RightWindows)]
    public void ModifierPressedDuringCompositionRemainsHeldAfterAtomicCommit(Keys modifier)
    {
        var window = new MockWindow();
        var pressed = new List<Keys>();
        window.OnTextEditing("draft", 0, 0);
        Assert.IsFalse(TextInputKeyState.TrackKeyDown(pressed, modifier, window.HasTextComposition));
        Assert.IsFalse(TextInputKeyState.TrackKeyDown(pressed, modifier, window.HasTextComposition));
        Assert.IsTrue(new KeyboardState(pressed.ToArray()).IsKeyDown(modifier));
        window.OnTextCommitted("日");
        Assert.IsTrue(TextInputKeyState.TrackKeyDown(pressed, Keys.A, window.HasTextComposition));
        CollectionAssert.AreEquivalent(new[] { modifier, Keys.A }, pressed);
        Assert.IsTrue(new KeyboardState(pressed.ToArray()).IsKeyDown(modifier));
        pressed.Remove(modifier);
        Assert.IsFalse(new KeyboardState(pressed.ToArray()).IsKeyDown(modifier));
    }

    [TestCase(Keys.Left)]
    [TestCase(Keys.Right)]
    [TestCase(Keys.Up)]
    [TestCase(Keys.Down)]
    [TestCase(Keys.Enter)]
    [TestCase(Keys.Tab)]
    [TestCase(Keys.Escape)]
    [TestCase(Keys.Back)]
    public void CandidateKeysDoNotLeakIntoPollingAfterComposition(Keys key)
    {
        var pressed = new List<Keys> { Keys.LeftShift };
        Assert.IsFalse(TextInputKeyState.TrackKeyDown(pressed, key, composing: true));
        CollectionAssert.AreEqual(new[] { Keys.LeftShift }, pressed);
    }

    [Test]
    public void NativeCommitIsAtomicBeforeCompatibleLegacyCharacters()
    {
        var window = new MockWindow();
        var order = new List<string>();
        window.TextCommitted += text => order.Add("commit:" + text);
        window.TextInput += (_, args) => order.Add("char:" + args.Character);
        window.OnTextEditing("draft", 0, 0);
        window.OnTextCommitted("日😀");
        CollectionAssert.AreEqual(new[] { "commit:日😀", "char:日", "char:\ud83d", "char:\ude00" }, order);
        Assert.IsFalse(window.HasTextComposition);
    }

    [Test]
    public void NativePreeditSelectionUsesUtf16AndEmptyTextCancels()
    {
        var window = new MockWindow();
        string text = null;
        var selection = Point.Zero;
        window.TextEditing += (value, start, length) => { text = value; selection = new Point(start, length); };
        window.OnTextEditing("😀日", 1, 1);
        Assert.AreEqual("😀日", text);
        Assert.AreEqual(new Point(2, 1), selection);
        Assert.IsTrue(window.HasTextComposition);
        window.OnTextEditing("😀日", -1, -1);
        Assert.AreEqual(new Point(3, 0), selection);
        window.OnTextEditing("", 0, 0);
        Assert.IsFalse(window.HasTextComposition);
        Assert.AreEqual(string.Empty, text);
    }

    [Test]
    public void UnimplementedWindowDoesNotClaimCompositionSupport()
    {
        var window = new MockWindow();
        Assert.IsFalse(window.SupportsTextComposition);
        Assert.IsFalse(window.SetTextInputActive(true));
        Assert.IsFalse(window.SetTextInputRectangle(new Rectangle(0, 0, 1, 20)));
    }
#endif
}
