using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// The chatbot document-copy service over the REAL application service with
/// scriptable CRM and delivery doubles: who may ask (verification), whose
/// documents they get (ownership), which one (selection), and that a retry
/// never sends twice.
/// </summary>
public class CrmDocumentCopyAppServiceTests
{
    private static CrmDocumentCopyRequestDto Req(
        DocumentServiceFixture f, string type = "Contract", string? recordId = null, string? unitId = null, string? channel = null, int? leadId = null) =>
        new(f.SessionId, type, unitId, recordId, channel) { CrmLeadId = leadId };

    private static Task<CrmDocumentCopyResult> Send(DocumentServiceFixture f, CrmDocumentCopyRequestDto r, string? key = "key-1") =>
        f.Service.SendAsync(DocumentServiceFixture.Caller, r, key);

    // ---- the four document types ----

    [Theory]
    [InlineData("Contract")]
    [InlineData("ReservationForm")]
    [InlineData("Reservation Form")]
    [InlineData("unit-layout")]
    [InlineData("RegistrationReceipt")]
    [InlineData("registration receipt")]
    public async Task EachDocumentType_WithOneMatch_IsSentToTheEmailOnRecord(string type)
    {
        var f = new DocumentServiceFixture();
        var documentType = Enum.Parse<CrmDocumentType>(new string(type.Where(char.IsLetter).ToArray()), ignoreCase: true);
        f.Own("REC-1", type: documentType);

        var result = await Send(f, Req(f, type));

        Assert.Equal(CrmDocumentCopyStatus.Sent, result.Status);
        Assert.Equal(documentType.ToString(), result.DocumentType);
        Assert.False(result.Duplicate);
        Assert.Equal("REC-1", result.RecordId);
        var sent = Assert.Single(f.Email.Sent);
        Assert.Equal("customer@example.com", sent.Destination);
        // Only a masked address ever leaves in the response.
        Assert.Equal("c***@e***.com", result.MaskedDestination);
        Assert.DoesNotContain("customer@example.com", System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Contains(f.Audit.Entries, e => e.Action == "CrmDocumentCopySent");
    }

    // ---- verification: identity is proven by the session only ----

    [Fact]
    public async Task NoVerificationSessionId_IsRefused_AndNothingIsLookedUpOrSent()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");

        var result = await Send(f, new CrmDocumentCopyRequestDto(null, "Contract"));

        Assert.Equal(CrmDocumentCopyStatus.InvalidRequest, result.Status);
        Assert.Empty(f.Gateway.ListCalls);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task UnknownSession_FailsVerification()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");

        var result = await Send(f, new CrmDocumentCopyRequestDto(Guid.NewGuid(), "Contract"));

        Assert.Equal(CrmDocumentCopyStatus.VerificationFailed, result.Status);
        Assert.Empty(f.Gateway.ListCalls);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task SessionOwnedByAnotherCaller_FailsVerification_WithTheSameAnswerAsUnknown()
    {
        var f = new DocumentServiceFixture(owner: Guid.NewGuid());
        f.Own("REC-1");

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.VerificationFailed, result.Status);
        Assert.Equal(CrmDocumentCodes.VerificationFailed, result.Code);
        Assert.Empty(f.Gateway.ListCalls);
    }

    [Fact]
    public async Task UnconfirmedSession_FailsVerification()
    {
        var f = new DocumentServiceFixture(confirm: false);
        f.Own("REC-1");

        Assert.Equal(CrmDocumentCopyStatus.VerificationFailed, (await Send(f, Req(f))).Status);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task ExpiredSession_FailsVerification()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(CrmDocumentCopyStatus.VerificationFailed, (await Send(f, Req(f))).Status);
        Assert.Empty(f.Email.Sent);
    }

