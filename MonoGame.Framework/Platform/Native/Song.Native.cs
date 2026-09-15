// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Threading;
using Microsoft.Xna.Framework.Audio;
using MonoGame.Interop;


namespace Microsoft.Xna.Framework.Media;

public sealed partial class Song : IEquatable<Song>, IDisposable
{
    private unsafe MGM_AudioDecoder* _decoder;
    private unsafe MGA_Voice* _voice;

    private MGM_AudioDecoderInfo _info;

#if BROWSER
    private static readonly System.Collections.Generic.List<Song> BrowserSongs = new();
    private bool _browserStarted, _browserFinished, _browserPaused;

    internal static unsafe void PumpBrowserAudio()
    {
        foreach (var song in BrowserSongs.ToArray())
        {
            if (song._browserPaused)
                continue;
            var queued = MGA.Voice_GetBufferCount(song._voice);
            if (song._browserFinished)
            {
                if (queued == 0)
                {
                    BrowserSongs.Remove(song);
                    song.DonePlaying?.Invoke(song, EventArgs.Empty);
                }
                continue;
            }
            if (queued >= 3)
                continue;
            song._browserFinished = MGM.AudioDecoder_Decode(song._decoder, out var buffer, out var size) != 0;
            if (size == 0)
                continue;
            MGA.Voice_AppendBuffer(song._voice, buffer, size);
            if (!song._browserStarted)
            {
                MGA.Voice_Play(song._voice, 0);
                song._browserStarted = true;
            }
        }
    }
#else
    private readonly ManualResetEvent _stop = new ManualResetEvent(false);
    private Thread _thread;
#endif

    private float _volume = 1.0f;

#if !BROWSER
    private unsafe void DecoderStream()
    {
        bool start_voice = true;
        bool finished = false;

        while (true)
        {
            // Do we need to stop?
            if (_stop.WaitOne(0))
                break;

            var count = MGA.Voice_GetBufferCount(_voice);
            if (count > 2)
            {
                // TODO: This sucks... add OnBufferEnd type of callback
                // into the voice API so we don't have useless sleeps.
                Thread.Sleep(100);
                continue;
            }

            uint size;
            byte* buffer;
            finished = MGM.AudioDecoder_Decode(_decoder, out buffer, out size) == 0 ? false : true;

            if (size > 0)
            {
                MGA.Voice_AppendBuffer(_voice, buffer, size);

                if (start_voice)
                {
                    MGA.Voice_Play(_voice, 0);
                    start_voice = false;
                }
            }

            if (finished)
            {
                // Signal on the main thread.
                Threading.OnUIThread(() => DonePlaying(this, EventArgs.Empty));
                break;
            }
        }

        // We're done streaming.
    }
#endif

    #region The playback API used by MediaPlayer

    private unsafe void PlatformInitialize(string filePath)
    {
        _decoder = MGM.AudioDecoder_Create(filePath, out _info);

        if (_decoder == null)
#if BROWSER
            throw new InvalidOperationException("Cannot decode browser song. Stage a valid PCM16 WAV, Ogg Vorbis or MP3 file before creating Song.");
#else
            return;
#endif

        SoundEffect.Initialize();

        _voice = MGA.Voice_Create(SoundEffect.System, _info.samplerate, _info.channels);

        _duration = TimeSpan.FromMilliseconds(_info.duration);
    }

    private unsafe void PlatformDispose(bool disposing)
    {
        Stop();

        if (_voice != null)
        {
            MGA.Voice_Destroy(_voice);
            _voice = null;
        }

        if (_decoder != null)
        {
            MGM.AudioDecoder_Destroy(_decoder);
            _decoder = null;
        }
    }

    private int PlatformGetPlayCount()
    {
        return _playCount;
    }

    internal unsafe float Volume
    {
        get
        {
            return _volume;
        }

        set
        {
            _volume = value;

            if (_voice != null)
                MGA.Voice_SetVolume(_voice, _volume);
        }
    }

    internal unsafe TimeSpan Position
    {
        get
        {
            if (_voice == null)
                return TimeSpan.Zero;

            var milliseconds = MGA.Voice_GetPosition(_voice);
            if (_duration.TotalMilliseconds > 0)
                milliseconds %= (ulong)_duration.TotalMilliseconds;

            return TimeSpan.FromMilliseconds(milliseconds);
        }
    }

    internal unsafe void Play(TimeSpan? startPosition, FinishedPlayingHandler handler)
    {
        if (_decoder == null)
            return;

        ulong milliseconds = 0;
        if (startPosition.HasValue)
            milliseconds = (ulong)startPosition.Value.TotalMilliseconds;

        // Only setup the finished callback once.
        if (DonePlaying == null)
            DonePlaying += handler;

        // Stop the current playback which cleans stuff up.
        Stop(true);

        // Move the decoder to the new position.
        MGM.AudioDecoder_SetPosition(_decoder, milliseconds);

#if BROWSER
        _browserStarted = _browserFinished = _browserPaused = false;
        BrowserSongs.Add(this);
#else
        // The thread does the rest of the work.
        _stop.Reset();
        _thread = new Thread(DecoderStream);
        _thread.Name = "MGSongDecoder";
        _thread.Priority = ThreadPriority.BelowNormal;
        _thread.Start();
#endif

        _playCount++;
    }

    internal unsafe void Pause()
    {
        if (_voice == null)
            return;

        // The thread will stop processing on its own.
#if BROWSER
        _browserPaused = true;
#endif
        MGA.Voice_Pause(_voice);
    }

    internal unsafe void Resume()
    {
        if (_voice == null)
            return;

#if BROWSER
        _browserPaused = false;
#endif
        MGA.Voice_Resume(_voice);
    }

    internal unsafe void Stop(bool immediate = false)
    {
#if BROWSER
        BrowserSongs.Remove(this);
#else
        if (_thread != null)
        {
            // Halt the thread.
            _stop.Set();
            _thread.Join();
            _thread = null;
        }
#endif

        if (_voice != null)
            MGA.Voice_Stop(_voice, (byte)(immediate ? 1 : 0));
    }


    #endregion

    #region Media Library Features Not Supported

    private Album PlatformGetAlbum()
    {
        // Not Supported.
        return null;
    }

    private Artist PlatformGetArtist()
    {
        // Not Supported.
        return null;
    }

    private Genre PlatformGetGenre()
    {
        // Not Supported.
        return null;
    }

    private bool PlatformIsProtected()
    {
        // Not Supported.
        return false;
    }

    private bool PlatformIsRated()
    {
        // Not Supported.
        return false;
    }

    private int PlatformGetRating()
    {
        // Not Supported.
        return 0;
    }

    private int PlatformGetTrackNumber()
    {
        // Not Supported.
        return 0;
    }

    #endregion
}
