// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Runtime.ExceptionServices;

namespace Microsoft.Xna.Framework;

internal sealed class LiveResizeFrameDispatcher
{
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _executing;
    private bool _stopped;
    private ExceptionDispatchInfo _error;

    internal bool IsPolling { get; set; }
    internal bool IsStopped => _stopped || _error != null;

    internal void Stop() => _stopped = true;

    internal void TryRun(Action frame)
    {
        if (Environment.CurrentManagedThreadId != _threadId || !IsPolling || _executing || IsStopped)
            return;

        _executing = true;
        try
        {
            frame();
        }
        catch (Exception error)
        {
            // Never unwind a managed exception through SDL's reverse-P/Invoke callback.
            _error = ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            _executing = false;
        }
    }

    internal void ThrowIfFaulted() => _error?.Throw();
}
