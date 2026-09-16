// The ingest counters are process-global instruments; parallel test classes would
// cross-contaminate each other's measurements (and race for Docker resources).
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
