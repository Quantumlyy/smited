using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Smited.Daemon.Triggering;
using Xunit;

namespace Smited.Daemon.Tests.Triggering;

public class BreakerChallengeServiceTests
{
    [Fact]
    public async Task Generate_returns_unique_tokens()
    {
        var time = new FakeTimeProvider();
        var service = new BreakerChallengeService(time);

        var c1 = await service.GenerateAsync(default);
        var c2 = await service.GenerateAsync(default);

        c1.Token.Should().NotBe(c2.Token);
    }

    [Fact]
    public async Task VerifyAndConsume_succeeds_first_time_fails_second_time()
    {
        var time = new FakeTimeProvider();
        var service = new BreakerChallengeService(time);
        var c = await service.GenerateAsync(default);

        (await service.VerifyAndConsumeAsync(c.Token, default)).Should().BeTrue();
        (await service.VerifyAndConsumeAsync(c.Token, default)).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAndConsume_fails_after_TTL_expires()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        var service = new BreakerChallengeService(time);
        var c = await service.GenerateAsync(default);

        // TTL is 30s; advance just past it.
        time.Advance(TimeSpan.FromSeconds(31));

        (await service.VerifyAndConsumeAsync(c.Token, default)).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAndConsume_with_unknown_token_returns_false()
    {
        var time = new FakeTimeProvider();
        var service = new BreakerChallengeService(time);

        (await service.VerifyAndConsumeAsync("not-a-real-token", default)).Should().BeFalse();
    }
}
