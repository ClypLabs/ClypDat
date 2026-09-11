using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
// Sets Avalonia up on its test thread before the first test runs - see
// AvaloniaFirstTestFramework in AvaloniaTestThread.cs.
[assembly: TestFramework("ClypDat.App.Tests.AvaloniaFirstTestFramework", "ClypDat.App.Tests")]
