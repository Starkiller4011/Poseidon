using Xunit;

// Several classes here (TrackPipelineIntegrationTests, TracksModuleTests, CoreTracksModuleTests) run a real
// TracksModule/ModuleRuntime: a real 1s TimeProvider.System tick timer plus TrackIngress's/TrackSourceConnector's
// background Task.Run pumps and registration loops. xunit's default test-collection parallelism (one collection
// per class, run concurrently) packs many of these onto the thread pool at once; under a constrained CI runner
// that starves them badly enough to blow even a 30s poll deadline (seen on Arch Linux CI, not locally). Running
// this assembly's test classes sequentially removes that contention at its source instead of chasing it with
// ever-larger timeouts.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
