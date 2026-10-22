using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Microsoft.Xna.Framework.Graphics;

/// <summary>The description of one shader pipeline: the artifacts it needs and the state it is built with.</summary>
public sealed class PipelineUsageDescription
{
    /// <summary>The schema version of a pipeline description.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The schema version of this description.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>The shader artifacts of the pipeline, by name.</summary>
    public IReadOnlyDictionary<string, string> ShaderArtifacts { get; set; } = new Dictionary<string, string>();
    /// <summary>The vertex layout the pipeline expects.</summary>
    public string VertexLayout { get; set; }
    /// <summary>The primitive topology the pipeline expects.</summary>
    public string PrimitiveTopology { get; set; }
    /// <summary>The blend state the pipeline expects.</summary>
    public string BlendState { get; set; }
    /// <summary>The depth and stencil state the pipeline expects.</summary>
    public string DepthStencilState { get; set; }
    /// <summary>The rasterizer state the pipeline expects.</summary>
    public string RasterizerState { get; set; }
    /// <summary>The render target formats the pipeline expects.</summary>
    public string RenderTargetFormats { get; set; }
    /// <summary>The depth and stencil format the pipeline expects.</summary>
    public string DepthStencilFormat { get; set; }
    /// <summary>The number of samples the pipeline expects.</summary>
    public int SampleCount { get; set; } = 1;
    /// <summary>The sample mask the pipeline expects.</summary>
    public uint SampleMask { get; set; } = uint.MaxValue;
    /// <summary>The specialization constants the pipeline expects.</summary>
    public string SpecializationConstants { get; set; } = string.Empty;
    /// <summary>The backend compatibility flags the pipeline expects.</summary>
    public string BackendCompatibilityFlags { get; set; } = string.Empty;

