using Npgsql;
using StackExchange.Redis;

namespace WcmsImport.Common;

// OMITTED (production): TLS to Postgres/Redis, connection-pool tuning, health endpoints, and draining
// in-flight work on shutdown (the importer leaves its message pending instead, see ImportService).
public static class Hosting
{
    public static string Required(this IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Missing required configuration value '{key}'.");

    public static string RequiredConnectionString(this IConfiguration config, string name) =>
        config.GetConnectionString(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Missing required connection string '{name}' (env ConnectionStrings__{name}).");

    public static void UseCompactConsole(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
    }

    /// <summary>A web app on a fixed port (override with PORT), pretty JSON, compact logs.</summary>
    public static WebApplicationBuilder CreateWebBuilder(string[] args, int defaultPort)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.UseCompactConsole();
        var port = builder.Configuration.GetValue("PORT", defaultPort);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.WriteIndented = true);
        return builder;
    }

    public static IServiceCollection AddPostgres(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(config.RequiredConnectionString("Postgres"))
        {
            GssEncryptionMode = GssEncryptionMode.Disable, // the runtime image has no Kerberos library to negotiate it with
        };
        return services.AddNpgsqlDataSource(connectionString.ConnectionString);
    }

    public static IServiceCollection AddRedis(this IServiceCollection services, IConfiguration config)
    {
        var options = ConfigurationOptions.Parse(config.RequiredConnectionString("Redis"));
        options.AbortOnConnectFail = false; // keep retrying instead of crashing if Redis is still starting
        return services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
    }
}