    [Theory]
    [InlineData(VerificationMethod.ManualAgentConfirmation)]
    [InlineData(VerificationMethod.Other)]
    public async Task AMethodTheChatbotChannelDoesNotAccept_FailsVerification(VerificationMethod method)
    {
        var f = new DocumentServiceFixture(method);
        f.Own("REC-1");

        Assert.Equal(CrmDocumentCopyStatus.VerificationFailed, (await Send(f, Req(f))).Status);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task AuthenticatedDigitalUser_IsAccepted()
    {
        var f = new DocumentServiceFixture(VerificationMethod.AuthenticatedDigitalUser);
        f.Own("REC-1");

        Assert.Equal(CrmDocumentCopyStatus.Sent, (await Send(f, Req(f))).Status);
    }

    // ---- ownership ----

    [Fact]
    public async Task ARecordIdOfAnotherCustomer_IsAnOwnershipMismatch_AndIsNeverFetched()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-MINE");
        f.Gateway.Add(9002, 22222, CrmDocumentType.Contract, "REC-THEIRS");

        var result = await Send(f, Req(f, recordId: "REC-THEIRS"));

        Assert.Equal(CrmDocumentCopyStatus.OwnershipMismatch, result.Status);
        Assert.Equal(CrmDocumentCodes.RecordOwnershipMismatch, result.Code);
        Assert.Empty(f.Gateway.DownloadCalls);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task AGuessedRecordIdThatDoesNotExist_GivesTheSameAnswerAsSomeoneElsesRecord()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-MINE");

        var result = await Send(f, Req(f, recordId: "REC-GUESS"));

        Assert.Equal(CrmDocumentCopyStatus.OwnershipMismatch, result.Status);
        Assert.Empty(f.Gateway.DownloadCalls);
    }

    [Fact]
    public async Task ADifferentUnitThanTheVerifiedOne_IsAnOwnershipMismatch_WithoutAnyCrmCall()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");

        var result = await Send(f, Req(f, unitId: "CRM-UNIT-1002"));

        Assert.Equal(CrmDocumentCopyStatus.OwnershipMismatch, result.Status);
        Assert.Empty(f.Gateway.ListCalls);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task TheVerifiedUnitIdMayBeRestated()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");

