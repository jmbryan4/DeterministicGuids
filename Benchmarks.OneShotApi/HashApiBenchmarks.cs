// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace Benchmarks.OneShotApi;

/// <summary>
/// Isolated (<c>ThreadLocal.Value.TryComputeHash</c>) versus the static one-shot APIs (<c>SHA1.HashData</c> etc.).
/// </summary>
[MemoryDiagnoser]
public class HashApiBenchmarks
{
    private static readonly ThreadLocal<HashAlgorithm> _md5Tls = new(MD5.Create);
    private static readonly ThreadLocal<HashAlgorithm> _sha1Tls = new(SHA1.Create);
    private static readonly ThreadLocal<HashAlgorithm> _sha256Tls = new(SHA256.Create);

    // 16-byte DNS namespace (big-endian) + UTF8("python.org") = 26 bytes
    private byte[] _input = null!;

    [Params("MD5", "SHA1", "SHA256")]
    public string Algorithm { get; set; } = "SHA1";

    [GlobalSetup]
    public void Setup()
    {
        var dns = DeterministicGuids.DeterministicGuid.Namespaces.Dns;
        byte[] name = [.. "python.org"u8];
        _input = new byte[16 + name.Length];
        dns.TryWriteBytes(_input.AsSpan(0, 16), bigEndian: true, out _);
        name.CopyTo(_input, 16);
    }

    [Benchmark(Baseline = true)]
    public byte ThreadLocal_TryComputeHash()
    {
        Span<byte> hash = stackalloc byte[32];
        var hasher = Algorithm switch
        {
            "MD5" => _md5Tls.Value!,
            "SHA1" => _sha1Tls.Value!,
            _ => _sha256Tls.Value!,
        };
        hasher.TryComputeHash(_input, hash, out _);
        return hash[0];
    }

    [Benchmark]
    public byte OneShot_HashData()
    {
        Span<byte> hash = stackalloc byte[32];
        _ = Algorithm switch
        {
            "MD5" => MD5.HashData(_input, hash),
            "SHA1" => SHA1.HashData(_input, hash),
            _ => SHA256.HashData(_input, hash),
        };
        return hash[0];
    }
}
