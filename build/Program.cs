using Cake.Common;
using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Build;
using Cake.Common.Tools.DotNet.MSBuild;
using Cake.Common.Tools.DotNet.Publish;
using Cake.Common.Tools.DotNet.Test;
using Cake.Core;
using Cake.Frosting;

return new CakeHost()
    .UseContext<BuildContext>()
    .Run(args);

public sealed class BuildContext : FrostingContext
{
    public string BuildConfiguration { get; }

    public string SolutionFile => "smited.sln";

    public string DaemonProject => "src/Smited.Daemon/Smited.Daemon.csproj";

    public string ArtifactsDir => "artifacts";

    public BuildContext(ICakeContext ctx) : base(ctx)
    {
        BuildConfiguration = ctx.Argument("configuration", "Release");
    }
}

[TaskName("Clean")]
public sealed class CleanTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext ctx)
    {
        ctx.CleanDirectory(ctx.ArtifactsDir);
        ctx.DotNetClean(ctx.SolutionFile);
    }
}

[TaskName("Restore")]
[IsDependentOn(typeof(CleanTask))]
public sealed class RestoreTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext ctx) =>
        ctx.DotNetRestore(ctx.SolutionFile);
}

[TaskName("Build")]
[IsDependentOn(typeof(RestoreTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext ctx) =>
        ctx.DotNetBuild(ctx.SolutionFile, new DotNetBuildSettings
        {
            Configuration = ctx.BuildConfiguration,
            NoRestore = true,
        });
}

[TaskName("Test")]
[IsDependentOn(typeof(BuildTask))]
public sealed class TestTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext ctx) =>
        ctx.DotNetTest(ctx.SolutionFile, new DotNetTestSettings
        {
            Configuration = ctx.BuildConfiguration,
            NoBuild = true,
        });
}

[TaskName("Publish-Linux-x64")]
[IsDependentOn(typeof(BuildTask))]
public sealed class PublishLinuxTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext ctx) => Publish(ctx, "linux-x64");

    public static void Publish(BuildContext ctx, string rid, DotNetMSBuildSettings? msbuild = null) =>
        ctx.DotNetPublish(ctx.DaemonProject, new DotNetPublishSettings
        {
            Configuration = ctx.BuildConfiguration,
            Runtime = rid,
            SelfContained = true,
            PublishSingleFile = true,
            OutputDirectory = $"{ctx.ArtifactsDir}/{rid}",
            MSBuildSettings = msbuild,
        });
}

[TaskName("Publish-Win-x64")]
[IsDependentOn(typeof(BuildTask))]
public sealed class PublishWinTask : FrostingTask<BuildContext>
{
    // EnableWindowsTargeting=true is already set in
    // src/Smited.Daemon/Smited.Daemon.csproj via the _TargetingWindows
    // gate, but passing it here makes the cross-build intent obvious in
    // the build script — anyone reading the Cake task sees that this
    // task can run from non-Windows hosts (CI Linux, dev Macs).
    public override void Run(BuildContext ctx) =>
        PublishLinuxTask.Publish(
            ctx,
            "win-x64",
            new DotNetMSBuildSettings().WithProperty("EnableWindowsTargeting", "true"));
}

[TaskName("Publish-OSX-arm64")]
[IsDependentOn(typeof(BuildTask))]
public sealed class PublishOsxTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext ctx) => PublishLinuxTask.Publish(ctx, "osx-arm64");
}

[TaskName("Publish-All")]
[IsDependentOn(typeof(PublishLinuxTask))]
[IsDependentOn(typeof(PublishWinTask))]
[IsDependentOn(typeof(PublishOsxTask))]
public sealed class PublishAllTask : FrostingTask<BuildContext> { }

[TaskName("Default")]
[IsDependentOn(typeof(TestTask))]
public sealed class DefaultTask : FrostingTask<BuildContext> { }
