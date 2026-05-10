using Xunit;

// End-to-end tests boot a real ASP.NET host with FakeTimeProvider; running
// them in parallel with unit-level mock tests causes timing-dependent
// failures (mock channel readers compete with bootstrapper fan-out tasks
// for the thread pool). The whole assembly is small enough that serial
// execution costs nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
