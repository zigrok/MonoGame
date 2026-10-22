// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using MonoGame.Interop;

namespace Microsoft.Xna.Framework.Graphics;

/// <summary>The status of the last pipeline cache operation.</summary>
public enum PipelineCacheStatus
{
    /// <summary>No pipeline cache operation has run yet.</summary>
    None = 0,
    /// <summary>The pipeline cache was imported.</summary>
    Success = 1,
    /// <summary>The pipeline cache held no data.</summary>
    Empty = 2,
    /// <summary>The pipeline cache data could not be read.</summary>
    InvalidData = 3,
    /// <summary>The pipeline cache does not match this device.</summary>
    Incompatible = 4,
    /// <summary>The device does not support pipeline caches.</summary>
    Unsupported = 5,
    /// <summary>The pipeline cache operation failed.</summary>
    Error = 6,
}

/// <summary>
/// Cumulative Native shader and graphics-pipeline creation diagnostics.
/// </summary>
/// <summary>Counters and timings of the shader and pipeline work of a device.</summary>
public readonly struct ShaderPipelineDiagnostics
{
    /// <summary>The build mode the device was created with.</summary>
    public string BuildMode { get; }
    /// <summary>The number of shaders the device created.</summary>
    public ulong ShaderCreationCount { get; }
    /// <summary>The number of pipelines that came from the cache.</summary>
    public ulong PipelineCacheHits { get; }
    /// <summary>The number of pipelines that were not in the cache.</summary>
    public ulong PipelineCacheMisses { get; }
    /// <summary>The number of pipelines the device created.</summary>
    public ulong PipelineCreationCount { get; }
    /// <summary>The number of shaders translated at runtime.</summary>
    public ulong RuntimeTranslationCount { get; }
    /// <summary>The number of pipeline caches that were imported.</summary>
    public ulong PipelineCacheImports { get; }
    /// <summary>The number of pipeline caches that were rejected.</summary>
    public ulong PipelineCacheRejections { get; }
    /// <summary>The status of the last pipeline cache operation.</summary>
    public PipelineCacheStatus LastPipelineCacheStatus { get; }
    /// <summary>The time spent creating shaders, in milliseconds.</summary>
    public double ShaderCreationMilliseconds { get; }
    /// <summary>The time spent creating pipelines, in milliseconds.</summary>
    public double PipelineCreationMilliseconds { get; }
    /// <summary>The time spent translating shaders at runtime, in milliseconds.</summary>
    public double RuntimeTranslationMilliseconds { get; }

    internal ShaderPipelineDiagnostics(
        ulong shaderCreationCount,
        ulong pipelineCacheHits,
        ulong pipelineCacheMisses,
        ulong pipelineCreationCount,
        ulong runtimeTranslationCount,
        ulong pipelineCacheImports,
        ulong pipelineCacheRejections,
        PipelineCacheStatus lastPipelineCacheStatus,
        double shaderCreationMilliseconds,
        double pipelineCreationMilliseconds,
        double runtimeTranslationMilliseconds)
    {
        BuildMode = System.Environment.GetEnvironmentVariable("MG_SHADER_BUILD_MODE") ?? "Development";
        ShaderCreationCount = shaderCreationCount;
        PipelineCacheHits = pipelineCacheHits;
        PipelineCacheMisses = pipelineCacheMisses;
        PipelineCreationCount = pipelineCreationCount;
        RuntimeTranslationCount = runtimeTranslationCount;
        PipelineCacheImports = pipelineCacheImports;
        PipelineCacheRejections = pipelineCacheRejections;
        LastPipelineCacheStatus = lastPipelineCacheStatus;
        ShaderCreationMilliseconds = shaderCreationMilliseconds;
        PipelineCreationMilliseconds = pipelineCreationMilliseconds;
        RuntimeTranslationMilliseconds = runtimeTranslationMilliseconds;
    }
}

public partial class GraphicsDevice
{
    /// <summary>
    /// Gets cumulative shader and graphics-pipeline diagnostics for this device.
    /// </summary>
    /// <summary>The shader and pipeline counters of the device.</summary>
    public unsafe ShaderPipelineDiagnostics GetShaderPipelineDiagnostics()
    {
        MGG.GraphicsDevice_GetShaderPipelineDiagnostics(Handle, out var diagnostics);
        return new ShaderPipelineDiagnostics(
            diagnostics.ShaderCreationCount,
            diagnostics.PipelineCacheHits,
            diagnostics.PipelineCacheMisses,
            diagnostics.PipelineCreationCount,
            diagnostics.RuntimeTranslationCount,
            diagnostics.PipelineCacheImports,
            diagnostics.PipelineCacheRejections,
            diagnostics.LastPipelineCacheStatus,
            diagnostics.ShaderCreationMilliseconds,
            diagnostics.PipelineCreationMilliseconds,
            diagnostics.RuntimeTranslationMilliseconds);
    }

    /// <summary>
    /// Resets cumulative shader and graphics-pipeline diagnostics for this device.
    /// </summary>
    /// <summary>Resets the shader and pipeline counters of the device.</summary>
    public unsafe void ResetShaderPipelineDiagnostics()
        => MGG.GraphicsDevice_ResetShaderPipelineDiagnostics(Handle);

    /// <summary>The pipeline cache of the device, or null when there is none.</summary>
    public unsafe byte[] GetPipelineCacheData()
    {
        var size = MGG.GraphicsDevice_GetPipelineCacheDataSize(Handle);
        if (size <= 0)
            return Array.Empty<byte>();
        var data = new byte[size];
        fixed (byte* dataPointer = data)
        {
            if (!MGG.GraphicsDevice_GetPipelineCacheData(Handle, dataPointer, data.Length))
                return Array.Empty<byte>();
        }
        return data;
    }

    /// <summary>Imports a pipeline cache into the device.</summary>
    public unsafe PipelineCacheStatus ImportPipelineCache(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return PipelineCacheStatus.Empty;
        fixed (byte* dataPointer = data)
            return MGG.GraphicsDevice_ImportPipelineCache(Handle, dataPointer, data.Length);
    }
}