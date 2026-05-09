using AllBasicTests;

await RoundTripTests.RunAsync();
await SearchTests.RunAsync();
await BatchTests.RunAsync();
await BoundaryTests.RunAsync();
await CrudTests.RunAsync();
await ConcurrencyTests.RunAsync();
await StorageTests.RunAsync();
await MemoryModeTests.RunAsync();
await MemoryModeValidationTests.RunAsync();
await LargeFieldMemoryModeTests.RunAsync();
await SimilarityTests.RunAsync();
await InMemoryEntityStoreTests.RunAsync();
await MigrationTests.RunAsync();
await QuiverEntityAttributeTests.RunAsync();
await V4FileFormatTests.RunAsync();
await MmapVectorStoreTests.RunAsync();
await TombstoneTests.RunAsync();
await SnapshotSelfHealTests.RunAsync();
await HalfVectorTests.RunAsync();

TestHelper.PrintSummary();

return TestHelper.Failed == 0 ? 0 : 1;
