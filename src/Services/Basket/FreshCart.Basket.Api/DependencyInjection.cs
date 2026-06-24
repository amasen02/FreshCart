using System.Reflection;
using FluentValidation;
using FreshCart.Basket.Api.Catalog;
using FreshCart.Basket.Api.Consumers;
using FreshCart.Basket.Api.Domain;
using FreshCart.Basket.Api.Persistence;
using FreshCart.Basket.Api.Pricing;
using FreshCart.BuildingBlocks.Behaviors;
using FreshCart.BuildingBlocks.Messaging.MassTransit;
using FreshCart.BuildingBlocks.Messaging.Outbox;
using Marten;
using MassTransit;
using Npgsql;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using JasperFx;
using RabbitMQ.Client;

namespace FreshCart.Basket.Api;

/// <summary>
/// Composition root for the basket service: Marten persistence with the cached repository
/// decorator, the Pricing gRPC port, the Catalog HTTP port, MassTransit and the outbox publisher.
/// </summary>
public static class DependencyInjection
{
    private const string BasketDatabaseConnectionName = "basketdb";
    private const string PostgresMaintenanceDatabaseName = "postgres";
    private const string CacheConnectionName = "cache";
    private const string CatalogBaseAddressConfigurationKey = "Services:Catalog:BaseAddress";
    private const string MessageBrokerHealthCheckName = "rabbitmq";
    private const string ReadinessHealthCheckTag = "ready";

    public static IServiceCollection AddBasketServices(this IServiceCollection services, IConfiguration configuration)
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

        AddBasketPersistence(services, configuration);
        AddBasketCaching(services, configuration);
        AddCatalogClient(services, configuration);
        AddPricingClient(services);
        AddBasketMessaging(services, configuration);

        return services;
    }

    private static void AddBasketPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var basketConnectionString = configuration.GetConnectionString(BasketDatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string \"{BasketDatabaseConnectionName}\" missing.");

        // Aspire registers the connection string but never issues CREATE DATABASE, so Marten provisions
        // the tenant database itself from a maintenance connection pointed at the always-present
        // "postgres" database. PersistSecurityInfo keeps the password available to Marten after first open.
        var maintenanceConnectionString = new NpgsqlConnectionStringBuilder(basketConnectionString)
        {
            Database = PostgresMaintenanceDatabaseName,
            PersistSecurityInfo = true,
        }.ConnectionString;

        services.AddMarten(martenOptions =>
        {
            martenOptions.Connection(basketConnectionString);
            martenOptions.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

            martenOptions.CreateDatabasesForTenants(databaseCreation =>
            {
                databaseCreation.MaintenanceDatabase(maintenanceConnectionString);
                databaseCreation.ForTenant().CheckAgainstPgDatabase();
            });

            // Two tabs, a rapid double-tap or a request racing the price-refresh consumer all
            // read-modify-write the same customer's basket. Optimistic concurrency turns the losing
            // write into a ConcurrencyException the repository retries against a fresh snapshot,
            // instead of silently dropping an item or quantity through last-write-wins.
            martenOptions.Schema.For<ShoppingBasket>().UseOptimisticConcurrency(true);

            // The publisher polls for unprocessed messages on every cycle; without this index the
            // poll degrades to a sequential scan as the archive of processed messages grows.
            martenOptions.Schema.For<OutboxMessage>().Index(outboxMessage => outboxMessage.ProcessedOnUtc!);
        }).UseLightweightSessions();

        services.AddScoped<MartenBasketRepository>();
        services.AddScoped<IBasketRepository>(serviceProvider => new CachedBasketRepository(
            serviceProvider.GetRequiredService<MartenBasketRepository>(),
            serviceProvider.GetRequiredService<HybridCache>()));

        services.AddScoped<IBasketUnitOfWork, MartenBasketUnitOfWork>();
        services.AddScoped<IBasketPriceRefresher, MartenBasketPriceRefresher>();
        services.AddScoped<IOutboxStore, MartenOutboxStore>();

        services.AddHealthChecks()
            .AddNpgSql(basketConnectionString, name: BasketDatabaseConnectionName, tags: [ReadinessHealthCheckTag]);
    }

    private static void AddBasketCaching(IServiceCollection services, IConfiguration configuration)
    {
        var cacheConnectionString = configuration.GetConnectionString(CacheConnectionName)
            ?? throw new InvalidOperationException($"Connection string \"{CacheConnectionName}\" missing.");

        services.AddStackExchangeRedisCache(redisCacheOptions => redisCacheOptions.Configuration = cacheConnectionString);
        services.AddHybridCache();
        services.AddSingleton<IBasketCacheInvalidator, HybridBasketCacheInvalidator>();

        services.AddHealthChecks()
            .AddRedis(cacheConnectionString, name: CacheConnectionName, tags: [ReadinessHealthCheckTag]);
    }

    private static void AddCatalogClient(IServiceCollection services, IConfiguration configuration)
    {
        var catalogBaseAddress = configuration[CatalogBaseAddressConfigurationKey]
            ?? throw new InvalidOperationException($"Configuration value \"{CatalogBaseAddressConfigurationKey}\" is required.");

        services.AddHttpClient<ICatalogProductClient, CatalogProductClient>(httpClient =>
            httpClient.BaseAddress = new Uri(catalogBaseAddress));
    }

    private static void AddPricingClient(IServiceCollection services)
    {
        services.AddSingleton<PricingGrpcChannelFactory>();
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<PricingGrpcChannelFactory>().CreateClient());
        services.AddSingleton<IBasketPricingClient, GrpcBasketPricingClient>();
    }

    private static void AddBasketMessaging(IServiceCollection services, IConfiguration configuration)
    {
        services.AddRabbitMqMessageBroker(configuration, typeof(ProductPriceChangedConsumer).Assembly);

        services.Configure<OutboxPublisherOptions>(configuration.GetSection(OutboxPublisherOptions.SectionName));

        // MassTransit registers IPublishEndpoint as scoped; a hosted service is a root singleton,
        // so the publisher receives the bus itself, which implements the same interface.
        services.AddSingleton<IHostedService>(serviceProvider => new OutboxPublisher(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            serviceProvider.GetRequiredService<IBus>(),
            serviceProvider.GetRequiredService<IOptions<OutboxPublisherOptions>>(),
            serviceProvider.GetRequiredService<ILogger<OutboxPublisher>>()));

        services.AddHealthChecks()
            .AddRabbitMQ(
                _ => CreateBrokerProbeConnectionAsync(configuration),
                name: MessageBrokerHealthCheckName,
                tags: [ReadinessHealthCheckTag]);
    }

    private static Task<IConnection> CreateBrokerProbeConnectionAsync(IConfiguration configuration)
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
