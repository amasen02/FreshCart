using System.Reflection;
using Azure.Identity;
using Azure.Storage.Blobs;
using FreshCart.BuildingBlocks.Messaging.MassTransit;
using FreshCart.Reporting.Application.Common.Abstractions;
using FreshCart.Reporting.Application.Projections.Consumers;
using FreshCart.Reporting.Infrastructure.Documents.Excel;
using FreshCart.Reporting.Infrastructure.Documents.Pdf;
using FreshCart.Reporting.Infrastructure.Persistence.Warehouse;
using FreshCart.Reporting.Infrastructure.Scheduled;
using FreshCart.Reporting.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuestPDF.Infrastructure;

namespace FreshCart.Reporting.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddReportingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // QuestPDF Community licence: free for non-commercial portfolio use; swap to
        // QuestPDF.Settings.License = LicenseType.Professional and configure a license key for
        // commercial deployment.
        QuestPDF.Settings.License = LicenseType.Community;

        AddWarehouse(services, configuration);
        AddBlobStorage(services, configuration);
        AddDocumentRendering(services);
        AddProjectionPipeline(services, configuration);
        AddScheduledJobs(services);

        return services;
    }

    private static void AddWarehouse(IServiceCollection services, IConfiguration configuration)
    {
        var warehouseConnectionString = configuration.GetConnectionString("reportingdb")
            ?? throw new InvalidOperationException("Connection string \"reportingdb\" is missing.");

        services.AddSingleton(new WarehouseConnectionOptions { ConnectionString = warehouseConnectionString });
        services.AddScoped<IWarehouseConnectionFactory, MySqlWarehouseConnectionFactory>();

        services.AddDbContext<WarehouseDbContext>(dbContextOptions =>
            dbContextOptions.UseMySql(
                warehouseConnectionString,
                ServerVersion.AutoDetect(warehouseConnectionString),
                mySqlOptions => mySqlOptions.EnableRetryOnFailure(maxRetryCount: 5)));

        services.AddScoped<IInvoiceRepository, InvoiceRepository>();
        services.AddScoped<ISalesReadWarehouse, DapperSalesReadWarehouse>();
        services.AddScoped<IProductReadWarehouse, DapperProductReadWarehouse>();
        services.AddScoped<ICustomerReadWarehouse, DapperCustomerReadWarehouse>();
        services.AddScoped<IDeliveryReadWarehouse, DapperDeliveryReadWarehouse>();
        services.AddScoped<IProjectionWriter, WarehouseProjectionWriter>();
    }

    private static void AddBlobStorage(IServiceCollection services, IConfiguration configuration)
    {
        var blobOptions = configuration.GetSection(BlobStorageOptions.SectionName).Get<BlobStorageOptions>()
            ?? throw new InvalidOperationException("Blob storage options missing.");

        services.AddSingleton(blobOptions);

        services.AddSingleton(serviceProvider =>
        {
            if (!string.IsNullOrWhiteSpace(blobOptions.ConnectionString))
            {
                return new BlobServiceClient(blobOptions.ConnectionString);
            }

            if (!string.IsNullOrWhiteSpace(blobOptions.ServiceUri))
            {
                return new BlobServiceClient(new Uri(blobOptions.ServiceUri), new DefaultAzureCredential());
            }

            throw new InvalidOperationException(
                "BlobStorage:ConnectionString or BlobStorage:ServiceUri must be configured.");
        });

        services.AddScoped<IDocumentStore, AzureBlobDocumentStore>();
    }

    private static void AddDocumentRendering(IServiceCollection services)
    {
        services.AddScoped<IInvoiceRenderer, QuestPdfInvoiceRenderer>();
        services.AddScoped<IExcelExporter, ClosedXmlExcelExporter>();
    }

    private static void AddProjectionPipeline(IServiceCollection services, IConfiguration configuration)
    {
        // Wire MassTransit + RabbitMQ for the projection consumers. In production the same
        // extension is reused with Azure Service Bus configuration.
        services.AddRabbitMqMessageBroker(
            configuration,
            typeof(OrderConfirmedProjectionConsumer).Assembly);
    }

    private static void AddScheduledJobs(IServiceCollection services)
    {
        services.AddHostedService<DailySalesReportBackgroundService>();
    }
}
