using DbExplorer.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace DbExplorer.Tests.Integration;

public class MetadataApiIntegrationTests : IClassFixture<DbExplorerWebFactory>
{
    private readonly DbExplorerWebFactory _factory;
    private HttpClient _client;

    public MetadataApiIntegrationTests(DbExplorerWebFactory factory)
    {
        _factory = factory;

        // Create client with test auth handler so all requests appear authenticated
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(opts =>
                {
                    // Override default policy to accept our test scheme
                    opts.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Test")
                        .RequireAuthenticatedUser()
                        .Build();
                });
            });
        }).CreateClient();
    }

    [Fact]
    public async Task GetSchemas_ReturnsOk_WithSchemaList()
    {
        _factory.MetadataMock
            .Setup(m => m.GetSchemasAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SchemaInfo("dbo"), new SchemaInfo("hr")]);

        var response = await _client.GetAsync("/api/metadata/schemas");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var schemas = await response.Content.ReadFromJsonAsync<List<SchemaInfo>>();
        schemas.Should().HaveCount(2);
        schemas![0].SchemaName.Should().Be("dbo");
    }

    [Fact]
    public async Task GetObjects_WithSchema_FiltersCorrectly()
    {
        _factory.MetadataMock
            .Setup(m => m.GetObjectsAsync("dbo", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DatabaseObjectInfo("dbo", "Users", "TABLE")]);

        var response = await _client.GetAsync("/api/metadata/objects?schema=dbo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var objects = await response.Content.ReadFromJsonAsync<List<DatabaseObjectInfo>>();
        objects.Should().HaveCount(1);
        objects![0].ObjectName.Should().Be("Users");
    }

    [Fact]
    public async Task GetColumns_MissingParams_Returns400()
    {
        var response = await _client.GetAsync("/api/metadata/columns?schema=&objectName=");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetDefinition_NotFound_Returns404()
    {
        _factory.MetadataMock
            .Setup(m => m.GetObjectDefinitionAsync("dbo", "NoProc", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ObjectDefinition?)null);

        var response = await _client.GetAsync("/api/metadata/definition?schema=dbo&objectName=NoProc");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
