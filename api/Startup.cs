using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

[assembly: FunctionsStartup(typeof(Company.Function.Startup))]

namespace Company.Function
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            var servicesToRemove = new List<ServiceDescriptor>();
            foreach (var x in builder.Services)
            {
                System.Console.WriteLine(x.ServiceType.ToString());
                if (x.ToString().Contains("WebJobsRoleEnvironmentTelemetryInitializer") ||
                    x.ToString().Contains("WebJobsTelemetryInitializer"))
                {
                    servicesToRemove.Add(x);
                }
            }
            foreach (var x in servicesToRemove)
            {
                System.Console.WriteLine("Removing " + x.ToString());
                builder.Services.Remove(x);
            }
            builder.Services.AddSingleton<ITelemetryInitializer, MyWebJobsTelemetryInitializer>();
            builder.Services.AddSingleton<ITelemetryInitializer, MyRoleEnvironmentTelemetryInitializer>();
        }
    }
}