using Microsoft.Data.SqlClient;

namespace FreshCart.Inventory.Api.Repositories;

/// <summary>
/// Hands out a single open connection per DI scope so the reservation transaction and every
/// repository call inside it share the same <see cref="SqlConnection"/>. Opening retries transient
/// failures so a contended cold boot (the database container still warming up) does not fault startup;
/// this mirrors the EnableRetryOnFailure resilience the EF-backed services already have.
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private const int MaxOpenAttempts = 5;
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(2);

    // Transient SQL Server error numbers worth retrying on a cold/contended boot: connection timeout,
    // network path, server-not-yet-available, deadlock and throttling codes. A non-transient error
    // (e.g. a bad password) is rethrown immediately rather than burning the retry budget.
    private static readonly HashSet<int> TransientErrorNumbers =
        [-2, 53, 121, 233, 258, 1205, 4060, 4221, 40197, 40501, 40613, 49918, 49919, 49920];

    private readonly string _connectionString;
    private SqlConnection? _scopedConnection;

    public SqlConnectionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
    }

    public async Task<SqlConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {
        _scopedConnection ??= await OpenWithRetryAsync(cancellationToken).ConfigureAwait(false);

        return _scopedConnection;
    }

    private async Task<SqlConnection> OpenWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxOpenAttempts; attempt++)
        {
            var connection = new SqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch (SqlException sqlException) when (attempt < MaxOpenAttempts && IsTransient(sqlException))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(BaseRetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        throw new InvalidOperationException("Connection retry loop exited without opening or throwing.");
    }

    private static bool IsTransient(SqlException sqlException) =>
        sqlException.Errors.OfType<SqlError>().Any(error => TransientErrorNumbers.Contains(error.Number));

    public async ValueTask DisposeAsync()
    {
        if (_scopedConnection is not null)
        {
            await _scopedConnection.DisposeAsync().ConfigureAwait(false);
            _scopedConnection = null;
        }
    }
}
