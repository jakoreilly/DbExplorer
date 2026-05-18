using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class MetadataControllerTests
{
    private static MetadataController CreateController(IMetadataService svc)
    {
        var controller = new MetadataController(svc, Mock.Of<IAuditLogger>(), NullLogger<MetadataController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public async Task GetSchemas_ReturnsOkWithSchemas()
    {
        var svc = new Mock<IMetadataService>();
        svc.Setup(s => s.GetSchemasAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync([new SchemaInfo("dbo"), new SchemaInfo("sales")]);

        var controller = CreateController(svc.Object);
        var result = await controller.GetSchemas(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var schemas = ok.Value.Should().BeAssignableTo<IReadOnlyList<SchemaInfo>>().Subject;
        schemas.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetColumns_MissingSchema_ReturnsBadRequest()
    {
        var svc = new Mock<IMetadataService>();
        var controller = CreateController(svc.Object);

        var result = await controller.GetColumns("", "MyTable", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetColumns_InvalidFormat_ReturnsBadRequest()
    {
        var svc = new Mock<IMetadataService>();
        svc.Setup(s => s.GetColumnsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new ArgumentException("Invalid identifier"));
        var controller = CreateController(svc.Object);

        var result = await controller.GetColumns("bad schema!", "Table", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetDefinition_NotFound_Returns404()
    {
        var svc = new Mock<IMetadataService>();
        svc.Setup(s => s.GetObjectDefinitionAsync("dbo", "MyProc", It.IsAny<CancellationToken>()))
           .ReturnsAsync((ObjectDefinition?)null);
        var controller = CreateController(svc.Object);

        var result = await controller.GetDefinition("dbo", "MyProc", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }
}
