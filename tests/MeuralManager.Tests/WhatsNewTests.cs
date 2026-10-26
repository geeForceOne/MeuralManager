using MeuralManager.Web.Services;

namespace MeuralManager.Tests;

public class WhatsNewTests
{
    [Theory]
    [InlineData("1.14.0", "1.13.0", WhatsNew.Decision.Announce)]
    [InlineData("1.14.0", "1.14.0", WhatsNew.Decision.Hide)]
    [InlineData("1.13.0", "1.14.0", WhatsNew.Decision.Hide)]
    [InlineData("1.14.0", null, WhatsNew.Decision.StartAtRunning)]
    [InlineData("1.14.0", "garbage", WhatsNew.Decision.Announce)]
    [InlineData("dev", "1.13.0", WhatsNew.Decision.Hide)]
    [InlineData("dev-abc1234", null, WhatsNew.Decision.Hide)]
    public void Decide(string running, string? lastSeen, WhatsNew.Decision expected) =>
        Assert.Equal(expected, WhatsNew.Decide(running, lastSeen));
}