    /// <summary>A key that identifies this description and its state.</summary>
    public string ComputeKey()
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        using (var sha256 = SHA256.Create())
        {
            writer.Write(SchemaVersion);
            var shaders = ShaderArtifacts.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            writer.Write(shaders.Length);
            foreach (var shader in shaders)
            {
                Write(writer, shader.Key);
                Write(writer, shader.Value);
            }
            Write(writer, VertexLayout);
            Write(writer, PrimitiveTopology);
            Write(writer, BlendState);
            Write(writer, DepthStencilState);
            Write(writer, RasterizerState);
            Write(writer, RenderTargetFormats);
            Write(writer, DepthStencilFormat);
            writer.Write(SampleCount);
            writer.Write(SampleMask);
            Write(writer, SpecializationConstants);
            Write(writer, BackendCompatibilityFlags);
            writer.Flush();
            return BitConverter.ToString(sha256.ComputeHash(stream.GetBuffer(), 0, (int)stream.Length))
                .Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    private static void Write(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes((value ?? string.Empty).Normalize(NormalizationForm.FormC));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}

/// <summary>One pipeline in a usage manifest.</summary>
public sealed class PipelineUsageManifestEntry
{
    /// <summary>The key of the pipeline.</summary>
    public string PipelineKey { get; set; }
    /// <summary>The priority of the pipeline: the lower the value, the earlier it is prepared.</summary>
    public int Priority { get; set; }
    /// <summary>Where the pipeline was declared.</summary>
    public string Source { get; set; }
    /// <summary>The group the pipeline priority belongs to.</summary>
    public string PriorityGroup { get; set; } = "Default";
}

/// <summary>The recorded pipeline usage of a device: the pipelines and their descriptions.</summary>
public sealed class PipelineUsageManifest
{
    /// <summary>The schema version of this manifest.</summary>
    public int SchemaVersion { get; set; } = 2;
    /// <summary>The pipelines of the manifest.</summary>
    public IReadOnlyList<PipelineUsageManifestEntry> Pipelines { get; set; } = Array.Empty<PipelineUsageManifestEntry>();
    /// <summary>The descriptions of the manifest, by pipeline key.</summary>
    public IReadOnlyDictionary<string, PipelineUsageDescription> Descriptions { get; set; }
        = new Dictionary<string, PipelineUsageDescription>();
}

/// <summary>The progress of a pipeline prewarm run.</summary>
public class PipelinePrewarmProgress
{
    /// <summary>The number of pipelines that were processed.</summary>
    public int Completed { get; set; }
    /// <summary>The number of pipelines to process.</summary>
    public int Total { get; set; }
    /// <summary>The number of pipelines that were prepared.</summary>
    public int Prepared { get; set; }
    /// <summary>The number of pipelines that were already cached.</summary>
    public int CacheHits { get; set; }
    /// <summary>The number of pipelines whose description could not be resolved.</summary>
    public int Unresolved { get; set; }
    /// <summary>The number of pipelines that failed to prepare.</summary>
    public int Failed { get; set; }
    /// <summary>The key of the pipeline.</summary>
    public string PipelineKey { get; set; }
    /// <summary>How long the run took.</summary>
    public TimeSpan Elapsed { get; set; }
}

/// <summary>The result of a pipeline prewarm run.</summary>
public sealed class PipelinePrewarmReport : PipelinePrewarmProgress
{
    /// <summary>Whether the run was cancelled.</summary>
    public bool Cancelled { get; set; }
}

/// <summary>Records which shader pipelines a device uses.</summary>
public sealed class PipelineUsageRecorder
{
    private readonly object _sync = new object();
    private readonly Dictionary<(string Key, string Group), PipelineUsageManifestEntry> _entries = new();
    private readonly Dictionary<string, PipelineUsageDescription> _descriptions = new(StringComparer.Ordinal);

    /// <summary>Declares a pipeline description with its priority.</summary>
    public void Declare(PipelineUsageDescription description, int priority, string priorityGroup = "Default", string source = "Declared")
    {
        if (description == null)
            throw new ArgumentNullException(nameof(description));
        if (string.IsNullOrWhiteSpace(priorityGroup))
            throw new ArgumentException("A priority group is required.", nameof(priorityGroup));
        Record(description, priority, source, priorityGroup);
    }

    internal void Observe(PipelineUsageDescription description)
        => Record(description, 0, "Recorded", "Development");

    /// <summary>The usage recorded so far.</summary>
    public PipelineUsageManifest Snapshot()
    {
        lock (_sync)
        {
            return new PipelineUsageManifest
            {
                Pipelines = _entries.Values
                    .OrderByDescending(entry => entry.Priority)
                    .ThenBy(entry => entry.PriorityGroup, StringComparer.Ordinal)
                    .ThenBy(entry => entry.PipelineKey, StringComparer.Ordinal)
                    .ToArray(),
                Descriptions = _descriptions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            };
        }
    }

    private void Record(PipelineUsageDescription description, int priority, string source, string group)
    {
        var key = description.ComputeKey();
        lock (_sync)
        {
            _descriptions[key] = description;
            var identity = (key, group);
            if (!_entries.TryGetValue(identity, out var entry))
            {
                _entries.Add(identity, new PipelineUsageManifestEntry
                {
                    PipelineKey = key,
                    Priority = priority,
                    Source = source,
                    PriorityGroup = group,
                });
            }
            else if (priority > entry.Priority)
            {
                entry.Priority = priority;
            }
        }
    }
}

public partial class GraphicsDevice
{
    private PipelineUsageRecorder _pipelineUsageRecorder;

    /// <summary>Starts recording the pipeline usage of the device.</summary>
    public PipelineUsageRecorder BeginPipelineUsageRecording()
    {
        if (_pipelineUsageRecorder != null)
            throw new InvalidOperationException("Pipeline usage recording is already active.");
        _pipelineUsageRecorder = new PipelineUsageRecorder();
        return _pipelineUsageRecorder;
    }

    /// <summary>Stops recording and returns the recorded usage.</summary>
    public PipelineUsageManifest EndPipelineUsageRecording()
    {
        if (_pipelineUsageRecorder == null)
            throw new InvalidOperationException("Pipeline usage recording is not active.");
        var manifest = _pipelineUsageRecorder.Snapshot();
        _pipelineUsageRecorder = null;
        return manifest;
    }

#if NATIVE
    /// <summary>Prepares the pipelines of the device ahead of time.</summary>
    public PipelinePrewarmReport PrewarmPipelines(
        PipelineUsageManifest manifest,
        Func<PipelineUsageDescription, bool> resolve,
        string priorityGroup = null,
        CancellationToken cancellationToken = default,
        IProgress<PipelinePrewarmProgress> progress = null)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));
        if (resolve == null)
            throw new ArgumentNullException(nameof(resolve));

        var entries = manifest.Pipelines
            .Where(entry => priorityGroup == null || StringComparer.Ordinal.Equals(entry.PriorityGroup, priorityGroup))
            .OrderByDescending(entry => entry.Priority)
            .ThenBy(entry => entry.PipelineKey, StringComparer.Ordinal)
            .ToArray();
        var report = new PipelinePrewarmReport { Total = entries.Length };
        var started = DateTime.UtcNow;

        foreach (var entry in entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                report.Cancelled = true;
                break;
            }

            report.PipelineKey = entry.PipelineKey;
            if (!manifest.Descriptions.TryGetValue(entry.PipelineKey, out var description) || !resolve(description))
            {
                report.Unresolved++;
            }
            else if (!Enum.TryParse(description.PrimitiveTopology, out PrimitiveType primitiveType))
            {
                report.Failed++;
            }
            else
            {
                var before = GetShaderPipelineDiagnostics();
                if (PrewarmCurrentPipeline(primitiveType))
                {
                    var after = GetShaderPipelineDiagnostics();
                    report.Prepared++;
                    if (after.PipelineCreationCount == before.PipelineCreationCount)
                        report.CacheHits++;
                }
                else
                {
                    report.Failed++;
                }
            }

