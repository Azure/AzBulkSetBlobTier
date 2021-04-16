using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AzBulkSetBlobTier
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    services
                        .AddApplicationInsightsTelemetryWorkerService(hostContext.Configuration["APPINSIGHTS_INSTRUMENTATIONKEY"])
                        .AddApplicationInsightsTelemetryProcessor<DependencyFilter>();

                    var config = new Config();
                    hostContext.Configuration.Bind(config);
                    config.Run = Guid.NewGuid().ToString();

                    services.AddSingleton(config);

                    services.AddHostedService<Worker>();
                });
    }
}
