namespace Smited.Daemon.Triggering;

/// <summary>
/// Challenge-response gate for breaker re-arm. The admin UI flow:
/// <list type="number">
///   <item>UI calls <see cref="GenerateAsync"/> and gets a challenge
///   token + expiry timestamp.</item>
///   <item>UI shows a confirmation dialog. User clicks "Yes,
///   re-arm".</item>
///   <item>UI calls <see cref="VerifyAndConsumeAsync"/> with the token.
///   Service validates (exists, not expired, not already consumed) and
///   consumes atomically.</item>
///   <item>If verification succeeds, UI calls
///   <see cref="IBreakerService.Rearm"/>.</item>
/// </list>
/// Single-use semantics close the spam-click loophole: even if a
/// challenge token leaks (accidentally logged, browser dev tools), it
/// can't be replayed because the first successful verification consumes
/// it.
/// </summary>
internal interface IBreakerChallengeService
{
    Task<BreakerChallenge> GenerateAsync(CancellationToken ct);

    /// <summary>
    /// Verify the challenge token and atomically consume it. Returns
    /// true iff the token existed, hadn't expired, and hadn't been
    /// consumed yet.
    /// </summary>
    Task<bool> VerifyAndConsumeAsync(string token, CancellationToken ct);
}

internal sealed record BreakerChallenge(
    string Token,
    DateTimeOffset ExpiresAt);
