using Xunit;

namespace TokenOptimizer.Providers.Tests.FreeToken;

/// <summary>
/// FreeTokenLocatorTests plants a real exe at the real, machine-global
/// %LOCALAPPDATA%\FreeToken Desktop\ path FreeTokenLocator probes - running
/// it in parallel with any other FreeToken test that reads that same real
/// state (FreeTokenAdapterTests' "not installed" tests) is a race. Serialize
/// the whole namespace, same convention as CliHostCollection.
/// </summary>
[CollectionDefinition("FreeToken", DisableParallelization = true)]
public sealed class FreeTokenCollection;