        Assert.Equal(CrmDocumentCopyStatus.Sent, (await Send(f, Req(f, unitId: "crm-unit-1001"))).Status);
    }

    [Fact]
    public async Task ContentForADifferentRecordThanSelected_IsNeverSent()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Gateway.Contents["REC-1"] = new CrmDocumentContent("REC-OTHER", [9], "application/pdf", "x.pdf");

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.OwnershipMismatch, result.Status);
        Assert.Empty(f.Email.Sent);
        Assert.Equal(DocumentDeliveryStatus.Failed, Assert.Single(f.Deliveries.All).Status);
    }

    [Fact]
    public async Task ALeadOfAnotherCustomer_IsAnOwnershipMismatch_AndCrmIsNeverAskedForDocuments()
    {
        var f = new DocumentServiceFixture();
        f.Gateway.Add(9002, 22222, CrmDocumentType.Contract, "REC-THEIRS");

        var result = await Send(f, Req(f, leadId: 22222));

        Assert.Equal(CrmDocumentCopyStatus.OwnershipMismatch, result.Status);
        Assert.Empty(f.Gateway.ListCalls);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task ARecordOfTheCustomersOtherUnit_CannotBeFetchedThroughTheVerifiedUnitsLead()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-MINE");
        f.Gateway.Add(DocumentServiceFixture.CustomerId, DocumentServiceFixture.OtherLeadId, CrmDocumentType.Contract, "REC-OTHER-UNIT");

        var result = await Send(f, Req(f, recordId: "REC-OTHER-UNIT"));

        Assert.Equal(CrmDocumentCopyStatus.OwnershipMismatch, result.Status);
        Assert.Empty(f.Gateway.DownloadCalls);
    }

    [Fact]
    public async Task OneOfTheCustomersOtherLeads_IsAllowed_AndCrmIsAskedForThatLead()
    {
        var f = new DocumentServiceFixture();
        f.Gateway.Add(DocumentServiceFixture.CustomerId, DocumentServiceFixture.OtherLeadId, CrmDocumentType.RegistrationReceipt, "RCPT-9");

        var result = await Send(f, Req(f, "RegistrationReceipt", leadId: DocumentServiceFixture.OtherLeadId));

        Assert.Equal(CrmDocumentCopyStatus.Sent, result.Status);
        Assert.Equal((CrmDocumentType.RegistrationReceipt, DocumentServiceFixture.CustomerId, DocumentServiceFixture.OtherLeadId), Assert.Single(f.Gateway.ListCalls));
    }

    [Fact]
    public async Task CrmIsAskedWithTheCustomerAndLeadFromTheVerifiedLookup_NeverFromTheCaller()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");

        await Send(f, Req(f));

        Assert.Equal(DocumentServiceFixture.VerifiedPhone, f.Buyers.LastSearchedPhoneNumber);
        Assert.Equal((CrmDocumentType.Contract, 9001, 12345), Assert.Single(f.Gateway.ListCalls));
        Assert.Contains(f.Audit.Entries, e => e.Action == "CrmDocumentCopySent" && e.AfterValue!.Contains("CrmCustomerId=9001") && e.AfterValue.Contains("CrmLeadId=12345"));
    }

    // ---- which CRM customer ----

    [Fact]
    public async Task AVerifiedContactWithNoPhone_CannotBeResolvedToACrmCustomer()
    {
        var f = new DocumentServiceFixture(contactChannel: "ahmed@example.com");
        f.Own("REC-1");

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.VerificationFailed, result.Status);
        Assert.Equal(CrmDocumentCodes.CrmCustomerNotResolved, result.Code);
        Assert.Equal(0, f.Buyers.CallCount);
        Assert.Empty(f.Gateway.ListCalls);
    }

    [Theory]
    [InlineData(CrmBuyerLookupOutcome.NotFound)]
    [InlineData(CrmBuyerLookupOutcome.AmbiguousCustomerMatch)]
    public async Task ANotFoundOrAmbiguousBuyer_ReleasesNothing(CrmBuyerLookupOutcome outcome)
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Buyers.Returns(new CrmBuyerLookupResult(outcome));

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.VerificationFailed, result.Status);
        Assert.Equal(CrmDocumentCodes.CrmCustomerNotResolved, result.Code);
        Assert.Empty(f.Gateway.ListCalls);
    }

    [Theory]
    [InlineData(CrmBuyerLookupOutcome.Unavailable)]
    [InlineData(CrmBuyerLookupOutcome.Unauthorized)]
    [InlineData(CrmBuyerLookupOutcome.InvalidResponse)]
    public async Task ABuyerLookupThatFails_IsSourceUnavailable(CrmBuyerLookupOutcome outcome)
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Buyers.Returns(new CrmBuyerLookupResult(outcome));

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.DocumentUnavailable, result.Status);
        Assert.Equal(CrmDocumentCodes.DocumentSourceUnavailable, result.Code);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task WhenTheVerifiedUnitIsNotOneOfTheCustomersLeads_TheCustomerIsAskedWhichUnit()
    {
        var f = new DocumentServiceFixture();
        f.Buyers.Returns(CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(9001, "Ahmed", null, DocumentServiceFixture.VerifiedPhone, "customer@example.com"),
                [DocumentServiceFixture.BuyerUnit(111, 1, "2001", "Tiger Sky"), DocumentServiceFixture.BuyerUnit(222, 2, "2002", "Tiger Sky")])
        ]));

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.SelectionRequired, result.Status);
        Assert.Equal("Unit", result.ChoiceKind);
        Assert.Equal(["111", "222"], result.Choices!.Select(c => c.RecordId).ToArray());
        Assert.Empty(f.Gateway.ListCalls);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task ASingleUnitCustomer_NeedsNoUnitSelection_EvenWhenTheNumbersDiffer()
    {
        var f = new DocumentServiceFixture();
        f.Buyers.Returns(CrmBuyerLookupResult.Success(
        [
            new CrmBuyerMatchDto(
                new CrmCustomerDto(9001, "Ahmed", null, DocumentServiceFixture.VerifiedPhone, "customer@example.com"),
                [DocumentServiceFixture.BuyerUnit(555, 5, "9999", "Elsewhere")])
        ]));
        f.Gateway.Add(9001, 555, CrmDocumentType.Contract, "REC-5");

        Assert.Equal(CrmDocumentCopyStatus.Sent, (await Send(f, Req(f))).Status);
    }

    [Fact]
    public async Task TheRestatedUnitAndALeadForADifferentUnit_AreRefused()
    {
        var f = new DocumentServiceFixture();

        var result = await Send(f, Req(f, unitId: "CRM-UNIT-1001", leadId: DocumentServiceFixture.OtherLeadId));

        Assert.Equal(CrmDocumentCopyStatus.OwnershipMismatch, result.Status);
        Assert.Empty(f.Gateway.ListCalls);
    }

    // ---- CRM's selectionRequired ----

    [Fact]
    public async Task WhenCrmSaysSelectionRequired_EvenForOneRecord_NothingIsSent()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Gateway.ForceSelectionRequired = true;

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.SelectionRequired, result.Status);
        Assert.Equal("Document", result.ChoiceKind);
        Assert.Empty(f.Email.Sent);
        Assert.Empty(f.Gateway.DownloadCalls);
    }

    [Fact]
    public async Task TheChoiceMadeAfterSelectionRequired_IsSent()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Own("REC-2", "Addendum 1");

        var result = await Send(f, Req(f, recordId: "REC-2"), key: "key-2");

        Assert.Equal(CrmDocumentCopyStatus.Sent, result.Status);
        Assert.Equal(["REC-2"], f.Gateway.DownloadCalls);
    }

    // ---- type mapping ----

    [Theory]
    [InlineData(CrmDocumentType.ReservationForm, "ReservationForm", 4)]
    [InlineData(CrmDocumentType.Contract, "TigerContract", 5)]
    [InlineData(CrmDocumentType.RegistrationReceipt, "RegistrationReceipt", 6)]
    [InlineData(CrmDocumentType.UnitLayout, "Layout", null)]
    public void PublicTypes_MapToCrmValues(CrmDocumentType type, string crmName, int? attachmentType)
    {
        Assert.Equal(crmName, CrmDocumentTypeMapping.ToCrmName(type));
        Assert.Equal(attachmentType, CrmDocumentTypeMapping.ToCrmAttachmentType(type));
    }

    [Fact]
    public void ThePublicTypeNamesAreUnchanged()
    {
        Assert.Equal(["Contract", "ReservationForm", "UnitLayout", "RegistrationReceipt"], Enum.GetNames<CrmDocumentType>());
        Assert.Equal(1, (byte)CrmDocumentType.Contract);
        Assert.Equal(3, (byte)CrmDocumentType.UnitLayout);
    }

    // ---- every CRM failure is its own answer ----

    [Theory]
    [InlineData(CrmDocumentSourceFailure.RequestRejected, CrmDocumentCodes.CrmRequestRejected, false)]
    [InlineData(CrmDocumentSourceFailure.AuthenticationFailed, CrmDocumentCodes.CrmAuthenticationFailed, false)]
    [InlineData(CrmDocumentSourceFailure.AccessDenied, CrmDocumentCodes.CrmAccessDenied, false)]
    [InlineData(CrmDocumentSourceFailure.InvalidResponse, CrmDocumentCodes.CrmInvalidResponse, false)]
    [InlineData(CrmDocumentSourceFailure.Unavailable, CrmDocumentCodes.DocumentSourceUnavailable, true)]
    public async Task ACrmListingFailure_GetsItsOwnCode_AndNothingIsSent(CrmDocumentSourceFailure failure, string code, bool retryable)
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Gateway.ListFailure = failure;

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.DocumentUnavailable, result.Status);
        Assert.Equal(code, result.Code);
        Assert.Equal(retryable, result.Retryable);
        Assert.Empty(f.Email.Sent);
        Assert.Empty(f.Deliveries.All); // nothing claimed
    }

    [Theory]
    [InlineData(CrmDocumentSourceFailure.AuthenticationFailed, CrmDocumentCodes.CrmAuthenticationFailed)]
    [InlineData(CrmDocumentSourceFailure.ReferenceRejected, CrmDocumentCodes.CrmInvalidResponse)]
    [InlineData(CrmDocumentSourceFailure.Unavailable, CrmDocumentCodes.DocumentSourceUnavailable)]
    public async Task ACrmDownloadFailure_FailsTheClaim_AndAFixedCrmCanBeRetriedWithTheSameKey(CrmDocumentSourceFailure failure, string code)
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Gateway.DownloadFailure = failure;

        var failed = await Send(f, Req(f));
        Assert.Equal(CrmDocumentCopyStatus.DocumentUnavailable, failed.Status);
        Assert.Equal(code, failed.Code);
        Assert.Equal(DocumentDeliveryStatus.Failed, Assert.Single(f.Deliveries.All).Status);
        Assert.Empty(f.Email.Sent);

        f.Gateway.DownloadFailure = null;
        Assert.Equal(CrmDocumentCopyStatus.Sent, (await Send(f, Req(f))).Status);
        Assert.Single(f.Email.Sent);
    }

    // ---- selection ----

    [Fact]
    public async Task SeveralMatchingRecords_ReturnTheChoices_AndSendNothing()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1", "Sale and Purchase Agreement");
        f.Own("REC-2", "Addendum 1");

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.SelectionRequired, result.Status);
        Assert.Equal(["REC-1", "REC-2"], result.Choices!.Select(c => c.RecordId).ToArray());
        Assert.Equal("1204", result.Choices![0].UnitNumber);
        Assert.Empty(f.Email.Sent);
        Assert.Empty(f.Deliveries.All);       // nothing claimed: the follow-up uses its own key
        Assert.Empty(f.Gateway.DownloadCalls); // and nothing fetched
    }

    [Fact]
    public async Task TheChosenRecord_IsTheOneSent()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Own("REC-2", "Addendum 1");

        var result = await Send(f, Req(f, recordId: "REC-2"), key: "key-2");

        Assert.Equal(CrmDocumentCopyStatus.Sent, result.Status);
        Assert.Equal("REC-2", result.RecordId);
        Assert.Equal("REC-2.pdf", Assert.Single(f.Email.Sent).FileName);
    }

    [Fact]
    public async Task NoRecordsForTheCustomer_IsDocumentUnavailable_NotFound()
    {
        var f = new DocumentServiceFixture();

        var result = await Send(f, Req(f, "RegistrationReceipt"));

        Assert.Equal(CrmDocumentCopyStatus.DocumentUnavailable, result.Status);
        Assert.Equal(CrmDocumentCodes.DocumentNotFound, result.Code);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task CrmDocumentSourceDown_IsDocumentUnavailable_WithTheSourceCode()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Gateway.ListFailure = CrmDocumentSourceFailure.Unavailable;

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.DocumentUnavailable, result.Status);
        Assert.Equal(CrmDocumentCodes.DocumentSourceUnavailable, result.Code);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task RecordDeletedBetweenListAndFetch_IsDocumentUnavailable_AndTheClaimIsFailed()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Gateway.Contents["REC-1"] = null; // CRM listed it, then the file is gone (404 on the storage reference)

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.DocumentUnavailable, result.Status);
        Assert.Equal(DocumentDeliveryStatus.Failed, Assert.Single(f.Deliveries.All).Status);
    }

    // ---- delivery ----

    [Theory]
    [InlineData("WhatsApp")]
    [InlineData("Sms")]
    public async Task ChannelsWithoutAnIntegration_SayExactlyThat_AndTouchNothing(string channel)
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");

        var result = await Send(f, Req(f, channel: channel));

        Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, result.Status);
        Assert.Equal(CrmDocumentCodes.DeliveryChannelNotIntegrated, result.Code);
        Assert.False(result.Retryable);
        Assert.Empty(f.Gateway.ListCalls);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task UnrecognisedChannel_IsInvalid()
    {
        var f = new DocumentServiceFixture();
        Assert.Equal(CrmDocumentCopyStatus.InvalidRequest, (await Send(f, Req(f, channel: "Pigeon"))).Status);
    }

    [Fact]
    public async Task NoValidEmailOnRecord_IsDeliveryFailed_WithoutSending()
    {
        var f = new DocumentServiceFixture(customerEmail: "not-an-email");
        f.Own("REC-1");

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, result.Status);
        Assert.Equal(CrmDocumentCodes.DeliveryDestinationUnavailable, result.Code);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task TransientDeliveryFailure_IsRetryable_AndARetryWithTheSameKeySends()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Email.NextOutcome = DocumentDeliveryOutcome.TransientFailure;

        var failed = await Send(f, Req(f));
        Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, failed.Status);
        Assert.Equal(CrmDocumentCodes.DeliveryFailed, failed.Code);
        Assert.True(failed.Retryable);
        Assert.Equal(DocumentDeliveryStatus.Failed, Assert.Single(f.Deliveries.All).Status);

        f.Email.NextOutcome = DocumentDeliveryOutcome.Sent;
        var retried = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.Sent, retried.Status);
        Assert.Single(f.Email.Sent);
        var row = Assert.Single(f.Deliveries.All); // the same row, retaken
        Assert.Equal(2, row.AttemptCount);
    }

    [Fact]
    public async Task PermanentDeliveryFailure_IsNotRetryable()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Email.NextOutcome = DocumentDeliveryOutcome.PermanentFailure;

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, result.Status);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task OversizedDocument_IsNotSent()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Options.MaxAttachmentBytes = 2;

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, result.Status);
        Assert.Equal(CrmDocumentCodes.DocumentTooLarge, result.Code);
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task WhenSwitchedOff_NothingHappens()
    {
        var f = new DocumentServiceFixture();
        f.Options.Enabled = false;
        f.Own("REC-1");

        var result = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.Disabled, result.Status);
        Assert.Empty(f.Gateway.ListCalls);
    }

    // ---- request shape ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has spaces")]
    public async Task MissingOrMalformedIdempotencyKey_IsInvalid(string? key)
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");

        Assert.Equal(CrmDocumentCopyStatus.InvalidRequest, (await Send(f, Req(f), key)).Status);
        Assert.Empty(f.Email.Sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Invoice")]
    public async Task UnknownDocumentType_IsInvalid(string? type)
    {
        var f = new DocumentServiceFixture();
        Assert.Equal(CrmDocumentCopyStatus.InvalidRequest, (await Send(f, new CrmDocumentCopyRequestDto(f.SessionId, type))).Status);
    }

    // ---- duplicate-send prevention ----

    [Fact]
    public async Task GenesysRetryWithTheSameKey_SendsExactlyOnce()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");

        var first = await Send(f, Req(f));
        var retry = await Send(f, Req(f));
        var retryAgain = await Send(f, Req(f));

        Assert.False(first.Duplicate);
        Assert.Equal(CrmDocumentCopyStatus.Sent, retry.Status);
        Assert.True(retry.Duplicate);
        Assert.True(retryAgain.Duplicate);
        Assert.Equal(first.DeliveryRequestId, retry.DeliveryRequestId);
        Assert.Single(f.Email.Sent);
        Assert.Single(f.Audit.Entries, e => e.Action == "CrmDocumentCopySent");
    }

    [Fact]
    public async Task ARetryWhileTheFirstIsStillSending_IsQueued_AndSendsNothingNew()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        CrmDocumentCopyResult? concurrent = null;
        f.Email.DuringSend = async () =>
        {
            f.Email.DuringSend = null;
            concurrent = await Send(f, Req(f));
        };

        var first = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.Queued, concurrent!.Status);
        Assert.Equal(CrmDocumentCodes.RequestInProgress, concurrent.Code);
        Assert.Equal(CrmDocumentCopyStatus.Sent, first.Status);
        Assert.Single(f.Email.Sent);
    }

    [Fact]
    public async Task TwoRequestsThatRaceToClaimTheKey_OnlyTheWinnerSends()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");

        // Between this request's check and its insert, a concurrent identical
        // request (another worker/connection) claims the key AND finishes the
        // send — so this request's insert hits the unique index.
        f.UnitOfWork.BeforeSave = () =>
        {
            var winner = new CrmDocumentDeliveryRequest(
                DocumentServiceFixture.Caller, "key-1", Fingerprint(f), f.SessionId, CrmDocumentType.Contract, "REC-1",
                DocumentDeliveryChannel.Email, "c***@e***.com", f.Clock.GetUtcNow().UtcDateTime);
            winner.MarkSent(f.Clock.GetUtcNow().UtcDateTime);
            f.Deliveries.All.Add(winner);
            typeof(CrmDocumentDeliveryRequest).GetProperty(nameof(CrmDocumentDeliveryRequest.CrmDocumentDeliveryRequestId))!
                .SetValue(winner, 99L);
            return Task.CompletedTask;
        };

        var loser = await Send(f, Req(f));

        Assert.Equal(CrmDocumentCopyStatus.Sent, loser.Status);
        Assert.True(loser.Duplicate);
        Assert.Equal(99, loser.DeliveryRequestId);
        Assert.Empty(f.Email.Sent); // the loser sent nothing; the winner's send is the one
    }

    private static string Fingerprint(DocumentServiceFixture f) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{f.SessionId:N}|{(byte)CrmDocumentType.Contract}|REC-1|{(byte)DocumentDeliveryChannel.Email}")));

    [Fact]
    public async Task ANewKeyForTheSameDocumentWithinTheWindow_DoesNotSendAgain()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        await Send(f, Req(f), key: "key-a");
        f.Clock.Advance(TimeSpan.FromMinutes(5));

        var again = await Send(f, Req(f), key: "key-b");

        Assert.Equal(CrmDocumentCopyStatus.Sent, again.Status);
        Assert.True(again.Duplicate);
        Assert.Single(f.Email.Sent);
    }

    [Fact]
    public async Task ANewKeyAfterTheWindow_SendsAgain()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        await Send(f, Req(f), key: "key-a");
        f.Options.DuplicateSuppressionMinutes = 15;
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        f.Clock.Advance(TimeSpan.FromMinutes(0));
        // The session is still valid for 30 minutes.

        var again = await Send(f, Req(f), key: "key-b");

        Assert.False(again.Duplicate);
        Assert.Equal(2, f.Email.Sent.Count);
    }

    [Fact]
    public async Task TheSameKeyForADifferentRequest_IsRefused()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        f.Own("RES-1", "Reservation Form", CrmDocumentType.ReservationForm);
        await Send(f, Req(f), key: "shared-key");

        var other = await Send(f, Req(f, "ReservationForm"), key: "shared-key");

        Assert.Equal(CrmDocumentCopyStatus.IdempotencyConflict, other.Status);
        Assert.Equal(CrmDocumentCodes.IdempotencyKeyReused, other.Code);
        Assert.Single(f.Email.Sent);
    }

    [Fact]
    public async Task AnAbandonedInProgressClaim_CanBeRetakenAfterTheStaleWindow()
    {
        var f = new DocumentServiceFixture();
        f.Own("REC-1");
        // A claim whose worker died: InProgress, never completed.
        var abandoned = new CrmDocumentDeliveryRequest(
            DocumentServiceFixture.Caller, "key-1", "x", f.SessionId, CrmDocumentType.Contract, "REC-1",
            DocumentDeliveryChannel.Email, "c***@e***.com", f.Clock.GetUtcNow().UtcDateTime);
        await f.Deliveries.AddAsync(abandoned);
        f.Deliveries.Commit();

        Assert.Equal(CrmDocumentCopyStatus.Queued, (await Send(f, Req(f))).Status);

        f.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(CrmDocumentCopyStatus.Sent, (await Send(f, Req(f))).Status);
        Assert.Single(f.Email.Sent);
    }
}
