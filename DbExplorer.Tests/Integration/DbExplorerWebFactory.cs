using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DbExplorer.Tests.Integration;

/// <summary>
/// WebApplicationFactory that replaces SQL services with mocks so integration tests
/// do not require a real SQL Server instance.
/// </summary>
public sealed class DbExplorerWebFactory : WebApplicationFactory<Program>
{
    public Mock<IMetadataService> MetadataMock { get; } = new();
    public Mock<IDataBrowsingService> DataMock { get; } = new();
    public Mock<IIdentifierValidator> ValidatorMock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Remove real implementations
            Remove<IMetadataService>(services);
            Remove<IDataBrowsingService>(services);
            Remove<IIdentifierValidator>(services);
            Remove<IDbConnectionFactory>(services);
            Remove<SqlDialect>(services);

            services.AddSingleton(MetadataMock.Object);
            services.AddSingleton(DataMock.Object);
            services.AddSingleton(ValidatorMock.Object);
        });
    }

    private static void Remove<T>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(T));
        if (descriptor is not null) services.Remove(descriptor);
    }
}
