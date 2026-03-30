using AllBasicTests;

await RoundTripTests.RunAsync();
await SearchTests.RunAsync();
await BatchTests.RunAsync();
await BoundaryTests.RunAsync();
await CrudTests.RunAsync();
await ConcurrencyTests.RunAsync();
await StorageTests.RunAsync();
await WalTests.RunAsync();

TestHelper.PrintSummary();

return TestHelper.Failed == 0 ? 0 : 1;
