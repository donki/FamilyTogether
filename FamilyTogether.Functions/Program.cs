using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FamilyTogether.Functions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        
        // Register services
        services.AddSingleton<IDataService, JsonDataService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<ILocationService, LocationService>();
        services.AddSingleton<IFamilyService, FamilyService>();
    })
    .Build();

host.Run();