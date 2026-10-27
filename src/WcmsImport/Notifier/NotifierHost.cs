using WcmsImport.Common;

namespace WcmsImport.Notifier;

public static class NotifierHost
{
    public static Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.UseCompactConsole();
        builder.Services.AddPostgres(builder.Configuration);
        builder.Services.AddHttpClient(NotificationService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5));
        builder.Services.AddHostedService<NotificationService>();
        return builder.Build().RunAsync();
    }
}
