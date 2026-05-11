using Microsoft.Extensions.Logging.Abstractions;
using Smited.Daemon.Pishock;
using Smited.Daemon.Pishock.Internal;

namespace Smited.PishockSmoke;

/// <summary>
/// Pre-flight scratch app for verifying PiShock credentials and
/// connectivity before plugging the device into the smited daemon.
/// Fires one op against either the cloud API or a LAN device, prints
/// the response, exits.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage:
          # Cloud mode
          dotnet run --project scripts/pishock-smoke -- \
              --mode cloud \
              --username <username> \
              --apikey <api-key> \
              --sharecode <share-code> \
              [--op vibrate|beep|shock] \
              [--duration <ms 1..15000>] \
              [--intensity <0..100>]

          # LAN mode
          dotnet run --project scripts/pishock-smoke -- \
              --mode lan \
              --ip <device-ip> \
              [--port <1..65535>] \
              [--op vibrate|beep|shock] \
              [--duration <ms 1..15000>] \
              [--intensity <0..100>]

        Defaults: --op vibrate --duration 200 --intensity 20
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var parseResult = ArgParser.Parse(args);
        if (parseResult is ArgParseResult.Failure failure)
        {
            Console.Error.WriteLine(failure.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var a = ((ArgParseResult.Success)parseResult).Args;

        var options = new PishockBackendOptions
        {
            Mode = a.Mode,
            Username = a.Username,
            ApiKey = a.ApiKey,
            ShareCode = a.ShareCode,
            DeviceIp = a.DeviceIp,
            DevicePort = a.DevicePort,
            // Pre-flight is a one-shot fire; bypass the daemon's per-op
            // safety caps. The duration/intensity passed on the command
            // line are validated by ArgParser before reaching here, so
            // we can trust them at this point.
            AllowedOps = new() { PishockOp.Vibrate, PishockOp.Beep, PishockOp.Shock },
            MaxIntensityShock = 100,
            MaxIntensityVibrate = 100,
            MaxDurationMs = 60_000,
            RequestTimeoutMs = 15_000,
        };

        IPishockClient client;
        if (a.Mode == PishockTransportMode.Cloud)
        {
            if (string.IsNullOrEmpty(a.Username)
                || string.IsNullOrEmpty(a.ApiKey)
                || string.IsNullOrEmpty(a.ShareCode))
            {
                Console.Error.WriteLine(
                    "Cloud mode requires --username, --apikey, and --sharecode.");
                return 2;
            }
            client = new CloudPishockClient(
                new HttpClient(), options, "pishock-smoke",
                NullLogger<CloudPishockClient>.Instance);
        }
        else
        {
            if (string.IsNullOrEmpty(a.DeviceIp))
            {
                Console.Error.WriteLine("LAN mode requires --ip.");
                return 2;
            }
            client = new LanPishockClient(
                new HttpClient(), options, "pishock-smoke",
                NullLogger<LanPishockClient>.Instance);
        }

        Console.WriteLine(
            $"Sending {a.Op} for {a.DurationMs}ms at {a.Intensity}% via {a.Mode}...");
        var result = await client.SendOpAsync(a.Op, a.DurationMs, a.Intensity, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"Accepted:    {result.Accepted}");
        Console.WriteLine($"Raw body:    {result.RawResponse ?? "(none)"}");
        if (!result.Accepted)
        {
            Console.WriteLine($"Error:       {result.ErrorMessage ?? "(none)"}");
        }
        return result.Accepted ? 0 : 1;
    }
}

/// <summary>
/// Validated command-line arguments for the smoke tool. ArgParser.Parse
/// guarantees every field has a sensible value before this struct is
/// constructed.
/// </summary>
internal readonly record struct ParsedArgs(
    PishockTransportMode Mode,
    string? Username,
    string? ApiKey,
    string? ShareCode,
    string? DeviceIp,
    int? DevicePort,
    PishockOp Op,
    int DurationMs,
    int Intensity);

/// <summary>
/// Discriminated result of <see cref="ArgParser.Parse"/>. Success
/// carries validated args; Failure carries the user-facing error
/// message to print.
/// </summary>
internal abstract record ArgParseResult
{
    public sealed record Success(ParsedArgs Args) : ArgParseResult;
    public sealed record Failure(string Message) : ArgParseResult;
}

/// <summary>
/// Command-line argument parsing for the smoke tool. Strict on
/// purpose — bad input gets a clear error and exit 2, not a silent
/// fallback to a default that fires something the user didn't ask
/// for on real hardware.
/// </summary>
internal static class ArgParser
{
    private static readonly HashSet<string> KnownFlags = new(StringComparer.Ordinal)
    {
        "--mode", "--username", "--apikey", "--sharecode",
        "--ip", "--port", "--op", "--duration", "--intensity",
    };

