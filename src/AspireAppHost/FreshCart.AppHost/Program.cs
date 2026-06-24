// ---------------------------------------------------------------------------
//  FreshCart.AppHost
//  Aspire-orchestrated local boot. Backing services start as persistent
//  containers; FreshCart services run as .NET projects and inherit
//  connection strings + service-discovery names from this manifest.
//
//  Run:   dotnet run --project src/AspireAppHost/FreshCart.AppHost
//  Open:  http://localhost:15888  (Aspire dashboard)
// ---------------------------------------------------------------------------

var distributedApplicationBuilder = DistributedApplication.CreateBuilder(args);

// Stable developer credentials for the persistent backing-service containers. Aspire mints a random
// password per run by default, but WithDataVolume() + ContainerLifetime.Persistent reuse the container
// whose password was baked in on first creation, so a fresh random password on the second run no longer
// matches ("Login failed for user 'sa'"). Reading the values from configuration (the AppHost's
// appsettings.Development.json :: Parameters, or user-secrets) keeps them stable across runs and out of
// the credential-free base config, matching the repo's dev-secret convention. RabbitMQ uses a non-"guest"
// user on purpose: the broker's default "guest" account is loopback-only and cannot authenticate over the
// container network from the service processes.
var sqlServerPassword = distributedApplicationBuilder.AddParameter("sql-password", secret: true);
var postgresPassword = distributedApplicationBuilder.AddParameter("postgres-password", secret: true);
var mySqlPassword = distributedApplicationBuilder.AddParameter("mysql-password", secret: true);
var messageBrokerUserName = distributedApplicationBuilder.AddParameter("rabbitmq-username", secret: true);
var messageBrokerPassword = distributedApplicationBuilder.AddParameter("rabbitmq-password", secret: true);

// --- Relational stores ------------------------------------------------------

var sqlServer = distributedApplicationBuilder
    .AddSqlServer("sqlserver", password: sqlServerPassword)
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

// Identity (EF MigrateAsync) and Ordering (EF EnsureCreatedAsync) create their own database on startup,
// so they are left to self-provision. The two raw-Dapper databases below have no EF creator and their
// schema initializers connect straight to the named database, so it must exist first: an idempotent
// guarded creation script runs against the server before the service starts. NB: do NOT add a creation
// script to orderingdb — EnsureCreatedAsync skips schema creation when the database already exists, so a
// pre-created empty database would leave Ordering with no tables.
var identityDatabase = sqlServer.AddDatabase("identitydb");
var orderingDatabase = sqlServer.AddDatabase("orderingdb");
var inventoryDatabase = sqlServer.AddDatabase("inventorydb")
    .WithCreationScript("IF DB_ID(N'inventorydb') IS NULL CREATE DATABASE [inventorydb];");
var paymentReadDatabase = sqlServer.AddDatabase("paymentreaddb")
    .WithCreationScript("IF DB_ID(N'paymentreaddb') IS NULL CREATE DATABASE [paymentreaddb];");

var postgres = distributedApplicationBuilder
    .AddPostgres("postgres", password: postgresPassword)
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

// Aspire's AddDatabase only registers a connection string; it does not CREATE DATABASE, and a Postgres
// creation script would run against the not-yet-existing target database. Catalog and Basket therefore
// let Marten create catalogdb/basketdb from its maintenance connection (see their DependencyInjection).
var catalogDatabase = postgres.AddDatabase("catalogdb");
var basketDatabase = postgres.AddDatabase("basketdb");

var mysql = distributedApplicationBuilder
    .AddMySql("mysql", password: mySqlPassword)
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

var reportingWarehouse = mysql.AddDatabase("reportingdb");

// --- Document store ---------------------------------------------------------

var mongo = distributedApplicationBuilder
    .AddMongoDB("mongodb")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

var deliveryDatabase = mongo.AddDatabase("deliverydb");
var paymentEventStore = mongo.AddDatabase("paymentevents");
var reviewsDatabase = mongo.AddDatabase("reviewsdb");
var supportChatTranscripts = mongo.AddDatabase("supportchatdb");
var notificationsDatabase = mongo.AddDatabase("notificationsdb");

// --- Cache + broker ---------------------------------------------------------

var distributedCache = distributedApplicationBuilder
    .AddRedis("cache")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

