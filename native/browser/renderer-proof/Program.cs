using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Media;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.Framework;

namespace MonoGame.Browser.Conformance;

[SupportedOSPlatform("browser")]
public static partial class Proof
{
    private static readonly ConformanceGame Game = new();
    public static void Main() => Start();
    public static void Start() => Game.Run();
    [JSExport]
    public static bool Tick() => BrowserGameLoop.Tick(Game);
    [JSExport]
    public static void Audio() => Game.StartAudio();
    [JSExport]
    public static void SuspendAudio(bool suspended) => BrowserGameLoop.SetAudioSuspended(suspended);
    [JSExport]
    public static double AudioPositionSeconds() => MediaPlayer.PlayPosition.TotalSeconds;
    [JSExport]
    public static bool GraphicsPassed() => Game.Passed;
}

sealed class ConformanceGame : Game
{
    private SpriteBatch _batch = null!;
    private Texture2D _white = null!, _alpha = null!;
    private RenderTarget2D _target = null!;
    private BasicEffect _effect = null!;
    private RasterizerState _clip = null!;
    private Song? _song;
    private SoundEffect? _sound;
    private bool _audioAdvanced, _audioFinished;
    private int _resizePhase;
    public bool Passed { get; private set; }

    public ConformanceGame()
    {
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 160, PreferredBackBufferHeight = 120 };
        IsFixedTimeStep = false;
    }
    public void StartAudio()
    {
        var pcm = new byte[44100 * 2 * 2];
        for (int i = 0; i < pcm.Length / 2; i++)
        {
            short value = (short)(Math.Sin(i * Math.PI * 2 * 440 / 44100) * 2500);
            pcm[2 * i] = (byte)value;
            pcm[2 * i + 1] = (byte)(value >> 8);
        }
        using (var stream = File.Create("browser-proof.wav"))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RIFF"u8); writer.Write(pcm.Length + 36); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100);
            writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
        }
        if (_sound == null)
            using (var stream = File.OpenRead("browser-proof.wav"))
                _sound = SoundEffect.FromStream(stream);
        if (!_sound.Play(0.2f, 0.5f, 0))
            throw new InvalidOperationException("WAV SoundEffect voice did not start.");
        Console.WriteLine("MONOGAME_BROWSER_WAV_SOUNDEFFECT_PROOF=PASS");
        _song?.Dispose();
        _song = Song.FromUri("proof", new Uri("browser-proof.wav", UriKind.Relative));
        MediaPlayer.Volume = 0.2f;
        MediaPlayer.Play(_song);
        _audioAdvanced = _audioFinished = false;
    }
    protected override void Update(GameTime time)
    {
        if (Passed && _resizePhase < 2)
        {
            _resizePhase++;
            int width = _resizePhase == 1 ? 320 : 160;
            int height = _resizePhase == 1 ? 240 : 120;
            BrowserGameLoop.Resize(this, width, height);
            _effect.Projection = Matrix.CreateOrthographicOffCenter(0, width, height, 0, 0, 1);
            Passed = false;
        }
        if (_song != null && !_audioAdvanced && MediaPlayer.PlayPosition.TotalMilliseconds > 500)
        {
            _audioAdvanced = true;
            Console.WriteLine("MONOGAME_BROWSER_AUDIO_ADVANCING=PASS");
        }
        if (_audioAdvanced && !_audioFinished && MediaPlayer.State == MediaState.Stopped)
        {
            _audioFinished = true;
            Console.WriteLine("MONOGAME_BROWSER_AUDIO_DRAINED=PASS");
        }
    }
    protected override void LoadContent()
    {
        if (MonoGame.Framework.Utilities.PlatformInfo.GraphicsBackend != MonoGame.Framework.Utilities.GraphicsBackend.WebGL)
            throw new InvalidOperationException("Browser must report its distinct WebGL backend.");
        _batch = new SpriteBatch(GraphicsDevice);
        using (var stream = new MemoryStream(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==")))
            _white = Texture2D.FromStream(GraphicsDevice, stream);
        _alpha = new Texture2D(GraphicsDevice, 1, 1, false, SurfaceFormat.Alpha8);
        _alpha.SetData(new byte[] { 128 });
        _target = new RenderTarget2D(GraphicsDevice, 64, 64);
        _effect = new BasicEffect(GraphicsDevice)
        {
            VertexColorEnabled = true,
            Projection = Matrix.CreateOrthographicOffCenter(0, 160, 120, 0, 0, 1)
        };
        _clip = new RasterizerState { ScissorTestEnable = true, CullMode = CullMode.None };
    }
    protected override void Draw(GameTime time)
    {
        GraphicsDevice.SetRenderTarget(_target);
        GraphicsDevice.Clear(Color.Black);
        _batch.Begin();
        _batch.Draw(_white, new Rectangle(0, 0, 64, 32), Color.Red);
        _batch.Draw(_white, new Rectangle(0, 32, 64, 32), Color.Blue);
        _batch.End();
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Color.Black);
        _batch.Begin();
        _batch.Draw(_target, Vector2.Zero, Color.White);
        _batch.End();
        _batch.Begin(blendState: BlendState.NonPremultiplied);
        _batch.Draw(_alpha, new Rectangle(100, 0, 40, 30), Color.White);
        _batch.End();
        GraphicsDevice.ScissorRectangle = new Rectangle(70, 0, 10, 20);
        _batch.Begin(rasterizerState: _clip);
        _batch.Draw(_white, new Rectangle(64, 0, 30, 30), Color.White);
        _batch.End();
        GraphicsDevice.RasterizerState = RasterizerState.CullNone;
        GraphicsDevice.BlendState = BlendState.Opaque;
        GraphicsDevice.DepthStencilState = DepthStencilState.None;
        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList, new[]
            {
                new VertexPositionColor(new Vector3(80,60,0),Color.Lime),
                new VertexPositionColor(new Vector3(150,60,0),Color.Lime),
                new VertexPositionColor(new Vector3(115,115,0),Color.Lime)
            }, 0, 1);
        }
        if (Passed)
            return;
        var pixels = new Color[GraphicsDevice.PresentationParameters.BackBufferWidth *
            GraphicsDevice.PresentationParameters.BackBufferHeight];
        GraphicsDevice.GetBackBufferData(pixels);
        Check(pixels, 8, 8, Color.Red, "render target top");
        Check(pixels, 8, 40, Color.Blue, "render target bottom");
        Check(pixels, 110, 85, Color.Lime, "BasicEffect triangle");
        Check(pixels, 75, 10, Color.White, "scissor inside");
        Check(pixels, 85, 10, Color.Black, "scissor outside");
        Check(pixels, 120, 10, new Color(128, 128, 128), "Alpha8 sampling");
        Console.WriteLine("MONOGAME_BROWSER_GRAPHICS_PROOF=PASS");
        Console.WriteLine("MONOGAME_BROWSER_PNG_STREAM_PROOF=PASS");
        if (_resizePhase == 2)
            Console.WriteLine("MONOGAME_BROWSER_RESIZE_PROOF=PASS");
        Passed = true;
    }
    private void Check(Color[] pixels, int x, int y, Color expected, string test)
    {
        var actual = pixels[y * GraphicsDevice.PresentationParameters.BackBufferWidth + x];
        if (Math.Abs(actual.R - expected.R) > 2 || Math.Abs(actual.G - expected.G) > 2 || Math.Abs(actual.B - expected.B) > 2)
            throw new InvalidOperationException($"{test}: expected {expected}, got {actual}");
    }
}
