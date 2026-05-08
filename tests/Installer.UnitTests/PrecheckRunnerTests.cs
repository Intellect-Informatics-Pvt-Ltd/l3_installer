using FluentAssertions;
using Installer.Actions.Prechecks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Installer.UnitTests;

public sealed class PrecheckRunnerTests
{
    [Fact]
    public async Task RunAllAsync_AllPass_CanProceedIsTrue()
    {
        var checks = new List<IPrecheck>
        {
            CreatePassingCheck("CHECK_1", "Check 1", 10),
            CreatePassingCheck("CHECK_2", "Check 2", 20)
        };

        var runner = new PrecheckRunner(checks, NullLogger<PrecheckRunner>.Instance);
        var result = await runner.RunAllAsync();

        result.CanProceed.Should().BeTrue();
        result.PassedCount.Should().Be(2);
        result.WarningCount.Should().Be(0);
        result.BlockingCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAllAsync_OneBlocking_CanProceedIsFalse()
    {
        var checks = new List<IPrecheck>
        {
            CreatePassingCheck("CHECK_1", "Check 1", 10),
            CreateBlockingCheck("CHECK_2", "Check 2", 20)
        };

        var runner = new PrecheckRunner(checks, NullLogger<PrecheckRunner>.Instance);
        var result = await runner.RunAllAsync();

        result.CanProceed.Should().BeFalse();
        result.PassedCount.Should().Be(1);
        result.BlockingCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAllAsync_WarningOnly_CanProceedIsTrue()
    {
        var checks = new List<IPrecheck>
        {
            CreatePassingCheck("CHECK_1", "Check 1", 10),
            CreateWarningCheck("CHECK_2", "Check 2", 20)
        };

        var runner = new PrecheckRunner(checks, NullLogger<PrecheckRunner>.Instance);
        var result = await runner.RunAllAsync();

        result.CanProceed.Should().BeTrue();
        result.WarningCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAllAsync_ExecutesInOrder()
    {
        var executionOrder = new List<string>();

        var check1 = CreateTrackingCheck("FIRST", 30, executionOrder);
        var check2 = CreateTrackingCheck("SECOND", 10, executionOrder);
        var check3 = CreateTrackingCheck("THIRD", 20, executionOrder);

        var runner = new PrecheckRunner([check1, check2, check3], NullLogger<PrecheckRunner>.Instance);
        await runner.RunAllAsync();

        executionOrder.Should().ContainInOrder("SECOND", "THIRD", "FIRST");
    }

    [Fact]
    public async Task RunAllAsync_CheckThrowsException_RecordedAsBlocking()
    {
        var throwingCheck = new Mock<IPrecheck>();
        throwingCheck.Setup(c => c.CheckId).Returns("THROWING");
        throwingCheck.Setup(c => c.Name).Returns("Throwing Check");
        throwingCheck.Setup(c => c.Order).Returns(10);
        throwingCheck.Setup(c => c.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test exception"));

        var runner = new PrecheckRunner([throwingCheck.Object], NullLogger<PrecheckRunner>.Instance);
        var result = await runner.RunAllAsync();

        result.CanProceed.Should().BeFalse();
        result.BlockingCount.Should().Be(1);
        result.Results[0].ErrorCode.Should().Be("ERP-CORE-SYS-0001");
    }

    private static IPrecheck CreatePassingCheck(string id, string name, int order)
    {
        var mock = new Mock<IPrecheck>();
        mock.Setup(c => c.CheckId).Returns(id);
        mock.Setup(c => c.Name).Returns(name);
        mock.Setup(c => c.Order).Returns(order);
        mock.Setup(c => c.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrecheckResult
            {
                CheckId = id,
                Name = name,
                Severity = PrecheckSeverity.Pass,
                Message = "OK"
            });
        return mock.Object;
    }

    private static IPrecheck CreateBlockingCheck(string id, string name, int order)
    {
        var mock = new Mock<IPrecheck>();
        mock.Setup(c => c.CheckId).Returns(id);
        mock.Setup(c => c.Name).Returns(name);
        mock.Setup(c => c.Order).Returns(order);
        mock.Setup(c => c.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrecheckResult
            {
                CheckId = id,
                Name = name,
                Severity = PrecheckSeverity.Block,
                Message = "Blocked",
                ErrorCode = "ERP-INST-PRE-0001"
            });
        return mock.Object;
    }

    private static IPrecheck CreateWarningCheck(string id, string name, int order)
    {
        var mock = new Mock<IPrecheck>();
        mock.Setup(c => c.CheckId).Returns(id);
        mock.Setup(c => c.Name).Returns(name);
        mock.Setup(c => c.Order).Returns(order);
        mock.Setup(c => c.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrecheckResult
            {
                CheckId = id,
                Name = name,
                Severity = PrecheckSeverity.Warning,
                Message = "Warning"
            });
        return mock.Object;
    }

    private static IPrecheck CreateTrackingCheck(string id, int order, List<string> tracker)
    {
        var mock = new Mock<IPrecheck>();
        mock.Setup(c => c.CheckId).Returns(id);
        mock.Setup(c => c.Name).Returns(id);
        mock.Setup(c => c.Order).Returns(order);
        mock.Setup(c => c.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                tracker.Add(id);
                return Task.FromResult(new PrecheckResult
                {
                    CheckId = id,
                    Name = id,
                    Severity = PrecheckSeverity.Pass,
                    Message = "OK"
                });
            });
        return mock.Object;
    }
}
