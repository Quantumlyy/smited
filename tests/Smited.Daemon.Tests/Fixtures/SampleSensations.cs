namespace Smited.Daemon.Tests.Fixtures;

/// <summary>
/// Helpers for materialising small known-good OWO sensation files into a
/// temp library root from end-to-end tests.
/// </summary>
internal static class SampleSensations
{
    public const string CompileErrorMild = """
        {
          "name": "compile_error_mild",
          "backend_kind": "owo_skin",
          "display_name": "Compile Error (Mild)",
          "description": "Mild bump on the left pectoral.",
          "tags": ["build", "error"],
          "default_zone_ids": ["pectoral_l"],
          "default_intensity": 50,
          "estimated_duration": "0.4s",
          "definition": {
            "microsensations": [
              {
                "parameters": {
                  "frequency": { "number": 50 },
                  "intensity": { "number": 60 },
                  "duration": { "duration": "0.3s" },
                  "ramp_up": { "duration": "0.05s" },
                  "ramp_down": { "duration": "0.05s" }
                }
              }
            ]
          }
        }
        """;

    public const string DeploySuccess = """
        {
          "name": "deploy_success",
          "backend_kind": "owo_skin",
          "display_name": "Deploy Success",
          "description": "Wide warm wave across the torso.",
          "tags": ["build", "success"],
          "default_zone_ids": ["torso"],
          "default_intensity": 70,
          "estimated_duration": "0.8s",
          "definition": {
            "microsensations": [
              {
                "parameters": {
                  "frequency": { "number": 30 },
                  "intensity": { "number": 70 },
                  "duration": { "duration": "0.6s" },
                  "ramp_up": { "duration": "0.1s" },
                  "ramp_down": { "duration": "0.1s" }
                }
              }
            ]
          }
        }
        """;

    public static void WriteOwo(string libraryRoot, string fileName, string contents)
    {
        var dir = Path.Combine(libraryRoot, "owo_skin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), contents);
    }
}
