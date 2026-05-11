using System.Security.Cryptography;

namespace Smited.Daemon.Triggering;

/// <summary>
/// In-memory single-use challenge store with a 30s TTL. Challenges are
/// expected to be consumed within seconds of generation in normal use,
/// so the steady-state size of the dictionary is 1; the worst case (a
/// UI bug spamming Generate) is bounded by the TTL.
/// </summary>
internal sealed class BreakerChallengeService : IBreakerChallengeService
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, DateTimeOffset> _challenges = new();
    private readonly object _lock = new();

    public BreakerChallengeService(TimeProvider time) => _time = time;

    public Task<BreakerChallenge> GenerateAsync(CancellationToken ct)
    {
        var token = GenerateToken();
        var expiresAt = _time.GetUtcNow() + _ttl;

        lock (_lock)
        {
            // Clean up expired challenges opportunistically on every
            // Generate call. Keeps the dictionary bounded without a
            // background sweeper.
            CleanExpired();
            _challenges[token] = expiresAt;
        }

        return Task.FromResult(new BreakerChallenge(token, expiresAt));
    }

    public Task<bool> VerifyAndConsumeAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(false);
        }

        lock (_lock)
        {
            CleanExpired();
            if (!_challenges.TryGetValue(token, out var expiresAt))
            {
                return Task.FromResult(false);
            }
            if (_time.GetUtcNow() >= expiresAt)
            {
                _challenges.Remove(token);
                return Task.FromResult(false);
            }
            // Atomic consume: remove the token before returning true so
            // a second VerifyAndConsume with the same token returns
            // false even on a tight race.
            _challenges.Remove(token);
            return Task.FromResult(true);
        }
    }

    private void CleanExpired()
    {
        var now = _time.GetUtcNow();
        var expired = _challenges
            .Where(kvp => now >= kvp.Value)
            .Select(kvp => kvp.Key)
            .ToArray();
        foreach (var token in expired)
        {
            _challenges.Remove(token);
        }
    }

    private static string GenerateToken()
    {
        // 32 bytes of cryptographically-random data, base64url-encoded.
        // ~43 chars, URL-safe so the token survives a query-string round
        // trip without re-encoding.
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
