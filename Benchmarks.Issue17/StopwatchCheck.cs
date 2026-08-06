// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Benchmarks.Issue17;

/// <summary>
/// Benchmark C — an independent plain-Stopwatch cross-check of the BenchmarkDotNet results,
/// run in BOTH orders (ThreadLocal first, then OneShot first) to directly test the
/// "bias in favour of what gets benchmarked first" hypothesis from PR #18.
/// Invoke with: dotnet run -c Release --project Benchmarks.Issue17 -- stopwatch
/// </summary>
public static class StopwatchCheck
{
    private const int WarmupIterations = 500_000;
    private const int Iterations = 5_000_000;
    private const int Repeats = 2;

    private static readonly ThreadLocal<HashAlgorithm> Md5Tls = new(MD5.Create);
    private static readonly ThreadLocal<HashAlgorithm> Sha1Tls = new(SHA1.Create);
    private static readonly ThreadLocal<HashAlgorithm> Sha256Tls = new(SHA256.Create);

    private static byte _sink;

    public static void Run()
    {
        byte[] input = BuildInput();

        Console.WriteLine($"Stopwatch harness — {Iterations:N0} iterations/pass, {Repeats} repeats, both orders");
        Console.WriteLine($"Input: {input.Length} bytes (DNS namespace + \"python.org\")");
        Console.WriteLine();

        foreach (string algo in new[] { "MD5", "SHA1", "SHA256" })
        {
            Console.WriteLine($"=== {algo} ===");

            // Warmup both variants before any measurement.
            RunPass(algo, input, oneShot: false, WarmupIterations);
            RunPass(algo, input, oneShot: true, WarmupIterations);

            for (int repeat = 1; repeat <= Repeats; repeat++)
            {
                // Order 1: ThreadLocal first.
                double tl1 = Measure(algo, input, oneShot: false);
                double os1 = Measure(algo, input, oneShot: true);
                Console.WriteLine($"  [repeat {repeat}] order TL->OS : ThreadLocal {tl1,7:F2} ns/op | OneShot {os1,7:F2} ns/op | OneShot/TL ratio {os1 / tl1:F3}");

                // Order 2: OneShot first.
                double os2 = Measure(algo, input, oneShot: true);
                double tl2 = Measure(algo, input, oneShot: false);
                Console.WriteLine($"  [repeat {repeat}] order OS->TL : ThreadLocal {tl2,7:F2} ns/op | OneShot {os2,7:F2} ns/op | OneShot/TL ratio {os2 / tl2:F3}");
            }

            Console.WriteLine();
        }

        Console.WriteLine($"(sink: {_sink})");
    }

    private static byte[] BuildInput()
    {
        Guid dns = DeterministicGuids.DeterministicGuid.Namespaces.Dns;
        byte[] name = Encoding.UTF8.GetBytes("python.org");
        byte[] input = new byte[16 + name.Length];
        dns.TryWriteBytes(input.AsSpan(0, 16), bigEndian: true, out _);
        name.CopyTo(input, 16);
        return input;
    }

    private static double Measure(string algo, byte[] input, bool oneShot)
    {
        var sw = Stopwatch.StartNew();
        RunPass(algo, input, oneShot, Iterations);
        sw.Stop();
        return sw.Elapsed.TotalNanoseconds / Iterations;
    }

    private static void RunPass(string algo, byte[] input, bool oneShot, int iterations)
    {
        Span<byte> hash = stackalloc byte[32];

        if (oneShot)
        {
            switch (algo)
            {
                case "MD5":
                    for (int i = 0; i < iterations; i++)
                    {
                        MD5.HashData(input, hash);
                    }

                    break;
                case "SHA1":
                    for (int i = 0; i < iterations; i++)
                    {
                        SHA1.HashData(input, hash);
                    }

                    break;
                default:
                    for (int i = 0; i < iterations; i++)
                    {
                        SHA256.HashData(input, hash);
                    }

                    break;
            }
        }
        else
        {
            // The library resolves ThreadLocal.Value on every Create call, so the
            // lookup belongs inside the loop to reflect the real per-call cost.
            ThreadLocal<HashAlgorithm> tls = algo switch
            {
                "MD5" => Md5Tls,
                "SHA1" => Sha1Tls,
                _ => Sha256Tls,
            };

            for (int i = 0; i < iterations; i++)
            {
                tls.Value!.TryComputeHash(input, hash, out _);
            }
        }

        _sink ^= hash[0];
    }
}
