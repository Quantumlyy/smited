namespace Smited.Daemon.Backends.Internal;

/// <summary>
/// A typed parameter value carried by a microsensation. Mirrors the proto
/// <c>ParameterValue</c> oneof (number / bool / string / duration / enum) but
/// stays inside the domain so backends and the trigger coordinator can pattern
/// match without depending on generated message types.
/// </summary>
public abstract record ParameterValue
{
    private ParameterValue() { }

    public sealed record Number(double Value) : ParameterValue;

    public sealed record Bool(bool Value) : ParameterValue;

    public sealed record Text(string Value) : ParameterValue;

    public sealed record Duration(TimeSpan Value) : ParameterValue;

    public sealed record EnumValue(string Value) : ParameterValue;
}
