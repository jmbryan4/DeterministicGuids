# Verifying the performance claims in issue #17 / PR #18

**Question:** Issue #17 proposed replacing the reused per-thread hashers
(`ThreadLocal<HashAlgorithm>.Value.TryComputeHash(...)`) with the static one-shot APIs
(`MD5.HashData`, `SHA1.HashData`, `SHA256.HashData`) on NET7+. PR #18 was rejected with the
claim of a **15–16 % performance degradation on .NET 8/9/10**, measured across "multiple
attempts and environments" (Windows x64 / GitHub Actions), plus a suspicion of "bias in
favour of what gets benchmarked first". Is that correct?

**Scope caveat:** all numbers below are from an **Apple M3 Pro (ARM64) on macOS**, where the
.NET crypto backend (Apple CommonCrypto/CryptoKit shims) differs from Windows CNG. These
results test the general claim, not the exact environment it was made in. A Windows x64 run
is the natural follow-up where results diverge.

## Verdict (Apple Silicon / .NET 10)

**Partially correct — the claim is algorithm-dependent, not general.**

| Algorithm | One-shot vs reused ThreadLocal hasher | Matches the 15–16 % degradation claim? |
|-----------|----------------------------------------|----------------------------------------|
| MD5 (v3) | **28–35 % faster** | No — opposite direction |
| SHA-1 (v5, the library default) | **33–40 % faster** | No — opposite direction |
| SHA-256 (v8) | **2.0–2.5× slower** | Direction yes, magnitude far larger |

- For the library's **default path (v5/SHA-1)** and for v3/MD5, PR #18's change is a clear
  *win* on this platform: rejecting it on performance grounds does not reproduce here.
- For **v8/SHA-256** the rejection is directionally right on this platform, and much worse
  than 15–16 %: the reused incremental hasher is exceptionally fast here (~66 ns for a
  26-byte input — faster than MD5/SHA-1), while one-shot `SHA256.HashData` costs ~140–158 ns.
  This looks like a macOS backend asymmetry (the instance path for SHA-2 appears to hit a
  much cheaper native path than the one-shot shim; MD5/SHA-1 show the reverse). Hypothesis,
  not root-caused.
- A flat 15–16 % degradation across algorithms did **not** reproduce. If Mark's Windows
  numbers averaged across algorithms, a large SHA-256 regression could easily read as an
  overall ~15 % loss — but per-algorithm data is needed to say that.
- **No ordering bias found**: an independent plain-`Stopwatch` harness run in both orders
  (ThreadLocal→OneShot and OneShot→ThreadLocal) reproduced the BenchmarkDotNet ratios within
  a few percent in every pass.

Practical implication (if pursuing this upstream): a per-algorithm hybrid — one-shot for
MD5/SHA-1, reused hasher for SHA-256 — would win on this platform, but the split may flip on
Windows CNG, so any change should be gated on per-platform, per-algorithm measurements.

## Environment

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.6 (25G72) [Darwin 25.6.0]
Apple M3 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a
```

Branch: `perf/verify-issue-17-net10` (local-only; `global.json` bumped to the installed SDK
10.0.302 and the library's net7.0 `PublishAot` gated to Windows so the solution evaluates on
osx-arm64 — neither change is intended for merge).

## Methodology

Rather than benchmarking the stale PR branch against master (different JIT/process/run
conditions), both hashing strategies were placed side-by-side in one project:

1. **Benchmark A — `HashApiBenchmarks`**: isolates exactly the disputed operation. Hash a
   fixed 26-byte field (16-byte DNS namespace, big-endian, + UTF-8 `"python.org"` — the same
   input shape as the README benchmarks) via the reused `ThreadLocal<HashAlgorithm>` (with the
   per-call `.Value` lookup, as the library pays it) vs the static one-shot `HashData`.
   Baseline = ThreadLocal. First hash byte returned to defeat dead-code elimination.
2. **Benchmark B — `CreatePathBenchmarks`**: two local copies of the library's
   `#if NET8_0_OR_GREATER` `Create` hot path, identical except for the hash call, plus the
   real `DeterministicGuid.Create` as a sanity anchor. Short name (`"python.org"`, stackalloc
   path) and long name (600 chars > 496 bytes, heap-fallback path). `[MemoryDiagnoser]` on.
   A `[GlobalSetup]` guard asserts all three variants produce identical GUIDs for every
   algorithm × name-length combination (it does — the run would have failed otherwise).
3. **Benchmark C — `StopwatchCheck`**: an independent plain-`Stopwatch` harness
   (5 M iterations/pass, warmup, 2 repeats, **both orders**) to cross-check BDN and directly
   test the ordering-bias hypothesis. Run via
   `dotnet run -c Release --project Benchmarks.Issue17 -- stopwatch`.

All BDN runs used the default job (each benchmark case in its own isolated process).

## Benchmark A — isolated hash call (26-byte input)

| Method | Algorithm | Mean | Error | StdDev | Median | Ratio | Allocated |
|---|---|---:|---:|---:|---:|---:|---:|
| ThreadLocal_TryComputeHash | MD5 | 259.68 ns | 4.974 ns | 11.528 ns | 254.41 ns | 1.00 | – |
| OneShot_HashData | MD5 | 185.76 ns | 3.592 ns | 5.697 ns | 185.71 ns | **0.72** | – |
| ThreadLocal_TryComputeHash | SHA1 | 235.46 ns | 4.562 ns | 4.480 ns | 234.43 ns | 1.00 | – |
| OneShot_HashData | SHA1 | 157.64 ns | 3.135 ns | 5.063 ns | 155.63 ns | **0.67** | – |
| ThreadLocal_TryComputeHash | SHA256 | 62.49 ns | 0.653 ns | 1.273 ns | 62.12 ns | 1.00 | – |
| OneShot_HashData | SHA256 | 158.06 ns | 0.878 ns | 0.685 ns | 158.03 ns | **2.53** | – |

