using Dapper;
using FreshCart.Reporting.Infrastructure.Persistence.Warehouse;
using MySqlConnector;
using Testcontainers.MySql;

namespace FreshCart.Reporting.Tests.Persistence;

/// <summary>
/// Boots a throwaway MySQL container for the warehouse projection-writer tests and creates the tables
/// the writer touches. The DDL mirrors the columns the writer and dashboard readers expect; production
/// schema provisioning is a separate, larger gap (no initializer exists for the warehouse — see the
/// REP-SCHEMA finding), so the schema lives here as test infrastructure. Shared across the collection
/// to amortise container start-up.
/// </summary>
public sealed class WarehouseIntegrationFixture : IAsyncLifetime
{
    private readonly MySqlContainer mySqlContainer = new MySqlBuilder()
        .WithImage("mysql:8.0")
        .WithDatabase("reportingdb")
        .Build();

    public IWarehouseConnectionFactory ConnectionFactory { get; private set; } = null!;

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await mySqlContainer.StartAsync();

        ConnectionString = mySqlContainer.GetConnectionString();
        ConnectionFactory = new MySqlWarehouseConnectionFactory(
            new WarehouseConnectionOptions { ConnectionString = ConnectionString });

        await CreateWarehouseTablesAsync();
    }

    public async Task DisposeAsync() => await mySqlContainer.DisposeAsync();

    private async Task CreateWarehouseTablesAsync()
    {
        const string createTablesSql = """
            CREATE TABLE projection_inbox (
                EventId        CHAR(36)     NOT NULL PRIMARY KEY,
                ProcessedOnUtc DATETIME(6)  NOT NULL
            );

            CREATE TABLE sales_facts (
                order_id        CHAR(36)       NOT NULL PRIMARY KEY,
                customer_id     CHAR(36)       NOT NULL,
                occurred_on_utc DATETIME(6)    NOT NULL,
                gross_revenue   DECIMAL(18,2)  NOT NULL,
                discount_total  DECIMAL(18,2)  NOT NULL,
                refund_total    DECIMAL(18,2)  NOT NULL,
                tax_total       DECIMAL(18,2)  NOT NULL,
                shipping_total  DECIMAL(18,2)  NOT NULL,
                net_revenue     DECIMAL(18,2)  NOT NULL,
                payment_method  VARCHAR(64)    NOT NULL
            );

            CREATE TABLE sales_line_facts (
                order_id         CHAR(36)      NOT NULL,
                product_sku      VARCHAR(64)   NOT NULL,
                product_name     VARCHAR(256)  NOT NULL,
                primary_category VARCHAR(128)  NOT NULL,
                quantity         INT           NOT NULL,
                unit_price       DECIMAL(18,2) NOT NULL,
                net_revenue      DECIMAL(18,2) NOT NULL,
                occurred_on_utc  DATETIME(6)   NOT NULL,
                PRIMARY KEY (order_id, product_sku)
            );

            CREATE TABLE customer_lifetime_value (
                customer_id    CHAR(36)      NOT NULL PRIMARY KEY,
                display_name   VARCHAR(128)  NOT NULL,
                order_count    INT           NOT NULL,
                lifetime_value DECIMAL(18,2) NOT NULL
            );
            """;

        var connection = new MySqlConnection(ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await connection.ExecuteAsync(createTablesSql).ConfigureAwait(false);
        }
    }
}
