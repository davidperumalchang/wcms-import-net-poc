using WcmsImport.Common;

namespace WcmsImport.Importer;

public static class ImporterHost
{
    public static Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.UseCompactConsole();
        builder.Services.AddPostgres(builder.Configuration).AddRedis(builder.Configuration);
        builder.Services.AddHttpClient<SourceClient>(c =>
        {
            c.BaseAddress = new Uri(builder.Configuration.Required("SOURCE_BASE_URL"));
            c.Timeout = TimeSpan.FromSeconds(30);
            c.MaxResponseContentBufferSize = 16 * 1024 * 1024;
        });
        builder.Services.AddScoped<ImportWorker>();
        builder.Services.AddHostedService<ImportService>();
        return builder.Build().RunAsync();
    }
}
