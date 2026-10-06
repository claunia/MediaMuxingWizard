namespace MMW.Metadata.Tests.Infrastructure;

/// <summary>Tests that read or modify the MMW_*_API_KEY environment variables run serially.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "Environment variables";
}
