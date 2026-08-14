namespace TokenOptimizer.Providers.Manifests;

/// <summary>
/// Provider-neutral description of a skill: enough for any adapter to
/// materialize it into that provider's native on-disk format and to read a
/// provider's native format back into this shape.
/// </summary>
public sealed record SkillManifest(
    string Id,
    string DisplayName,
    string Description,
    string TriggerHint,
    string BodyMarkdown,
    IReadOnlyList<SkillAsset> Assets);

public sealed record SkillAsset(string RelativePath, string Content);
