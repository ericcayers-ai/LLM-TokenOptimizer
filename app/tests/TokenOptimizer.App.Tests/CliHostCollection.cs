using Xunit;

namespace TokenOptimizer.App.Tests;

[CollectionDefinition("CliHost", DisableParallelization = true)]
public sealed class CliHostCollection : ICollectionFixture<CliHostTestFixture>;