Neither variant allocates managed memory.

## Benchmark B — full `Create` path A/B

| Method | Algorithm | NameLength | Mean | Ratio | Allocated |
|---|---|---|---:|---:|---:|
| Create_ThreadLocal | MD5 | Short | 296.68 ns | 1.00 | – |
| Create_OneShot | MD5 | Short | 193.04 ns | **0.65** | – |
| Create_Library | MD5 | Short | 278.04 ns | 0.94 | – |
| Create_ThreadLocal | MD5 | Long | 1,044.15 ns | 1.00 | 640 B |
| Create_OneShot | MD5 | Long | 949.79 ns | **0.91** | 640 B |
| Create_Library | MD5 | Long | 1,036.86 ns | 0.99 | 640 B |
| Create_ThreadLocal | SHA1 | Short | 251.51 ns | 1.00 | – |
| Create_OneShot | SHA1 | Short | 152.05 ns | **0.60** | – |
| Create_Library | SHA1 | Short | 236.91 ns | 0.94 | – |
| Create_ThreadLocal | SHA1 | Long | 476.73 ns | 1.00 | 640 B |
| Create_OneShot | SHA1 | Long | 368.71 ns | **0.77** | 640 B |
| Create_Library | SHA1 | Long | 459.86 ns | 0.97 | 640 B |
| Create_ThreadLocal | SHA256 | Short | 71.42 ns | 1.00 | – |
| Create_OneShot | SHA256 | Short | 153.34 ns | **2.15** | – |
| Create_Library | SHA256 | Short | 69.98 ns | 0.98 | – |
| Create_ThreadLocal | SHA256 | Long | 314.71 ns | 1.00 | 640 B |
| Create_OneShot | SHA256 | Long | 404.87 ns | **1.29** | 640 B |
| Create_Library | SHA256 | Long | 328.04 ns | 1.04 | 640 B |

Observations:

- `Create_Library` ≈ `Create_ThreadLocal` (ratio 0.94–1.04) — the local copy of the hot path
  is faithful, so the A/B isolates only the hash-call difference.
- The hash-call delta carries straight through to the end-to-end path: v5 (the default) is
  ~40 % faster with one-shot on short names; v8 is ~2.15× slower.
- On long names the fixed hash-call delta is diluted by hashing/allocation cost but never
  changes direction. The 640 B allocation (heap buffer for >496-byte inputs) is identical
  across variants.

## Benchmark C — Stopwatch cross-check (ordering bias)

5,000,000 iterations per pass, warmup before measurement, 2 repeats, both orders:

```
=== MD5 ===
  [repeat 1] order TL->OS : ThreadLocal  266.72 ns/op | OneShot  184.05 ns/op | OneShot/TL ratio 0.690
  [repeat 1] order OS->TL : ThreadLocal  264.47 ns/op | OneShot  182.02 ns/op | OneShot/TL ratio 0.688
  [repeat 2] order TL->OS : ThreadLocal  266.99 ns/op | OneShot  186.89 ns/op | OneShot/TL ratio 0.700
  [repeat 2] order OS->TL : ThreadLocal  263.23 ns/op | OneShot  181.17 ns/op | OneShot/TL ratio 0.688

=== SHA1 ===
  [repeat 1] order TL->OS : ThreadLocal  230.54 ns/op | OneShot  143.22 ns/op | OneShot/TL ratio 0.621
  [repeat 1] order OS->TL : ThreadLocal  224.41 ns/op | OneShot  139.03 ns/op | OneShot/TL ratio 0.620
  [repeat 2] order TL->OS : ThreadLocal  225.20 ns/op | OneShot  145.47 ns/op | OneShot/TL ratio 0.646
  [repeat 2] order OS->TL : ThreadLocal  232.31 ns/op | OneShot  147.92 ns/op | OneShot/TL ratio 0.637

=== SHA256 ===
  [repeat 1] order TL->OS : ThreadLocal   66.20 ns/op | OneShot  142.48 ns/op | OneShot/TL ratio 2.152
  [repeat 1] order OS->TL : ThreadLocal   66.17 ns/op | OneShot  139.17 ns/op | OneShot/TL ratio 2.103
  [repeat 2] order TL->OS : ThreadLocal   65.96 ns/op | OneShot  138.73 ns/op | OneShot/TL ratio 2.103
  [repeat 2] order OS->TL : ThreadLocal   70.19 ns/op | OneShot  140.27 ns/op | OneShot/TL ratio 1.998

(sink: 0)
```

The ratios agree with BenchmarkDotNet (which isolates every case in its own process) and are
stable regardless of which variant runs first — the "bias in favour of what gets benchmarked
first" hypothesis is not supported by this data.

## Follow-ups

1. **Windows x64 run** (GitHub Actions or a Windows box) of this same project — the SHA-256
   asymmetry is very likely backend-specific, and that is the environment the original claim
   was made in.
2. If the Windows split differs, per-algorithm data per platform is the only sound basis for
   deciding the upstream API choice (including a possible per-algorithm hybrid).
