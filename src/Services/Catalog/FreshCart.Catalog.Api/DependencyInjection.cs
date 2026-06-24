using System.Reflection;
using FreshCart.BuildingBlocks.Behaviors;
using FreshCart.BuildingBlocks.Messaging.MassTransit;
using FreshCart.Catalog.Api.Data;
using FreshCart.Catalog.Api.Models;
using FreshCart.Catalog.Api.Seeding;
using FluentValidation;
using JasperFx;
using Marten;
using Marten.Schema;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FreshCart.Catalog.Api;

/// <summary>
/// Composition root for the catalog service: Marten document persistence with schema indexes,
/// the Redis-backed HybridCache, MassTransit publishing and the development data seeder.
/// </summary>
public static class DependencyInjection
{
    private const string CatalogDatabaseConnectionName = "catalogdb";
    private const string PostgresMaintenanceDatabaseName = "postgres";
    private const string CacheConnectionName = "cache";
    private const string MessageBrokerHealthCheckName = "rabbitmq";
    private const string ReadinessHealthCheckTag = "ready";

    public static IServiceCollection AddCatalogServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var apiAssembly = Assembly.GetExecutingAssembly();

        services.AddMediatR(mediatrConfiguration =>
        {
            mediatrConfiguration.RegisterServicesFromAssembly(apiAssembly);
            mediatrConfiguration.AddOpenBehavior(typeof(ValidationBehavior<,>));
            mediatrConfiguration.AddOpenBehavior(typeof(LoggingBehavior<,>));
        });

        services.AddValidatorsFromAssembly(apiAssembly);
        services.TryAddSingleton(TimeProvider.System);

        AddCatalogPersistence(services, configuration);
        AddCatalogCaching(services, configuration);
        AddCatalogMessaging(services, configuration);

        return services;
    }

    private static void AddCatalogPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var catalogConnectionString = configuration.GetConnectionString(CatalogDatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string \"{CatalogDatabaseConnectionName}\" missing.");

        // Aspire registers the connection string but never issues CREATE DATABASE, so Marten provisions
        // the tenant database itself from a maintenance connection pointed at the always-present
        // "postgres" database. PersistSecurityInfo keeps the password available to Marten after first open.
        var maintenanceConnectionString = new NpgsqlConnectionStringBuilder(catalogConnectionString)
        {
            Database = PostgresMaintenanceDatabaseName,
            PersistSecurityInfo = true,
        }.ConnectionString;

        services.AddMarten(martenOptions =>
        {
            martenOptions.Connection(catalogConnectionString);
            martenOptions.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
            martenOptions.UseSystemTextJsonForSerialization();

            martenOptions.CreateDatabasesForTenants(databaseCreation =>
            {
                databaseCreation.MaintenanceDatabase(maintenanceConnectionString);
                databaseCreation.ForTenant().CheckAgainstPgDatabase();
            });

            martenOptions.Schema.For<Product>()
                .UniqueIndex(UniqueIndexType.Computed, product => product.Sku)
                .Index(product => product.Slug)
                .Index(product => product.CategoryId)
                .Index(product => product.BrandId)
                .FullTextIndex(product => product.Name, product => product.Description!);

            martenOptions.Schema.For<Category>().Index(category => category.Slug);
            martenOptions.Schema.For<Brand>().Index(brand => brand.Slug);
        }).UseLightweightSessions();

        services.AddScoped<ICatalogQueries, MartenCatalogQueries>();
        services.AddHostedService<CatalogDataSeeder>();

        services.AddHealthChecks()
            .AddNpgSql(catalogConnectionString, name: CatalogDatabaseConnectionName, tags: [ReadinessHealthCheckTag]);
    }

    private static void AddCatalogCaching(IServiceCollection services, IConfiguration configuration)
    {
        var cacheConnectionString = configuration.GetConnectionString(CacheConnectionName)
            ?? throw new InvalidOperationException($"Connection string \"{CacheConnectionName}\" missing.");

        services.AddStackExchangeRedisCache(redisCacheOptions => redisCacheOptions.Configuration = cacheConnectionString);
        services.AddHybridCache();

        services.AddHealthChecks()
            .AddRedis(cacheConnectionString, name: CacheConnectionName, tags: [ReadinessHealthCheckTag]);
    }

    private static void AddCatalogMessaging(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRabbitMqMessageBroker(configuration);

        services.AddHealthChecks()
            .AddRabbitMQ(
                _ => CreateBrokerProbeConnectionAsync(configuration),
                name: MessageBrokerHealthCheckName,
                tags: [ReadinessHealthCheckTag]);
    }

    private static Task<RabbitMQ.Client.IConnection> CreateBrokerProbeConnectionAsync(IConfiguration configuration)
    {
        var brokerSection = configuration.GetSection(MessageBrokerOptions.SectionName);
        var brokerHost = brokerSection[nameof(MessageBrokerOptions.Host)]
            ?? throw new InvalidOperationException(
                $"Configuration value \"{MessageBrokerOptions.SectionName}:{nameof(MessageBrokerOptions.Host)}\" is required.");

        var connectionFactory = new RabbitMQ.Client.ConnectionFactory { Uri = new Uri(brokerHost) };

        var brokerUserName = brokerSection[nameof(MessageBrokerOptions.UserName)];
        if (!string.IsNullOrWhiteSpace(brokerUserName))
        {
            connectionFactory.UserName = brokerUserName;
        }

        var brokerPassword = brokerSection[nameof(MessageBrokerOptions.Password)];
        if (!string.IsNullOrWhiteSpace(brokerPassword))
        {
            connectionFactory.Password = brokerPassword;
        }

        return connectionFactory.CreateConnectionAsync();
    }
}
