using dynamicworkflows;

using Microsoft.Azure.Functions.Extensions.DependencyInjection;

[assembly: FunctionsStartup(typeof(Startup))]
namespace dynamicworkflows
{
    using System;
    using Microsoft.Extensions.Configuration;

    public class Startup : FunctionsStartup
        {
            private IConfiguration configuration;
            public override void Configure(IFunctionsHostBuilder builder)
            {

                this.configuration = new ConfigurationBuilder()
                    .AddEnvironmentVariables()
                    .Build();

            }
        }
}

