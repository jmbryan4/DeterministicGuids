// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

using BenchmarkDotNet.Attributes;

using DeterministicGuids;

namespace Benchmarks.Issue17;

/// <summary>
/// Benchmark B — full end-to-end A/B of the net8.0+ <c>DeterministicGuid.Create</c> hot path.
/// Two local copies of the library's <c>#if NET8_0_OR_GREATER</c> branch, identical except for
/// the hash call (reused ThreadLocal hasher vs static one-shot HashData), plus the real library
/// method as a sanity anchor. Short name exercises the stackalloc path; the long name (&gt;496
/// UTF-8 bytes) exercises the heap-fallback path.
/// </summary>
[MemoryDiagnoser]
public class CreatePathBenchmarks
{
    private const string ShortName = "python.org";
    private static readonly string LongName = new('x', 600);

    private static readonly ThreadLocal<HashAlgorithm> Md5Tls = new(MD5.Create);
    private static readonly ThreadLocal<HashAlgorithm> Sha1Tls = new(SHA1.Create);
    private static readonly ThreadLocal<HashAlgorithm> Sha256Tls = new(SHA256.Create);

    private static readonly Guid Namespace = DeterministicGuid.Namespaces.Dns;

    private string _name = ShortName;

    [Params(DeterministicGuid.Version.MD5, DeterministicGuid.Version.SHA1, DeterministicGuid.Version.SHA256)]
    public DeterministicGuid.Version Algorithm { get; set; }

    [Params("Short", "Long")]
    public string NameLength { get; set; } = "Short";

    [GlobalSetup]
    public void Setup()
    {
        _name = NameLength == "Short" ? ShortName : LongName;

        // Correctness guard: both local copies must agree with each other and the library
        // for every algorithm and both name lengths, otherwise the A/B is meaningless.
        foreach (var version in new[] { DeterministicGuid.Version.MD5, DeterministicGuid.Version.SHA1, DeterministicGuid.Version.SHA256 })
        {
            foreach (var name in new[] { ShortName, LongName })
            {
                Guid tl = CreateThreadLocal(Namespace, name, version);
                Guid os = CreateOneShot(Namespace, name, version);
                Guid lib = DeterministicGuid.Create(Namespace, name, version);
                if (tl != os || tl != lib)
                {
                    throw new InvalidOperationException(
                        $"Variant mismatch for {version}/{name.Length} chars: ThreadLocal={tl}, OneShot={os}, Library={lib}");
                }
            }
        }
    }

    [Benchmark(Baseline = true)]
    public Guid Create_ThreadLocal() => CreateThreadLocal(Namespace, _name, Algorithm);

    [Benchmark]
    public Guid Create_OneShot() => CreateOneShot(Namespace, _name, Algorithm);

    [Benchmark]
    public Guid Create_Library() => DeterministicGuid.Create(Namespace, _name, Algorithm);

    // Verbatim copy of the library's NET8_0_OR_GREATER Create path (current code).
    [SkipLocalsInit]
    private static Guid CreateThreadLocal(Guid namespaceId, string name, DeterministicGuid.Version version)
    {
        name = name.Normalize(NormalizationForm.FormC);
        int totalLen = 16 + Encoding.UTF8.GetByteCount(name);

        Span<byte> concat = totalLen <= 496
            ? stackalloc byte[totalLen]
            : new byte[totalLen];

        namespaceId.TryWriteBytes(concat[..16], bigEndian: true, out _);
        Encoding.UTF8.GetBytes(name.AsSpan(), concat[16..]);

        Span<byte> uuidBe = stackalloc byte[16];

        if (version == DeterministicGuid.Version.SHA1)
        {
            Span<byte> hashBuf = stackalloc byte[20];
            Sha1Tls.Value!.TryComputeHash(concat, hashBuf, out _);
            hashBuf[..16].CopyTo(uuidBe);
            uuidBe[6] = (byte)((uuidBe[6] & 0x0F) | (5 << 4));
        }
        else if (version == DeterministicGuid.Version.SHA256)
        {
            Span<byte> hashBuf = stackalloc byte[32];
            Sha256Tls.Value!.TryComputeHash(concat, hashBuf, out _);
            hashBuf[..16].CopyTo(uuidBe);
            uuidBe[6] = (byte)((uuidBe[6] & 0x0F) | (8 << 4));
        }
        else // MD5
        {
            Span<byte> hashBuf = stackalloc byte[16];
            Md5Tls.Value!.TryComputeHash(concat, hashBuf, out _);
            hashBuf.CopyTo(uuidBe);
            uuidBe[6] = (byte)((uuidBe[6] & 0x0F) | (3 << 4));
        }

        uuidBe[8] = (byte)((uuidBe[8] & 0x3F) | 0x80);

        return new Guid(uuidBe, bigEndian: true);
    }

    // Identical to CreateThreadLocal except the hash call uses the static one-shot APIs (PR #18).
    [SkipLocalsInit]
    private static Guid CreateOneShot(Guid namespaceId, string name, DeterministicGuid.Version version)
    {
        name = name.Normalize(NormalizationForm.FormC);
        int totalLen = 16 + Encoding.UTF8.GetByteCount(name);

        Span<byte> concat = totalLen <= 496
            ? stackalloc byte[totalLen]
            : new byte[totalLen];

        namespaceId.TryWriteBytes(concat[..16], bigEndian: true, out _);
        Encoding.UTF8.GetBytes(name.AsSpan(), concat[16..]);

        Span<byte> uuidBe = stackalloc byte[16];

        if (version == DeterministicGuid.Version.SHA1)
        {
            Span<byte> hashBuf = stackalloc byte[20];
            SHA1.HashData(concat, hashBuf);
            hashBuf[..16].CopyTo(uuidBe);
            uuidBe[6] = (byte)((uuidBe[6] & 0x0F) | (5 << 4));
        }
        else if (version == DeterministicGuid.Version.SHA256)
        {
            Span<byte> hashBuf = stackalloc byte[32];
            SHA256.HashData(concat, hashBuf);
            hashBuf[..16].CopyTo(uuidBe);
            uuidBe[6] = (byte)((uuidBe[6] & 0x0F) | (8 << 4));
        }
        else // MD5
        {
            Span<byte> hashBuf = stackalloc byte[16];
            MD5.HashData(concat, hashBuf);
            hashBuf.CopyTo(uuidBe);
            uuidBe[6] = (byte)((uuidBe[6] & 0x0F) | (3 << 4));
        }

        uuidBe[8] = (byte)((uuidBe[8] & 0x3F) | 0x80);

        return new Guid(uuidBe, bigEndian: true);
    }
}
