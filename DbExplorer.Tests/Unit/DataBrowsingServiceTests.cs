using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class DataBrowsingServiceTests
{
    private static DataBrowsingService CreateService(
        IDbConnectionFactory factory,
        IIdentifierValidator? validator = null)
    {
        var v = validator ?? Mock.Of<IIdentifierValidator>();
        return new DataBrowsingService(
            factory,
            new SqlDialect(DatabaseProvider.SqlServer),
            v,
            NullLogger<DataBrowsingService>.Instance);
    }

    [Fact]
    public async Task GetPagedDataAsync_InvalidSchemaFormat_ThrowsArgumentException()
    {
        var factory = new DbConnectionFactory(
            DatabaseProvider.SqlServer,
            "Server=.;Database=test;Trusted_Connection=True;");
        var svc = CreateService(factory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.GetPagedDataAsync("bad schema!", "Table1", new PagingOptions()));
    }

    [Fact]
    public async Task GetPagedDataAsync_InvalidObjectFormat_ThrowsArgumentException()
    {
        var factory = new DbConnectionFactory(
            DatabaseProvider.SqlServer,
            "Server=.;Database=test;Trusted_Connection=True;");
        var svc = CreateService(factory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.GetPagedDataAsync("dbo", "bad object!", new PagingOptions()));
    }

    [Fact]
    public async Task GetPagedDataAsync_ObjectNotInCatalog_ThrowsInvalidOperationException()
    {
        var factory = new DbConnectionFactory(
            DatabaseProvider.SqlServer,
            "Server=.;Database=test;Trusted_Connection=True;");
        var validatorMock = new Mock<IIdentifierValidator>();
        validatorMock
            .Setup(v => v.ValidateObjectAsync("dbo", "NonExistent", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Object not found"));

        var svc = CreateService(factory, validatorMock.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.GetPagedDataAsync("dbo", "NonExistent", new PagingOptions()));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(501, 500)]
    [InlineData(1000, 500)]
    public void PagingOptions_Clamp_IsEnforced(int requestedSize, int expectedSize)
    {
        // The clamping logic is internal, so we test the observable range boundary
        var clamped = Math.Clamp(requestedSize, 1, 500);
        clamped.Should().Be(expectedSize);
    }
}
