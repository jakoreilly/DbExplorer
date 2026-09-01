
// ── BastionSignIn ────────────────────────────────────────────────────────────
// Only the disabled-guard path is unit-testable without a full HTTP pipeline -
// same scope boundary the existing Windows/Google actions already have here
// (their Challenge/AuthenticateAsync paths aren't unit tested either).

using System.Collections.Generic;
using DbExplorer.Controllers;
using DbExplorer.Core.Interfaces;
using DbExplorer.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class ExternalAuthControllerBastionTests
{
    private static ExternalAuthController MakeController(bool bastionEnabled)
    {
        // Options.Create is ambiguous here: the DbExplorer.Options NAMESPACE
        // collides with Microsoft.Extensions.Options's OPTIONS CLASS name.
        // Authority/ClientId/ClientSecret must be present or BastionSignIn short-circuits
        // to a "not fully configured" 503 before it ever reaches the Challenge (that guard
        // was added in cd00705, after this fixture was written). The disabled-path test is
        // unaffected — Enabled=false is checked first.
        var options = Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            Bastion = new BastionAuthOptions
            {
                Enabled = bastionEnabled,
                Authority = "https://identity.example.test",
                ClientId = "dbexplorer-test",
                ClientSecret = "test-secret",
            },
        });
        var audit = new Mock<IAuditLogger>();
        var logger = new Mock<ILogger<ExternalAuthController>>();
        return new ExternalAuthController(options, audit.Object, logger.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public void BastionSignIn_WhenDisabled_Returns503()
    {
        var controller = MakeController(bastionEnabled: false);

        var result = controller.BastionSignIn();

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusResult.StatusCode);
    }

    [Fact]
    public void BastionSignIn_WhenEnabled_ChallengesTheBastionIdentityScheme()
    {
        var controller = MakeController(bastionEnabled: true);

        var result = controller.BastionSignIn();

        var challenge = Assert.IsType<ChallengeResult>(result);
        Assert.Contains("BastionIdentity", challenge.AuthenticationSchemes);
    }

    [Fact]
    public void BastionSignIn_WhenEnabledButNotConfigured_Returns503()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            Bastion = new BastionAuthOptions { Enabled = true }, // no Authority/ClientId/ClientSecret
        });
        var controller = new ExternalAuthController(
            options, new Mock<IAuditLogger>().Object, new Mock<ILogger<ExternalAuthController>>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = controller.BastionSignIn();

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusResult.StatusCode);
    }
}
