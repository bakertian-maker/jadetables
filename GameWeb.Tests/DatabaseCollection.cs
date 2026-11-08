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

/// <summary>
/// Tests that compare engine strength under a time limit run on their own: with other tests using the
/// CPU at the same time, the timed searches get shallower and the results become a coin toss.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StrengthCollection
{
    public const string Name = "Strength";
}
