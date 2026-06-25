namespace FreshCart.Reporting.Tests.Persistence;

/// <summary>
/// Binds the warehouse projection-writer tests to the shared <see cref="WarehouseIntegrationFixture"/>
/// so the MySQL container starts once for the whole suite rather than per test class.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WarehouseIntegrationCollection : ICollectionFixture<WarehouseIntegrationFixture>
{
    public const string Name = "Reporting warehouse integration";
}
