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
              [--duration <ms>] \
              [--intensity <0..100>]

          # LAN mode
          dotnet run --project scripts/pishock-smoke -- \
              --mode lan \
              --ip <device-ip> \
              [--port <port>] \
              [--op vibrate|beep|shock] \
              [--duration <ms>] \
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

        var parsed = ParseArgs(args);
        if (parsed is null)
        {
            return 2;
        }
        var a = parsed.Value;

        if (!System.Enum.TryParse<PishockOp>(a.OpName, ignoreCase: true, out var op))
        {
            Console.Error.WriteLine($"Unknown op '{a.OpName}'. Valid: vibrate, beep, shock.");
            return 2;
        }

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
            // line are user-provided and trusted in this context.
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
            $"Sending {op} for {a.DurationMs}ms at {a.Intensity}% via {a.Mode}...");
        var result = await client.SendOpAsync(op, a.DurationMs, a.Intensity, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"Accepted:    {result.Accepted}");
        Console.WriteLine($"Raw body:    {result.RawResponse ?? "(none)"}");
        if (!result.Accepted)
        {
            Console.WriteLine($"Error:       {result.ErrorMessage ?? "(none)"}");
        }
        return result.Accepted ? 0 : 1;
    }

    private readonly record struct ParsedArgs(
        PishockTransportMode Mode,
        string? Username,
        string? ApiKey,
        string? ShareCode,
        string? DeviceIp,
        int? DevicePort,
        string OpName,
        int DurationMs,
        int Intensity);

    private static ParsedArgs? ParseArgs(string[] args)
    {
        var modeStr = ReadOpt(args, "--mode") ?? "";
        if (!System.Enum.TryParse<PishockTransportMode>(modeStr, ignoreCase: true, out var mode))
        {
            Console.Error.WriteLine($"--mode must be 'cloud' or 'lan' (got '{modeStr}').");
            Console.Error.WriteLine(Usage);
            return null;
        }

        return new ParsedArgs(
            Mode: mode,
            Username: ReadOpt(args, "--username"),
            ApiKey: ReadOpt(args, "--apikey"),
            ShareCode: ReadOpt(args, "--sharecode"),
            DeviceIp: ReadOpt(args, "--ip"),
            DevicePort: ReadIntOpt(args, "--port"),
            OpName: ReadOpt(args, "--op") ?? "vibrate",
            DurationMs: ReadIntOpt(args, "--duration") ?? 200,
            Intensity: ReadIntOpt(args, "--intensity") ?? 20);
    }

    private static string? ReadOpt(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        return null;
    }

    private static int? ReadIntOpt(string[] args, string name)
    {
        var raw = ReadOpt(args, name);
        return raw is not null && int.TryParse(raw, out var v) ? v : null;
    }
}
