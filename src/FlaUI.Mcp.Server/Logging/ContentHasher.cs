// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Security.Cryptography;
using System.Text;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>A masked value as it appears on disk: `{"len": n, "sha8": "..."}` (spec §5, §6.1).</summary>
public readonly record struct MaskedValue(int Len, string Sha8);

/// <summary>HMAC-SHA256 under a per-process random key that is never written anywhere. Equal values in
/// ONE session's files hash equal; nothing more (spec §6.1). Tests inject the key.</summary>
public sealed class ContentHasher
{
    private readonly byte[] _key;
    public ContentHasher(byte[]? key = null) => _key = key ?? RandomNumberGenerator.GetBytes(32);

    public MaskedValue Mask(string value)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
        return new MaskedValue(value.Length, Convert.ToHexString(mac, 0, 4).ToLowerInvariant());
    }
}
