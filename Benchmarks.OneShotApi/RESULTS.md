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

Averaged over three full runs of the protocol (see "Three-run averages" below):

| Algorithm | One-shot vs reused ThreadLocal hasher | Matches the 15–16 % degradation claim? |
|-----------|----------------------------------------|----------------------------------------|
| MD5 (v3) | **~30 % faster** (ratio 0.69–0.71) | No — opposite direction |
| SHA-1 (v5, the library default) | **~34–37 % faster** (ratio 0.63–0.66) | No — opposite direction |
| SHA-256 (v8) | **2.1–2.4× slower** | Direction yes, magnitude far larger |

- For the library's **default path (v5/SHA-1)** and for v3/MD5, PR #18's change is a clear
  *win* on this platform: rejecting it on performance grounds does not reproduce here.
- For **v8/SHA-256** the rejection is directionally right on this platform, and much worse
  than 15–16 %: the reused incremental hasher is exceptionally fast here (~66 ns for a
  26-byte input — faster than MD5/SHA-1), while one-shot `SHA256.HashData` costs ~140–158 ns.
  This looks like a macOS backend asymmetry (the instance path for SHA-2 appears to hit a
  much cheaper native path than the one-shot shim; MD5/SHA-1 show the reverse). Hypothesis,
  not root-caused.
- A flat 15–16 % degradation across algorithms did **not reproduce on this platform**. If the
  upstream Windows numbers averaged across algorithms, a large SHA-256 regression could
  plausibly read as an overall ~15 % loss — but that is an untested hypothesis about data not
  broken down per algorithm, not an explanation of it.
- **On ordering bias:** every BenchmarkDotNet case here ran in its own isolated process under
  the default job, so no case can prime or pollute another. That addresses the hypothesis for
  these runs; it says nothing about whether ordering affected the original upstream runs,
  which used a different harness on a different platform.

Practical implication (if pursuing this upstream): a per-algorithm hybrid — one-shot for
MD5/SHA-1, reused hasher for SHA-256 — would win on this platform, but the split may flip on
Windows CNG, so any change should be gated on per-platform, per-algorithm measurements.

## Three-run averages

The full protocol (Benchmark A and Benchmark B) was executed three times.
Run 1 and run 3 ran on a quiet machine (default BDN job); run 2 used `--job Medium` for
Benchmark A and ran with more background load (visibly higher StdDev/RatioSD), but every
ratio kept the same direction and similar magnitude in all three runs.

### Benchmark A — isolated hash call, mean of 3 runs

| Algorithm | ThreadLocal (ns) | OneShot (ns) | Mean ratio (per-run: R1 / R2 / R3) |
|---|---:|---:|---|
| MD5 | 264.6 | 183.0 | **0.70** (0.72 / 0.67 / 0.70) |
| SHA-1 | 245.9 | 162.4 | **0.66** (0.67 / 0.70 / 0.62) |
| SHA-256 | 71.0 | 163.0 | **2.36** (2.53 / 2.08 / 2.48) |

### Benchmark B — full `Create` path, mean OneShot/ThreadLocal ratio of 3 runs

| Algorithm | NameLength | Mean ratio (per-run: R1 / R2 / R3) | Library/ThreadLocal mean |
|---|---|---|---:|
| MD5 | Short | **0.71** (0.65 / 0.74 / 0.74) | 0.96 |
| MD5 | Long | **0.91** (0.91 / 0.90 / 0.91) | 0.98 |
| SHA-1 | Short | **0.59** (0.60 / 0.54 / 0.63) | 0.96 |
| SHA-1 | Long | **0.77** (0.77 / 0.78 / 0.75) | 1.00 |
| SHA-256 | Short | **2.07** (2.15 / 1.90 / 2.16) | 0.96 |
| SHA-256 | Long | **1.15** (1.29 / 1.06 / 1.10) | 1.03 |

Both benchmarks agree across all three runs: one-shot wins by ~30 % (MD5) and
~34–37 % (SHA-1), and loses by ~2.1–2.4× (SHA-256) on the isolated call. The
`Create_Library`/`Create_ThreadLocal` anchor stayed at 0.96–1.03 in every run. The
single-run tables below are from run 1 and are representative.

## Environment

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.6 (25G72) [Darwin 25.6.0]
Apple M3 Pro, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a
```

Branch: `perf/one-shot-hash-benchmarks`. The harness is a standalone project
(`Benchmarks.OneShotApi`) with its own `Benchmarks.OneShotApi.slnx` and a permissive
`global.json` (`10.0.100` + `rollForward: latestFeature`), so it builds against any 10.0.x SDK
without needing the repository's exact pinned SDK. It consumes the published
`DeterministicGuids` 1.0.11 NuGet package rather than the local project, so it measures the
released library and does not depend on the repository building.

Reproduce with:

```
cd Benchmarks.OneShotApi
dotnet run -c Release --project Benchmarks.OneShotApi.csproj -- --filter '*'
```

## Methodology

Rather than benchmarking the stale PR branch against master (different JIT/process/run
conditions), both hashing strategies were placed side-by-side in one project:

1. **Benchmark A — `HashApiBenchmarks`**: isolates exactly the disputed operation. Hash a
   fixed 26-byte field (16-byte DNS namespace, big-endian, + UTF-8 `"python.org"`) via the
   reused `ThreadLocal<HashAlgorithm>` (with the per-call `.Value` lookup, as the library pays
   it) vs the static one-shot `HashData`. Baseline = ThreadLocal. First hash byte returned to
   defeat dead-code elimination. A short name is deliberate: it maximises the share of total
   cost attributable to per-call overhead, which is the thing under dispute, and short names
   are the common case for this library. Benchmark B's long-name case covers the other end.
2. **Benchmark B — `CreateBenchmarks`**: two local copies of the library's
   `#if NET8_0_OR_GREATER` `Create` hot path, identical except for the hash call, plus the
   real `DeterministicGuid.Create` as a sanity anchor. Short name (`"python.org"`, stackalloc
   path) and long name (600 chars > 496 bytes, heap-fallback path). `[MemoryDiagnoser]` on.
   A `[GlobalSetup]` guard asserts all three variants produce identical GUIDs for every
   algorithm × name-length combination (it does — the run would have failed otherwise).

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

## Follow-ups

1. **Windows x64 run of this same project on bare metal** (not a hosted CI runner — shared
   VMs are too noisy to trust for anything short of a large delta). The SHA-256 asymmetry is
   very likely crypto-backend-specific, and Windows/CNG is the environment the original claim
   was made in. Until that run exists, the results above are a single-platform data point and
   nothing more.
2. The Windows numbers may well confirm the original 15–16 % regression, including for SHA-1 —
   CNG's one-shot versus reused-handle economics are not the same as CommonCrypto's. That
   outcome is as informative as the alternative and should be recorded either way.
3. If the split does differ per platform, per-algorithm data per platform is the only sound
   basis for deciding the upstream API choice. A per-algorithm hybrid is conceivable but would
   add conditional surface to a core file that is already dense with `#if` directives, which is
   a real maintenance cost borne by the maintainer — a decision for them, not a foregone
   conclusion from these numbers.