var rabbitMq = distributedApplicationBuilder
    .AddRabbitMQ("rabbitmq", userName: messageBrokerUserName, password: messageBrokerPassword)
    .WithManagementPlugin()
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

// MassTransit reads MessageBroker:Host/UserName/Password and applies the explicit credentials over any
// embedded in the URI, so every broker-bound service is given the same stable host + credentials here.
// This single helper keeps that wiring in one place instead of repeating it per service.
IResourceBuilder<ProjectResource> ReferenceMessageBroker(IResourceBuilder<ProjectResource> service) =>
    service
        .WithReference(rabbitMq)
        .WithEnvironment("MessageBroker__Host", rabbitMq.Resource.ConnectionStringExpression)
        .WithEnvironment("MessageBroker__UserName", messageBrokerUserName)
        .WithEnvironment("MessageBroker__Password", messageBrokerPassword)
        .WaitFor(rabbitMq);

// --- Services ---------------------------------------------------------------

var identityService = distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Identity_Api>("identity")
    .WithReference(identityDatabase)
    .WithReference(distributedCache)
    .WaitFor(identityDatabase)
    .WaitFor(distributedCache);

var catalogService = ReferenceMessageBroker(distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Catalog_Api>("catalog")
    .WithReference(catalogDatabase)
    .WithReference(distributedCache)
    .WaitFor(catalogDatabase));

// Pricing is a self-contained gRPC calculator backed by an embedded SQLite file,
// so it owns no Aspire-managed backing resource and binds no broker.
distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Pricing_Grpc>("pricing");

var basketService = ReferenceMessageBroker(distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Basket_Api>("basket")
    .WithReference(basketDatabase)
    .WithReference(distributedCache)
    .WaitFor(basketDatabase));

var inventoryService = ReferenceMessageBroker(distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Inventory_Api>("inventory")
    .WithReference(inventoryDatabase)
    .WaitFor(inventoryDatabase));

// Payment is reached only over internal REST from Ordering, never through the
// public gateway, and returns capture/refund outcomes synchronously in the HTTP
// response, so its handle is not captured and it binds no broker.
distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Payment_Api>("payment")
    .WithReference(paymentReadDatabase)
    .WithReference(paymentEventStore)
    .WaitFor(paymentReadDatabase)
    .WaitFor(paymentEventStore);

var orderingService = ReferenceMessageBroker(distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Ordering_Api>("ordering")
    .WithReference(orderingDatabase)
    .WaitFor(orderingDatabase));

var deliveryService = ReferenceMessageBroker(distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Delivery_Api>("delivery")
    .WithReference(deliveryDatabase)
    .WaitFor(deliveryDatabase));

var notificationService = ReferenceMessageBroker(distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Notification_Api>("notification")
    .WithReference(notificationsDatabase)
    .WithReference(distributedCache)
    .WaitFor(notificationsDatabase)
    .WaitFor(distributedCache));

var reportingService = ReferenceMessageBroker(distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Reporting_Api>("reporting")
    .WithReference(reportingWarehouse)
    .WaitFor(reportingWarehouse));

var customerSupportService = distributedApplicationBuilder
    .AddProject<Projects.FreshCart_CustomerSupport_Api>("customersupport")
    .WithReference(supportChatTranscripts)
    .WithReference(distributedCache)
    .WaitFor(supportChatTranscripts)
    .WaitFor(distributedCache);

var reviewsService = ReferenceMessageBroker(distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Reviews_Api>("reviews")
    .WithReference(reviewsDatabase)
    .WaitFor(reviewsDatabase));

// The gateway is the single public ingress. It needs the shared Redis key ring
// for the BFF cookie exchange and a reference to every downstream cluster so
// Aspire service discovery resolves them by name.
distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Gateway_Yarp>("gateway")
    .WithReference(distributedCache)
    .WithReference(identityService)
    .WithReference(catalogService)
    .WithReference(basketService)
    .WithReference(orderingService)
    .WithReference(inventoryService)
    .WithReference(deliveryService)
    .WithReference(notificationService)
    .WithReference(reportingService)
    .WithReference(customerSupportService)
    .WithReference(reviewsService)
    .WaitFor(distributedCache)
    .WaitFor(identityService);

await distributedApplicationBuilder.Build().RunAsync().ConfigureAwait(false);
