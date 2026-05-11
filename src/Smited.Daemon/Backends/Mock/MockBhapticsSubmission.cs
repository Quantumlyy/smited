using System.Collections.Immutable;

namespace Smited.Daemon.Backends.Mock;

/// <summary>
/// Record of a single <see cref="IBhapticsSdk.Submit"/> the mock
/// would have made if it were the real SDK. Captured into the mock's
/// <see cref="IMockBhapticsController.RecentSubmissions"/> ring buffer
/// for test assertions.
/// </summary>
/// <param name="DeviceKey">The smited-side device key
/// (<c>"vest" | "sleeve_l" | "sleeve_r" | "feet_l" | "feet_r"</c>).</param>
/// <param name="MotorIntensities">Per-motor intensities 0..100;
/// length matches the device's motor count.
/// <see cref="ImmutableArray{T}"/> not <c>byte[]</c> so a test that
/// captures a submission and a later test that wants to inspect it
/// cannot accidentally see the buffer mutated underneath them; the
/// mock backend always converts via <c>ToImmutableArray</c> before
/// appending.</param>
/// <param name="Duration">The duration the SDK would have played the
/// motor pattern for.</param>
/// <param name="At">The mock-clock timestamp when the Submit was
/// captured.</param>
public sealed record MockBhapticsSubmission(
    string DeviceKey,
    ImmutableArray<byte> MotorIntensities,
    TimeSpan Duration,
    DateTimeOffset At);
