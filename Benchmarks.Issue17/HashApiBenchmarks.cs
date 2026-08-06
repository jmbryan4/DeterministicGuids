// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;

using BenchmarkDotNet.Attributes;

namespace Benchmarks.Issue17;

/// <summary>
/// Benchmark A — isolates the exact operation disputed in issue #17 / PR #18:
/// hashing a small (26-byte) input via a reused per-thread <see cref="HashAlgorithm"/>
/// (<c>ThreadLocal.Value.TryComputeHash</c>, current library code) versus the static
/// one-shot APIs (<c>SHA1.HashData</c> etc., proposed change). Nothing else is measured.
/// </summary>
[MemoryDiagnoser]
public class HashApiBenchmarks
{
    // Mirrors the library's per-thread hasher fields exactly.
    private static readonly ThreadLocal<HashAlgorithm> Md5Tls = new(MD5.Create);
    private static readonly ThreadLocal<HashAlgorithm> Sha1Tls = new(SHA1.Create);
    private static readonly ThreadLocal<HashAlgorithm> Sha256Tls = new(SHA256.Create);

    // 16-byte DNS namespace (big-endian) + UTF8("python.org") = 26 bytes,
    // the same input shape the README benchmarks use.
    private byte[] _input = null!;

    [Params("MD5", "SHA1", "SHA256")]
    public string Algorithm { get; set; } = "SHA1";

    [GlobalSetup]
    public void Setup()
    {
        Guid dns = DeterministicGuids.DeterministicGuid.Namespaces.Dns;
        byte[] name = Encoding.UTF8.GetBytes("python.org");
        _input = new byte[16 + name.Length];
        dns.TryWriteBytes(_input.AsSpan(0, 16), bigEndian: true, out _);
        name.CopyTo(_input, 16);
    }

    [Benchmark(Baseline = true)]
    public byte ThreadLocal_TryComputeHash()
    {
        Span<byte> hash = stackalloc byte[32];
        HashAlgorithm hasher = Algorithm switch
        {
            "MD5" => Md5Tls.Value!,
            "SHA1" => Sha1Tls.Value!,
            _ => Sha256Tls.Value!,
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
