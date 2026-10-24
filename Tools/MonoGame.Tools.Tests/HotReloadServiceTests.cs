using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Graphics;
using NUnit.Framework;

namespace MonoGame.Tests.ContentPipeline;

[TestFixture]
internal sealed class HotReloadServiceTests
{
    [Test]
    public void SuccessfulCompileSwapsAndRetiresPreviousResource()
    {
        using var watchedFile = new TemporaryFile();
        var initial = new FakeResource(1);
        using var service = new HotReloadService<FakeResource>(
            initial,
            watchedFile.Path,
            _ => Task.FromResult(new HotReloadCompilation(new byte[] { 2 }, cacheHit: true)),
            bytes => new FakeResource(bytes[0]),
            TimeSpan.Zero,
            retirementFrames: 1);

        service.NotifyChanged();
        WaitForResult(service);

        Assert.That(service.Current.Id, Is.EqualTo(2));
        Assert.That(service.LastStatus.Succeeded, Is.True);
        Assert.That(service.LastStatus.CacheHit, Is.True);
        Assert.That(initial.Disposed, Is.False);

        service.Update();
        Assert.That(initial.Disposed, Is.True);
    }

    [Test]
    public void FailedCompileKeepsLastValidResource()
    {
        using var watchedFile = new TemporaryFile();
        var initial = new FakeResource(1);
        using var service = new HotReloadService<FakeResource>(
            initial,
            watchedFile.Path,
            _ => throw new InvalidOperationException("compiler failed"),
            bytes => new FakeResource(bytes[0]),
            TimeSpan.Zero);

        service.NotifyChanged();
        WaitForResult(service);

        Assert.That(service.Current, Is.SameAs(initial));
        Assert.That(service.LastStatus.Succeeded, Is.False);
        Assert.That(service.LastStatus.Message, Is.EqualTo("compiler failed"));
        Assert.That(initial.Disposed, Is.False);
    }

    // Retried on purpose: macOS can replay the watched file's creation event when the FileSystemWatcher starts, which
    // starts one extra compile and breaks the exact invocation count on a loaded runner. The behaviour under test is
    // still asserted on every attempt (the superseded compile must not be applied), and a deterministic failure fails
    // all attempts.
    [Test]
    [Retry(3)]
    public void SupersededCompileIsCancelled()
    {
        using var watchedFile = new TemporaryFile();
        using var firstCompileStarted = new ManualResetEventSlim();
        var invocation = 0;
        using var service = new HotReloadService<FakeResource>(
            new FakeResource(1),
            watchedFile.Path,
            async cancellationToken =>
            {
                var id = Interlocked.Increment(ref invocation);
                if (id == 1)
                {
                    firstCompileStarted.Set();
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }
                return new HotReloadCompilation(new byte[] { (byte)(id + 1) }, cacheHit: false);
            },
            bytes => new FakeResource(bytes[0]),
            TimeSpan.Zero);

        service.NotifyChanged();
        Assert.That(firstCompileStarted.Wait(TimeSpan.FromSeconds(2)), Is.True);
        service.NotifyChanged();
        WaitForResult(service);

        Assert.That(service.Current.Id, Is.EqualTo(3));
        Assert.That(invocation, Is.EqualTo(2));
    }

    private static void WaitForResult(HotReloadService<FakeResource> service)
    {
        for (var attempt = 0; attempt < 100 && service.LastStatus.Message == null; attempt++)
        {
            Thread.Sleep(10);
            service.Update();
        }
        Assert.That(service.LastStatus.Message, Is.Not.Null);
    }

    private sealed class FakeResource : IDisposable
    {
        public int Id { get; }
        public bool Disposed { get; private set; }

        public FakeResource(int id) => Id = id;
        public void Dispose() => Disposed = true;
    }

    private sealed class TemporaryFile : IDisposable
    {
        // A directory of its own, not the shared temp directory: the service watches this path, and
        // anything else writing in the same directory can raise an event that starts a third
        // compile and makes "exactly two invocations" fail on a busy runner.
        private readonly string _directory = Directory.CreateTempSubdirectory("monogame-hot-reload-").FullName;

        public string Path { get; }

        public TemporaryFile()
        {
            Path = System.IO.Path.Combine(_directory, "watched.fx");
            File.WriteAllText(Path, string.Empty);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}