            report.Completed++;
            report.Elapsed = DateTime.UtcNow - started;
            progress?.Report(new PipelinePrewarmProgress
            {
                Completed = report.Completed,
                Total = report.Total,
                Prepared = report.Prepared,
                CacheHits = report.CacheHits,
                Unresolved = report.Unresolved,
                Failed = report.Failed,
                PipelineKey = report.PipelineKey,
                Elapsed = report.Elapsed,
            });
        }

        report.Elapsed = DateTime.UtcNow - started;
        return report;
    }
#endif

    private void RecordPipelineUsage(PrimitiveType primitiveType)
    {
        if (_pipelineUsageRecorder == null)
            return;

        _pipelineUsageRecorder.Observe(CaptureCurrentPipelineDescription(primitiveType));
    }

    /// <summary>The description of the pipeline the device uses for the given primitive type.</summary>
    public PipelineUsageDescription GetCurrentPipelineDescription(PrimitiveType primitiveType)
    {
        ApplyState(true);
        return CaptureCurrentPipelineDescription(primitiveType);
    }

    private PipelineUsageDescription CaptureCurrentPipelineDescription(PrimitiveType primitiveType)
    {
        var layout = new StringBuilder();
        foreach (var binding in _vertexBuffers.Get())
        {
            layout.Append(binding.VertexBuffer.VertexDeclaration.VertexStride).Append('@').Append(binding.InstanceFrequency).Append(':');
            foreach (var element in binding.VertexBuffer.VertexDeclaration.GetVertexElements())
                layout.Append(element.VertexElementFormat).Append('/').Append(element.VertexElementUsage).Append(element.UsageIndex).Append('@').Append(element.Offset).Append(',');
            layout.Append('|');
        }

        var blend = new StringBuilder();
        for (var index = 0; index < 4; index++)
        {
            var target = _actualBlendState[index];
            blend.Append(target.AlphaBlendFunction).Append(',').Append(target.AlphaSourceBlend).Append(',').Append(target.AlphaDestinationBlend).Append(',')
                .Append(target.ColorBlendFunction).Append(',').Append(target.ColorSourceBlend).Append(',').Append(target.ColorDestinationBlend).Append(',')
                .Append(target.ColorWriteChannels).Append('|');
        }

        var renderTargetFormats = new List<string>();
        for (var index = 0; index < _currentRenderTargetCount; index++)
            renderTargetFormats.Add(_currentRenderTargetBindings[index].RenderTarget.Format.ToString());
        if (renderTargetFormats.Count == 0)
            renderTargetFormats.Add(PresentationParameters.BackBufferFormat.ToString());

        return new PipelineUsageDescription
        {
            ShaderArtifacts = new Dictionary<string, string>
            {
                ["Pixel"] = _pixelShader.ArtifactKey,
                ["Vertex"] = _vertexShader.ArtifactKey,
            },
            VertexLayout = layout.ToString(),
            PrimitiveTopology = primitiveType.ToString(),
            BlendState = blend.ToString(),
            DepthStencilState = string.Join(",", _actualDepthStencilState.DepthBufferEnable, _actualDepthStencilState.DepthBufferWriteEnable,
                _actualDepthStencilState.DepthBufferFunction, _actualDepthStencilState.StencilEnable, _actualDepthStencilState.StencilFunction,
                _actualDepthStencilState.StencilFail, _actualDepthStencilState.StencilDepthBufferFail, _actualDepthStencilState.StencilPass,
                _actualDepthStencilState.StencilMask, _actualDepthStencilState.StencilWriteMask, _actualDepthStencilState.ReferenceStencil),
            RasterizerState = string.Join(",", _actualRasterizerState.CullMode, _actualRasterizerState.FillMode,
                _actualRasterizerState.DepthBias.ToString("R", CultureInfo.InvariantCulture),
                _actualRasterizerState.SlopeScaleDepthBias.ToString("R", CultureInfo.InvariantCulture),
                _actualRasterizerState.DepthClipEnable, _actualRasterizerState.MultiSampleAntiAlias, _actualRasterizerState.ScissorTestEnable),
            RenderTargetFormats = string.Join(",", renderTargetFormats),
            DepthStencilFormat = ActiveDepthFormat.ToString(),
            SampleCount = GetActiveSampleCount(),
            SampleMask = uint.MaxValue,
            BackendCompatibilityFlags = "NativeProfile:" + Shader.Profile.ToString(CultureInfo.InvariantCulture),
        };
    }

    private int GetActiveSampleCount()
    {
        if (_currentRenderTargetCount == 0)
            return PresentationParameters.MultiSampleCount;
        var target = _currentRenderTargetBindings[0].RenderTarget;
        if (target is RenderTarget2D target2D)
            return target2D.MultiSampleCount;
        if (target is RenderTargetCube targetCube)
            return targetCube.MultiSampleCount;
#if DIRECTX
        if (target is RenderTarget3D target3D)
            return target3D.MultiSampleCount;
#endif
        return 1;
    }
}