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

// Delivery's transactional outbox and Payment's atomic event+projection append commit across two
// collections in one MongoDB transaction, which a standalone mongod rejects. Aspire's AddMongoDB starts a
// standalone (with auth) and has no replica-set switch, so the container is run as a single-node replica
// set: a wrapper entrypoint generates the keyfile internal auth requires, hands off to the stock entrypoint
// (which still creates the admin user) with --replSet + --keyFile, and a backgrounded task runs rs.initiate
// once mongod answers — idempotent, since rs.status throws only until the set is initiated, so a restart
// against the persisted volume re-initiates nothing. The single member advertises the container-internal
// host, so service processes connect with directConnection=true (see ReferenceMongoDatabase) and use the
// seed they are given instead of resolving that host. The script is one line so the C# source's line
// endings can never put a stray carriage return into the shell command. Verified end-to-end against the
// mongo:7 image (fresh init, persistent-volume restart, and a committed multi-document transaction).
const string MongoReplicaSetInitScript =
    """KEYFILE=/data/configdb/replica-set.key; if [ ! -f "$KEYFILE" ]; then openssl rand -base64 756 > "$KEYFILE"; chmod 400 "$KEYFILE"; chown mongodb:mongodb "$KEYFILE"; fi; ( until mongosh --quiet -u "$MONGO_INITDB_ROOT_USERNAME" -p "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin --eval "db.adminCommand({ping:1}).ok" >/dev/null 2>&1; do sleep 0.5; done; mongosh --quiet -u "$MONGO_INITDB_ROOT_USERNAME" -p "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin --eval 'try { rs.status().ok } catch (e) { rs.initiate({_id:"rs0",members:[{_id:0,host:"127.0.0.1:27017"}]}) }' ) & exec docker-entrypoint.sh mongod --replSet rs0 --keyFile "$KEYFILE" --bind_ip_all""";

var mongo = distributedApplicationBuilder
    .AddMongoDB("mongodb")
    .WithEntrypoint("/bin/bash")
    .WithArgs(context =>
    {
        context.Args.Clear();
        context.Args.Add("-c");
        context.Args.Add(MongoReplicaSetInitScript);
        return Task.CompletedTask;
    })
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

// Wires a service to one of the MongoDB databases on the single-node replica set. directConnection=true is
// appended so the driver connects straight to the seed Aspire publishes (a random host port) and treats it
// as the primary, instead of doing replica-set discovery toward the container-internal member host it
// cannot reach. It is joined with & because Aspire's connection string already carries an auth query
// string (?authSource=admin&...). This sets the ConnectionStrings__<name> the service reads, so a separate
// WithReference is unnecessary.
IResourceBuilder<ProjectResource> ReferenceMongoDatabase(
    IResourceBuilder<ProjectResource> service,
    IResourceBuilder<MongoDBDatabaseResource> database) =>
    service
        .WithEnvironment(
            $"ConnectionStrings__{database.Resource.Name}",
            ReferenceExpression.Create($"{database.Resource.ConnectionStringExpression}&directConnection=true"))
        .WaitFor(database);

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
ReferenceMongoDatabase(
    distributedApplicationBuilder
        .AddProject<Projects.FreshCart_Payment_Api>("payment")
        .WithReference(paymentReadDatabase)
        .WaitFor(paymentReadDatabase),
    paymentEventStore);

var orderingService = ReferenceMessageBroker(distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Ordering_Api>("ordering")
    .WithReference(orderingDatabase)
    .WaitFor(orderingDatabase));

var deliveryService = ReferenceMongoDatabase(
    ReferenceMessageBroker(distributedApplicationBuilder
        .AddProject<Projects.FreshCart_Delivery_Api>("delivery")),
    deliveryDatabase);

var notificationService = ReferenceMongoDatabase(
    ReferenceMessageBroker(distributedApplicationBuilder
        .AddProject<Projects.FreshCart_Notification_Api>("notification")
        .WithReference(distributedCache)
        .WaitFor(distributedCache)),
    notificationsDatabase);

var reportingService = ReferenceMessageBroker(distributedApplicationBuilder
    .AddProject<Projects.FreshCart_Reporting_Api>("reporting")
    .WithReference(reportingWarehouse)
    .WaitFor(reportingWarehouse));

var customerSupportService = ReferenceMongoDatabase(
    distributedApplicationBuilder
        .AddProject<Projects.FreshCart_CustomerSupport_Api>("customersupport")
        .WithReference(distributedCache)
        .WaitFor(distributedCache),
    supportChatTranscripts);

var reviewsService = ReferenceMongoDatabase(
    ReferenceMessageBroker(distributedApplicationBuilder
        .AddProject<Projects.FreshCart_Reviews_Api>("reviews")),
    reviewsDatabase);

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
