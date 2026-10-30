// Several test classes redirect Console.Out (the CLI and the easy engine print to it);
// with classes running in parallel they would capture each other's output.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
