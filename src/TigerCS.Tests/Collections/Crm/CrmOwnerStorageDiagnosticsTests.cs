using System.Data.Common;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Crm;

/// <summary>"The CRM owners could not be stored." must say what failed, where, and with which SQL error, without any customer data, credential or database message text.</summary>
public sealed class CrmOwnerStorageDiagnosticsTests
{
    private const string SecretValue = "Jane Secret 0501234567 jane.secret@example.test";
    private const string Password = "P@ssw0rd-do-not-log";

    private sealed class ThrowingStore(Exception onReplace, Exception? onRecord = null) : ICollectionsCrmOwnerStore
    {
        public string? Recorded { get; private set; }
        public Task<int> ReplaceAsync(IReadOnlyList<CrmOwnerRow> rows, CancellationToken cancellationToken, string? note = null) => throw onReplace;
        public Task RecordFailureAsync(string message, CancellationToken cancellationToken) { Recorded = message; return onRecord is null ? Task.CompletedTask : throw onRecord; }
        public Task<CrmOwnerState> GetStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<(int CompanyId, string UnitKey), CrmUnitLink>> GetLinksAsync(IReadOnlyList<(int CompanyId, string UnitKey)> units, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static (CollectionsCrmOwnersRefreshService Service, CapturingLogger<CollectionsCrmOwnersRefreshService> Log) Service(ICollectionsCrmOwnerStore store)
    {
        var gateway = new FakeCrmUnitOwnersGateway();
        gateway.Owners.Add(new CrmUnitOwnerDto(LeadId: 1, LeadStatus: 8, LeadStatusName: "Sold", CustomerType: 1, CustomerId: 5,
            FullNameEnglish: SecretValue, FullNameArabic: null, MobileNumber: "0501234567", Email: "jane.secret@example.test",
            UnitId: 9, UnitNumber: "101", ProjectId: 3, ProjectCode: "140", ProjectName: "Al Ghaf"));
        var log = new CapturingLogger<CollectionsCrmOwnersRefreshService>();
        return (new(new CollectionsCrmOwnersOptions { Enabled = true }, gateway, store, log), log);
    }

    private static SqlStage Stage(int number, string procedure = "usp_Collections_PublishCrmUnitOwners", string message = "x") =>
        new(CrmOwnerStorageErrors.FromSql(SqlExceptionFactory.Create(number, 16, 3, "10.10.10.117", message), "publishing the run", procedure, "TigerCsTicketing", 1234, 10137));

    private sealed record SqlStage(CrmOwnerStorageException Exception);

    [Fact]
    public async Task TheSavedError_NamesTheStage_Procedure_SqlNumberClassAndState_NotJustCouldNotBeStored()
    {
        var store = new ThrowingStore(Stage(50040).Exception);
        var (service, log) = Service(store);
        var result = await service.RefreshAsync(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(result.Message, store.Recorded);                                       // what the page shows is what LastError holds
        Assert.Contains("publishing the run (usp_Collections_PublishCrmUnitOwners)", store.Recorded);
        Assert.Contains("SQL error 50040 (class 16, state 3)", store.Recorded);
        Assert.Contains("staged row count did not match", store.Recorded);
        Assert.Contains("previous CRM data was kept", store.Recorded);
        Assert.True(store.Recorded!.Length <= 1000);
        // The log carries the connection details.
        var line = Assert.Single(log.Entries, e => e.Message.StartsWith("CRM owners could not be stored", StringComparison.Ordinal)).Message;
        foreach (var expected in new[] { "stage=publishing the run", "procedure=usp_Collections_PublishCrmUnitOwners", "sqlNumber=50040", "class=16", "state=3", "exceptionType=SqlException",
                     "server=10.10.10.117", "database=TigerCsTicketing", "clientConnectionId=", "rows=10137", "elapsedMs=1234" })
            Assert.Contains(expected, line);
    }

    [Theory]
    [InlineData(10060, "connection to SQL Server was lost")]
    [InlineData(-1, "connection to SQL Server was lost")]
    [InlineData(-2, "timed out")]
    [InlineData(2628, "too long for its column")]
    [InlineData(515, "required value was missing")]
    [InlineData(208, "object was not found")]
    [InlineData(1205, "deadlock")]
    [InlineData(9999, "see the API log")]
    public void EachSqlNumber_GetsAFixedDataFreeDescription(int number, string expected)
    {
        var message = Stage(number, "usp_Collections_StoreCrmUnitOwners").Exception.Message;
        Assert.Contains(expected, message);
        Assert.Contains($"SQL error {number}", message);
    }

    [Fact]
    public async Task NeitherTheSavedErrorNorTheLog_ContainsCustomerDataCredentialsOrTheDatabasesOwnMessage()
    {
        // SQL Server's truncation message echoes the offending value; the connection details could carry a password if a message were copied.
        var databaseMessage = $"String or binary data would be truncated in table 'dbo.CollectionsCrmUnitOwner', column 'FullName'. Truncated value: '{SecretValue}'. Password={Password}";
        var ex = CrmOwnerStorageErrors.FromSql(SqlExceptionFactory.Create(2628, 16, 1, "10.10.10.117", databaseMessage), "staging chunk 3 of 11", "usp_Collections_StoreCrmUnitOwners", "TigerCsTicketing", 50, 1000);
        var store = new ThrowingStore(ex);
        var (service, log) = Service(store);
        await service.RefreshAsync(CancellationToken.None);
        var everything = store.Recorded + "\n" + string.Join("\n", log.Messages) + "\n" + ex.Message + ex.LogDetail;
        foreach (var forbidden in new[] { "Jane Secret", "0501234567", "jane.secret@example.test", Password, "Truncated value", "CollectionsCrmUnitOwner" })
            Assert.DoesNotContain(forbidden, everything);
        Assert.Contains("staging chunk 3 of 11 (usp_Collections_StoreCrmUnitOwners)", store.Recorded);
        Assert.Contains("SQL error 2628", store.Recorded);
    }

    [Fact]
    public async Task ANonSqlFailure_IsSavedWithItsTypeNameOnly()
    {
        var store = new ThrowingStore(new InvalidOperationException($"boom {SecretValue}"));
        var (service, log) = Service(store);
        var result = await service.RefreshAsync(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("(InvalidOperationException)", store.Recorded);
        Assert.DoesNotContain("Jane Secret", store.Recorded + string.Join("", log.Messages));
    }

    [Fact]
    public async Task IfEvenTheFailureCannotBeRecorded_ItIsLoggedWithItsSqlNumber_NotSwallowedSilently()
    {
        var recordFailure = CrmOwnerStorageErrors.FromSql(SqlExceptionFactory.Create(10060), "recording the failure", "usp_Collections_RecordCrmOwnerFailure", null, 5, 0);
        var store = new ThrowingStore(Stage(10060).Exception, recordFailure);
        var (service, log) = Service(store);
        var result = await service.RefreshAsync(CancellationToken.None);
        Assert.False(result.Succeeded);                                                      // the refresh still reports its failure
        var line = Assert.Single(log.Entries, e => e.Message.StartsWith("The CRM owner failure could not be recorded", StringComparison.Ordinal)).Message;
        Assert.Contains("sqlNumber=10060", line); Assert.Contains("usp_Collections_RecordCrmOwnerFailure", line);
    }

    [Fact]
    public void TheStoreErrorBuilder_CapturesTheRealSqlFields_AndKeepsTheInnerExceptionOutOfTheMessage()
    {
        var sql = SqlExceptionFactory.Create(2627, 14, 2, "db-host", "Violation of PRIMARY KEY ... The duplicate key value is (secret-key)");
        var ex = CrmOwnerStorageErrors.FromSql(sql, "staging chunk 1 of 2", "usp_Collections_StoreCrmUnitOwners", "TigerCsTicketing", 7, 2000);
        Assert.Equal(2627, ex.SqlNumber); Assert.Equal(14, ex.SqlClass); Assert.Equal(2, ex.SqlState);
        Assert.Same(sql, ex.InnerException);                                                 // kept for diagnostics, never copied into the text
        Assert.DoesNotContain("secret-key", ex.Message + ex.LogDetail);
        var other = CrmOwnerStorageErrors.FromOther(new InvalidOperationException("The receivables snapshot connection is not configured."), "opening the connection", null, null, 1, 0);
        Assert.Contains("opening the connection failed (InvalidOperationException)", other.Message);
        Assert.Null(other.SqlNumber);
    }
}
