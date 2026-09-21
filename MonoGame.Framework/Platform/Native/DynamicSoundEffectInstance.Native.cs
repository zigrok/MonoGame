// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using MonoGame.Interop;
using System;

namespace Microsoft.Xna.Framework.Audio;

public sealed partial class DynamicSoundEffectInstance : SoundEffectInstance
{
    /// <summary>Live raw streaming PCM capacities across all voices and systems in this native library image.</summary>
    /// <remarks>
    /// Updated after successful malloc and actual free calls; remains queryable after voice disposal.
    /// Observe at quiescent call boundaries: concurrent submissions/disposals can be in flight,
    /// and other voices contribute to this atomic total. Excludes immutable SoundEffect buffers,
    /// mixer/output caches, allocator overhead and process residency.
    /// </remarks>
    public static ulong LivePcmBufferBytes => MGA.GetLivePcmBufferBytes();

    /// <summary>
    /// Source sample frames consumed by the native mixer since this voice was created.
    /// Pause and starvation stop advancement; Stop/flush does not reset the counter.
    /// </summary>
    /// <remarks>
    /// This is the integer source-frame clock, not submitted buffers, milliseconds, or
    /// an estimate of speaker time. Device/output latency is not subtracted.
    /// Seeking consumers must establish a new baseline after the flushed queue drains.
    /// </remarks>
    public unsafe ulong ConsumedSampleFrames
    {
        get
        {
            AssertNotDisposed();
            return Voice == null ? 0 : MGA.Voice_GetSamplesPlayed(Voice);
        }
    }

    /// <summary>Native PCM capacity owned by this voice, including queued and reusable buffers.</summary>
    /// <remarks>Requires a live instance; use the global counter for post-disposal baseline comparisons.</remarks>
    public unsafe ulong AllocatedPcmBufferBytes
    {
        get
        {
            AssertNotDisposed();
            MGA.Voice_GetPcmBufferMemory(Voice, out var allocated, out _);
            return allocated;
        }
    }

    /// <summary>PCM payload in submitted buffers, including callbacks still retiring a buffer.</summary>
    public unsafe ulong QueuedPcmBufferBytes
    {
        get
        {
            AssertNotDisposed();
            MGA.Voice_GetPcmBufferMemory(Voice, out _, out var queued);
            return queued;
        }
    }

    private unsafe void PlatformCreate()
    {
        Voice = MGA.Voice_Create(SoundEffect.System, _sampleRate, (int)_channels);
    }

    private unsafe int PlatformGetPendingBufferCount()
    {
        if (Voice != null)
            return MGA.Voice_GetBufferCount(Voice);

        return 0;
    }

    private unsafe void PlatformPlay()
    {
        if (Voice != null)
            MGA.Voice_Play(Voice, 1);
    }

    private unsafe void PlatformPause()
    {
        if (Voice != null)
            MGA.Voice_Pause(Voice);
    }

    private unsafe void PlatformResume()
    {
        if (Voice != null)
            MGA.Voice_Resume(Voice);
    }

    private unsafe void PlatformStop()
    {
        if (Voice != null)
            MGA.Voice_Stop(Voice, 1);
    }

    private unsafe void PlatformSubmitBuffer(byte[] buffer, int offset, int count)
    {
        if (Voice != null)
        {
            fixed (byte* ptr = buffer)
                MGA.Voice_AppendBuffer(Voice, ptr + offset, (uint)count);
        }
    }

    private unsafe void PlatformDispose(bool disposing)
    {
        if (disposing)
        {
            if (Voice != null)
            {
                MGA.Voice_Destroy(Voice);
                Voice = null;
            }
        }
    }

    private unsafe void PlatformUpdateQueue()
    {
        // TODO: This really shouldn't be per-instance
        // instead this should be handled internally by
        // the native sound system.

        _buffersNeeded += MGA.Voice_GetFinishedBufferCount(Voice);
    }
}
