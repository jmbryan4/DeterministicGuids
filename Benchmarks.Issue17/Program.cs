// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Benchmarks.Issue17;

using BenchmarkDotNet.Running;

if (args.Length > 0 && string.Equals(args[0], "stopwatch", StringComparison.OrdinalIgnoreCase))
{
    StopwatchCheck.Run();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(HashApiBenchmarks).Assembly).Run(args);
