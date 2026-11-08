// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlaUI.Mcp.Server.Logging;
using Microsoft.Extensions.Logging;

namespace FlaUI.Mcp.Tests.Logging;

/// <summary>A real <see cref="JsonlFileLoggerProvider"/> over a temp dir, shared by the on-disk tests.</summary>
internal sealed class OnDiskHarness : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flaui-ondisk-" + Guid.NewGuid().ToString("N"));
    private readonly JsonlFileLoggerProvider _provider;
    public ILoggerFactory Factory { get; }
    public ContentHasher Hasher { get; } = new();

    public OnDiskHarness(Func<string?, bool> isSensitiveProcess)
    {
        _provider = JsonlFileLoggerProvider.TryCreate(new JsonlLogOptions(_dir, 1, DateTime.UtcNow, LogLevel.Trace),
            new LogMasker(Hasher, isSensitiveProcess), out _)!;
        Factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(_provider));
    }

    public static Dictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    /// <summary>Drains and closes the file, then returns every byte written.</summary>
    public byte[] CloseAndReadAll()
    {
        Factory.Dispose();
        _provider.Dispose();   // an instance passed to AddProvider is not owned by the factory
        return Directory.GetFiles(_dir).SelectMany(File.ReadAllBytes).ToArray();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
}
