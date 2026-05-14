using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Net;
using Xunit;

namespace DbExplorer.Tests.Integration;

/// <summary>
/// Smoke tests verifying the Profiler Blazor page is reachable and properly guarded.
/// </summary>
public class ProfilerPageIntegrationTests : IClassFixture<DbExplorerWebFactory>
{
    private readonly DbExplorerWebFactory _factory;

    public ProfilerPageIntegrationTests(DbExplorerWebFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateAuthenticatedClient()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(opts =>
                {
                    opts.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Test")
                        .RequireAuthenticatedUser()
                        .Build();
                });
            });
        }).CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task ProfilerPage_AuthenticatedUser_Returns200()
    {
        // The layout's ObjectTree calls these during SSR prerender — set up empty defaults
        // so it renders gracefully without a real database connection.
        _factory.MetadataMock
            .Setup(m => m.GetCurrentCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test");
        _factory.MetadataMock
            .Setup(m => m.GetCatalogsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DbExplorer.Core.Models.CatalogInfo("test")]);
        _factory.MetadataMock
            .Setup(m => m.GetSchemasAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var client = CreateAuthenticatedClient();
        var response = await client.GetAsync("/profiler");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ProfilerPage_UnauthenticatedUser_RedirectsToLogin()
    {
        // Default client has no auth — should redirect to /login
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var response = await client.GetAsync("/profiler");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location?.ToString().Should().Contain("/login");
    }
}
