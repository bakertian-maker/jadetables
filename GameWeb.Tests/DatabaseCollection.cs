namespace GameWeb.Tests;

/// <summary>
/// Tests that use the developer's SQL Server run one at a time: they touch the same opening-book rows
/// (the start position) inside their own rolled-back transactions, and running them in parallel can
/// make one see or wait on another's changes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseCollection
{
    public const string Name = "Database";
}
