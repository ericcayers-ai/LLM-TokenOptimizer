namespace TokenOptimizer.Providers;

public sealed record ProviderResult(bool Success, string Message)
{
    public static ProviderResult Ok(string message = "") => new(true, message);
    public static ProviderResult Fail(string message) => new(false, message);
}