    public static ArgParseResult Parse(string[] args)
    {
        // First pass: every arg must be a recognized flag followed by
        // a value. Without this, a typo like --intensiy 0 falls
        // through to the default --intensity 20 and fires real
        // hardware — exactly the silent failure the smoke tool is
        // supposed to catch before it reaches the device.
        var unknownCheck = ValidateRecognizedFlags(args);
        if (unknownCheck is not null)
        {
            return unknownCheck;
        }

        var modeStr = ReadOpt(args, "--mode");
        if (string.IsNullOrEmpty(modeStr))
        {
            return new ArgParseResult.Failure("--mode is required (cloud or lan).");
        }
        if (!System.Enum.TryParse<PishockTransportMode>(modeStr, ignoreCase: true, out var mode))
        {
            return new ArgParseResult.Failure(
                $"--mode must be 'cloud' or 'lan', got '{modeStr}'.");
        }

        var opStr = ReadOpt(args, "--op") ?? "vibrate";
        if (!System.Enum.TryParse<PishockOp>(opStr, ignoreCase: true, out var op))
        {
            return new ArgParseResult.Failure(
                $"--op must be 'vibrate', 'beep', or 'shock', got '{opStr}'.");
        }

        // Duration in ms. Reject obvious nonsense — non-int, <=0, or
        // beyond the cloud API's 15s ceiling. The daemon's per-op
        // duration cap is higher (60s for the smoke session) so the
        // tighter bound here is about catching typos like "abc" or
        // "1500ms" that the daemon would silently coerce to a
        // default.
        var durationResult = ParseInt(args, "--duration", min: 1, max: 15_000, defaultValue: 200);
        if (durationResult is ParseIntResult.Failure df) return new ArgParseResult.Failure(df.Message);
        var duration = ((ParseIntResult.Success)durationResult).Value;

        var intensityResult = ParseInt(args, "--intensity", min: 0, max: 100, defaultValue: 20);
        if (intensityResult is ParseIntResult.Failure intf) return new ArgParseResult.Failure(intf.Message);
        var intensity = ((ParseIntResult.Success)intensityResult).Value;

        int? port = null;
        if (ReadOpt(args, "--port") is not null)
        {
            var portResult = ParseInt(args, "--port", min: 1, max: 65_535, defaultValue: 80);
            if (portResult is ParseIntResult.Failure pf) return new ArgParseResult.Failure(pf.Message);
            port = ((ParseIntResult.Success)portResult).Value;
        }

        return new ArgParseResult.Success(new ParsedArgs(
            Mode: mode,
            Username: ReadOpt(args, "--username"),
            ApiKey: ReadOpt(args, "--apikey"),
            ShareCode: ReadOpt(args, "--sharecode"),
            DeviceIp: ReadOpt(args, "--ip"),
            DevicePort: port,
            Op: op,
            DurationMs: duration,
            Intensity: intensity));
    }

    private static ArgParseResult.Failure? ValidateRecognizedFlags(string[] args)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                return new ArgParseResult.Failure(
                    $"Unexpected argument '{arg}'. Every flag must be in --name form; "
                    + "stray positional arguments aren't supported.");
            }
            if (!KnownFlags.Contains(arg))
            {
                return new ArgParseResult.Failure(
                    $"Unknown flag '{arg}'. Run with --help for the list of supported flags.");
            }
            if (!seen.Add(arg))
            {
                return new ArgParseResult.Failure(
                    $"Flag '{arg}' specified more than once. Each flag must appear at most once.");
            }
            // Skip the value that follows. Out-of-bounds is fine — the
            // value-required check inside ParseInt / required-string
            // checks below catch trailing flags without values.
            i++;
        }
        return null;
    }

    private static string? ReadOpt(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        // Also catch "--flag" as the last arg with no value. Without
        // this, the loop above silently treats trailing flags as
        // absent — a typo like `--duration` at the end of the line
        // would fall back to the default rather than erroring.
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == name) return ""; // present but missing value
        }
        return null;
    }

    private static ParseIntResult ParseInt(
        string[] args, string name, int min, int max, int defaultValue)
    {
        var raw = ReadOpt(args, name);
        if (raw is null)
        {
            return new ParseIntResult.Success(defaultValue);
        }
        if (raw.Length == 0)
        {
            return new ParseIntResult.Failure($"{name} requires a value.");
        }
        if (!int.TryParse(raw, out var v))
        {
            return new ParseIntResult.Failure(
                $"{name} must be an integer, got '{raw}'.");
        }
        if (v < min || v > max)
        {
            return new ParseIntResult.Failure(
                $"{name} must be in [{min}, {max}], got {v}.");
        }
        return new ParseIntResult.Success(v);
    }

    private abstract record ParseIntResult
    {
        public sealed record Success(int Value) : ParseIntResult;
        public sealed record Failure(string Message) : ParseIntResult;
    }
}
