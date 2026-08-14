namespace TokenOptimizer.Core.Benchmarking;

public sealed record BenchmarkRow(
    string Model,
    string Stage,
    string Status,
    double? AvgTokensPerSecond,
    double? ResolveRate,
    double? CompositeScore);
