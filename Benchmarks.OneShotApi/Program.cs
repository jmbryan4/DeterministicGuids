// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BenchmarkDotNet.Running;
using Benchmarks.OneShotApi;

BenchmarkSwitcher.FromAssembly(typeof(HashApiBenchmarks).Assembly).Run(args);
