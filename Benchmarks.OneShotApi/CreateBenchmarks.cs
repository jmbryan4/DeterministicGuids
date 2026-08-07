// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using DeterministicGuids;

namespace Benchmarks.OneShotApi;

/// <summary>
/// Full end-to-end A/B
/// </summary>
[MemoryDiagnoser]
public class CreateBenchmarks
{
    private const string ShortName = "python.org";
    private const string LongName = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore " + "magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo " + "consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla " + "pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id " + "est laborum. Sed ut perspiciatis unde omnis iste natus error sit voluptatem accusantium doloremque laudantium, " + "totam rem aperiam, eaque ipsa quae ab illo inventore veritatis et quasi architecto beatae vitae dicta sunt explicabo.";

    private static readonly Guid _namespace = DeterministicGuid.Namespaces.Dns;

    private string _name = ShortName;

    [Params(DeterministicGuid.Version.MD5, DeterministicGuid.Version.SHA1, DeterministicGuid.Version.SHA256)]
    public DeterministicGuid.Version Algorithm { get; set; }

    [Params("Short", "Long")]
    public string NameLength { get; set; } = "Short";

    [GlobalSetup]
    public void Setup()
    {
        _name = NameLength == "Short" ? ShortName : LongName;

        foreach (var version in new[] { DeterministicGuid.Version.MD5, DeterministicGuid.Version.SHA1, DeterministicGuid.Version.SHA256 })
        {
            foreach (string name in new[] { ShortName, LongName })
            {
                var os = CreateOneShot(_namespace, name, version);
                var lib = DeterministicGuid.Create(_namespace, name, version);
                if (os != lib)
                {
                    throw new InvalidOperationException($"Variant mismatch for {version}/{name.Length} chars: OneShot={os}, NuGet={lib}");
                }
            }
        }
    }

    [Benchmark(Baseline = true)]
    public Guid Create_DeterministicGuid() => DeterministicGuid.Create(_namespace, _name, Algorithm);

    [Benchmark]
    public Guid Create_OneShot() => CreateOneShot(_namespace, _name, Algorithm);

    // Copy of the library's NET8_0_OR_GREATER Create path with only the hash call swapped to the static one-shot HashData APIs.
    [SkipLocalsInit]
    private static Guid CreateOneShot(Guid namespaceId, string name, DeterministicGuid.Version version)
    {
        name = name.Normalize(NormalizationForm.FormC);
        int totalLen = 16 + Encoding.UTF8.GetByteCount(name);

        Span<byte> concat = totalLen <= 496 ? stackalloc byte[totalLen] : new byte[totalLen];

        namespaceId.TryWriteBytes(concat[..16], bigEndian: true, out _);
        Encoding.UTF8.GetBytes(name.AsSpan(), concat[16..]);

        Span<byte> uuidBe = stackalloc byte[16];

        switch (version)
        {
            case DeterministicGuid.Version.SHA1:
            {
                Span<byte> hashBuf = stackalloc byte[20];
                SHA1.HashData(concat, hashBuf);
                hashBuf[..16].CopyTo(uuidBe);
                uuidBe[6] = (byte)((uuidBe[6] & 0x0F) | (5 << 4));
                break;
            }
            case DeterministicGuid.Version.SHA256:
            {
                Span<byte> hashBuf = stackalloc byte[32];
                SHA256.HashData(concat, hashBuf);
                hashBuf[..16].CopyTo(uuidBe);
                uuidBe[6] = (byte)((uuidBe[6] & 0x0F) | (8 << 4));
                break;
            }
            case DeterministicGuid.Version.MD5:
            default:
            {
                Span<byte> hashBuf = stackalloc byte[16];
                MD5.HashData(concat, hashBuf);
                hashBuf.CopyTo(uuidBe);
                uuidBe[6] = (byte)((uuidBe[6] & 0x0F) | (3 << 4));
                break;
            }
        }

        uuidBe[8] = (byte)((uuidBe[8] & 0x3F) | 0x80);

        return new Guid(uuidBe, bigEndian: true);
    }
}
