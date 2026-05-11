using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Controllers;
using DbExplorer.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class DataControllerTests
{
    private static DataController CreateController(IDataBrowsingService svc)
    {
        var controller = new DataController(
            svc,
            Microsoft.Extensions.Options.Options.Create(new DataBrowsingOptions()),
            NullLogger<DataController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public async Task GetPage_MissingSchema_ReturnsBadRequest()
    {
        var svc = new Mock<IDataBrowsingService>();
        var controller = CreateController(svc.Object);

        var result = await controller.GetPage("", "MyTable", ct: CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetPage_ObjectNotFound_Returns404()
    {
        var svc = new Mock<IDataBrowsingService>();
        svc.Setup(s => s.GetPagedDataAsync("dbo", "Ghost", It.IsAny<PagingOptions>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("not found"));

        var controller = CreateController(svc.Object);
        var result = await controller.GetPage("dbo", "Ghost", ct: CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetPage_ValidRequest_ReturnsOk()
    {
        var rows = new List<DataRow>
        {
            new(new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Test" })
        };
        var pagedResult = new PagedResult<DataRow>(rows, 1, 50, 1);

        var svc = new Mock<IDataBrowsingService>();
        svc.Setup(s => s.GetPagedDataAsync("dbo", "Users", It.IsAny<PagingOptions>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(pagedResult);

        var controller = CreateController(svc.Object);
        var result = await controller.GetPage("dbo", "Users", ct: CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<PagedResult<DataRow>>();
    }
}
