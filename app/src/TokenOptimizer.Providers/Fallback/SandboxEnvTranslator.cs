namespace TokenOptimizer.Providers.Fallback;

/// <summary>
/// The single translation seam every sandbox launch flows through: host-side
/// proxies bind http://127.0.0.1:&lt;port&gt;, but an env value forwarded verbatim into
/// a container resolves that loopback to the container itself, where nothing
/// listens. Translate rewrites loopback-host URLs to host.docker.internal
/// (preserving scheme, port, path and query) and drops *CONFIG_DIR* keys,
/// whose Windows paths can never resolve inside the Linux container.
/// </summary>
public static class SandboxEnvTranslator
{
    public const string ContainerLoopbackHost = "host.docker.internal";

    public static IReadOnlyDictionary<string, string>? Translate(IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null || environment.Count == 0)
            return environment;

        Dictionary<string, string> translated = new(environment.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environment)
        {
            // Windows config-dir paths are meaningless in the container either way:
            // isolated profiles have no mount to satisfy them, non-isolated launches
            // already provide config via the read-only /root/.claude mount.
            if (key.EndsWith("CONFIG_DIR", StringComparison.OrdinalIgnoreCase))
                continue;
            translated[key] = TranslateValue(value);
        }

        return translated.Count == 0 ? null : translated;
    }

    private static string TranslateValue(string value)
    {
        // Port-only values (e.g. CLAUDE_MEM_WORKER_PORT=37778) carry no host to
        // remap - only absolute http(s) URLs pointing at the host loopback change.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !IsHostLoopback(uri.Host))
            return value;

        return new UriBuilder(uri) { Host = ContainerLoopbackHost }.Uri.ToString();
    }

    private static bool IsHostLoopback(string host) =>
        host == "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
}
