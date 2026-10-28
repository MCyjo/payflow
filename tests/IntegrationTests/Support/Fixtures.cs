using Npgsql;
using Testcontainers.PostgreSql;

namespace PayFlow.IntegrationTests.Support;

/// <summary>One PostgreSQL container shared by a test collection; each test class gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public Task<string> CreateDatabaseAsync(string prefix) => PostgresAdmin.CreateDatabaseAsync(_container.GetConnectionString(), prefix);
}

internal static class PostgresAdmin
{
    public static async Task<string> CreateDatabaseAsync(string adminConnectionString, string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name, Pooling = true }.ConnectionString;
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}

public static class Poll
{
    /// <summary>Polls until the condition holds; fails the test with a clear message on timeout.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int timeoutSeconds = 60)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        Assert.Fail($"Timed out after {timeoutSeconds}s waiting for: {what}");
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

[CollectionDefinition(Name)]
public sealed class SystemCollectionDefinition : ICollectionFixture<SystemFixture>
{
    public const string Name = "system";
}
