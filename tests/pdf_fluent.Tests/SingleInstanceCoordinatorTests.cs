using pdf_fluent.Services;
using Xunit;

namespace pdf_fluent.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task SecondaryInstanceForwardsArgumentsToThePrimaryInstance()
    {
        string instanceName = $"FluentPDF.Tests.{Guid.NewGuid():N}";
        using var primary = new SingleInstanceCoordinator(instanceName);
        using var secondary = new SingleInstanceCoordinator(instanceName);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(args =>
        {
            received.TrySetResult(args);
            return Task.CompletedTask;
        });
        string[] sentArgs = [@"C:\Documents with spaces\document.pdf", "notes.txt"];

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
        Assert.True(await secondary.TrySendAsync(sentArgs));
        Assert.Equal(sentArgs, await received.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
