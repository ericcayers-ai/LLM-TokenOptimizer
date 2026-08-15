namespace TokenOptimizer.Providers.LmStudio;

public sealed record LmStudioModel(string ModelKey, string Type, long? SizeBytes = null, int? MaxContextLength = null);
