using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class ShipmentRulesTests
{
    private const string Inn = "7700000000";
    private const string Code = "010460000000000021test";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    private static ShipmentDocument Transfer(string id, int day = 0, bool incoming = false) =>
        new(id, "UNIVERSAL_TRANSFER_DOCUMENT", "CHECKED_OK", Start.AddDays(day),
            incoming ? "7800000000" : Inn, incoming ? Inn : "7800000000", [new(Code)],
            UpdNumber: id, UpdDate: Start.AddDays(day));
    private static ShipmentDocument Cancel(string id, string target, int day = 10) =>
        new(id, "UNIVERSAL_CANCEL_DOCUMENT", "CHECKED_OK", Start.AddDays(day), null, null, [], CancelledDocumentId: target);
    private static ShipmentCheckState Check(params ShipmentDocument[] docs) => ShipmentRules.Evaluate(Code, Inn, docs).State;

    [Fact] public void NoDocumentsIsNotShipped() => Assert.Equal(ShipmentCheckState.NotShipped, Check());
    [Fact] public void OutgoingIsShipped() => Assert.Equal(ShipmentCheckState.Shipped, Check(Transfer("a")));
    [Fact] public void CancelledOutgoingIsNotShipped() => Assert.Equal(ShipmentCheckState.NotShipped, Check(Transfer("a"), Cancel("c", "a")));
    [Fact] public void AnotherOutgoingSurvivesCancellation() => Assert.Equal(ShipmentCheckState.Shipped, Check(Transfer("a"), Transfer("b", 1), Cancel("c", "a")));
    [Fact] public void CancellingBothLeavesNotShipped() => Assert.Equal(ShipmentCheckState.NotShipped, Check(Transfer("a"), Transfer("b", 1), Cancel("c", "a"), Cancel("d", "b")));
    [Fact] public void ReturnLeavesNotShipped() => Assert.Equal(ShipmentCheckState.NotShipped, Check(Transfer("a"), Transfer("b", 1, true)));
    [Fact] public void CancellingReturnRestoresShipment() => Assert.Equal(ShipmentCheckState.Shipped, Check(Transfer("a"), Transfer("b", 1, true), Cancel("c", "b")));
    [Fact] public void NewShipmentAfterReturnIsShipped() => Assert.Equal(ShipmentCheckState.Shipped, Check(Transfer("a"), Transfer("b", 1, true), Transfer("c", 2)));
    [Fact] public void HistoryOrderDoesNotChangeOutcome() => Assert.Equal(ShipmentCheckState.NotShipped, Check(Transfer("b", 1, true), Transfer("a")));
    [Fact] public void DuplicatesDoNotCreateMovements() { var a = Transfer("a"); Assert.Equal(ShipmentCheckState.Shipped, Check(a, a)); }
    [Fact] public void ConflictingDuplicateIsUnknown() => Assert.Equal(ShipmentCheckState.Unknown, Check(Transfer("a"), Transfer("a", 1, true)));
    [Fact] public void EqualTimeDifferentDirectionsAreUnknown() => Assert.Equal(ShipmentCheckState.Unknown, Check(Transfer("a"), Transfer("b", 0, true)));
    [Fact] public void MissingCancellationTargetIsUnknown() => Assert.Equal(ShipmentCheckState.Unknown, Check(Cancel("c", "missing")));
    [Theory] [InlineData("CANCELED")] [InlineData("CANCELLED")] [InlineData("CHECKED_NOT_OK")]
    public void InactiveStatusIsIgnored(string status) => Assert.Equal(ShipmentCheckState.NotShipped, Check(Transfer("a") with { Status = status }));
    [Fact] public void PendingStatusIsUnknown() => Assert.Equal(ShipmentCheckState.Unknown, Check(Transfer("a") with { Status = "IN_PROGRESS" }));
    [Fact] public void ForeignParticipantsAreIgnored() => Assert.Equal(ShipmentCheckState.NotShipped, Check(Transfer("a") with { SellerInn = "7900000000", BuyerInn = "7800000000" }));
    [Fact] public void OsuCannotProveIndividualShipment() => Assert.Equal(ShipmentCheckState.Unknown, Check(Transfer("a") with { Products = [new("04600000000000", "OSU")] }));

    private static ShipmentDocument Correction(string id, ShipmentDocument original, bool before, bool after) =>
        new(id, "UNIVERSAL_CORRECTION_DOCUMENT", "CHECKED_OK", Start.AddDays(1), original.SellerInn, original.BuyerInn,
            (before ? new[] { new ShipmentProduct(Code, "EXEMPLARY", "BEFORE") } : []).Concat(
                after ? new[] { new ShipmentProduct(Code, "EXEMPLARY", "AFTER") } : []).ToArray(),
            UpdNumber: original.UpdNumber, UpdDate: original.UpdDate, UcdNumber: id, UcdDate: Start.AddDays(1));

    [Fact] public void CorrectionRemovalRemovesOriginalMovement() { var a = Transfer("a"); Assert.Equal(ShipmentCheckState.NotShipped, Check(a, Correction("b", a, true, false))); }
    [Fact] public void CancelledCorrectionRestoresOriginal() { var a = Transfer("a"); Assert.Equal(ShipmentCheckState.Shipped, Check(a, Correction("b", a, true, false), Cancel("c", "b"))); }
    [Fact] public void CorrectionAdditionCreatesShipment() { var a = Transfer("a") with { Products = [] }; Assert.Equal(ShipmentCheckState.Shipped, Check(a, Correction("b", a, false, true))); }
    [Fact] public void UnlinkedCorrectionIsUnknown() => Assert.Equal(ShipmentCheckState.Unknown, Check(Correction("b", Transfer("missing"), true, false)));
    [Fact] public void FixReplacesOriginalComposition() { var a = Transfer("a"); var fix = a with { Id = "b", Type = "UNIVERSAL_TRANSFER_DOCUMENT_FIX", EventAt = Start.AddDays(1), FixNumber = "1", FixDate = Start.AddDays(1), Products = [] }; Assert.Equal(ShipmentCheckState.NotShipped, Check(a, fix)); }
    [Fact] public void CancelledFixRestoresOriginal() { var a = Transfer("a"); var fix = a with { Id = "b", Type = "UNIVERSAL_TRANSFER_DOCUMENT_FIX", EventAt = Start.AddDays(1), FixNumber = "1", FixDate = Start.AddDays(1), Products = [] }; Assert.Equal(ShipmentCheckState.Shipped, Check(a, fix, Cancel("c", "b"))); }
    [Fact] public void CancellationOfCancellationReactivatesShipment() => Assert.Equal(ShipmentCheckState.Shipped, Check(Transfer("a"), Cancel("b", "a"), Cancel("c", "b", 11)));
    [Fact] public void CancellationCycleIsUnknown() => Assert.Equal(ShipmentCheckState.Unknown, Check(Transfer("a"), Cancel("b", "c"), Cancel("c", "b")));
    [Fact] public void UnknownParticipantsCannotProveShipment() => Assert.Equal(ShipmentCheckState.Unknown, Check(Transfer("a") with { SellerInn = null }));
    [Fact] public void ForeignPendingDocumentDoesNotAffectProfile() => Assert.Equal(ShipmentCheckState.NotShipped, Check(Transfer("a") with { SellerInn = "7900000000", BuyerInn = "7800000000", Status = "IN_PROGRESS" }));
    [Fact] public void SelfTransferDoesNotShipOutsideProfile() => Assert.Equal(ShipmentCheckState.NotShipped, Check(Transfer("a") with { BuyerInn = Inn }));
    [Fact] public void DocumentErrorsExcludeMovement() => Assert.Equal(ShipmentCheckState.NotShipped, Check(Transfer("a") with { HasErrors = true }));
    [Fact] public void CorrectionBeforeAndAfterPreservesShipment() { var a = Transfer("a"); Assert.Equal(ShipmentCheckState.Shipped, Check(a, Correction("b", a, true, true))); }
    [Fact] public void CorrectionFixReplacesCorrection() { var a = Transfer("a"); var b = Correction("b", a, true, false); var c = b with { Id = "c", Type = "UNIVERSAL_CORRECTION_DOCUMENT_FIX", FixNumber = "1", FixDate = Start.AddDays(2), Products = [new(Code, "EXEMPLARY", "BEFORE"), new(Code, "EXEMPLARY", "AFTER")] }; Assert.Equal(ShipmentCheckState.Shipped, Check(a, b, c)); }
    [Fact] public void CancellationOfCorrectionFixRestoresCorrection() { var a = Transfer("a"); var b = Correction("b", a, true, false); var c = b with { Id = "c", Type = "UNIVERSAL_CORRECTION_DOCUMENT_FIX", FixNumber = "1", FixDate = Start.AddDays(2), Products = [new(Code, "EXEMPLARY", "BEFORE"), new(Code, "EXEMPLARY", "AFTER")] }; Assert.Equal(ShipmentCheckState.NotShipped, Check(a, b, c, Cancel("d", "c"))); }
    [Fact] public void CorrectionWithWrongBeforeIsUnknown() { var a = Transfer("a"); Assert.Equal(ShipmentCheckState.Unknown, Check(a, Correction("b", a, false, true))); }
    [Fact] public void TwoOriginalDocumentsWithSameIdentityAreAmbiguous() { var a = Transfer("a"); Assert.Equal(ShipmentCheckState.Unknown, Check(a, a with { Id = "duplicate-number" }, Correction("b", a, true, false))); }
    [Fact] public void FixAddingCodeAfterReturnUsesFixMovementTime() { var a = Transfer("a") with { Products = [] }; var fix = a with { Id = "fix", Type = "UNIVERSAL_TRANSFER_DOCUMENT_FIX", FixNumber = "1", FixDate = Start.AddDays(2), EventAt = Start.AddDays(2), Products = [new(Code)] }; Assert.Equal(ShipmentCheckState.Shipped, Check(a, Transfer("return", 1, true), fix)); }
    [Fact] public void CorrectionReferencingMissingUpdFixIsUnknown() { var a = Transfer("a"); var c = Correction("c", a, true, false) with { ReferencedUpdFixNumber = "1", ReferencedUpdFixDate = Start.AddDays(1) }; Assert.Equal(ShipmentCheckState.Unknown, Check(a, c)); }
    [Fact] public void CorrectionReferencingCurrentUpdFixApplies() { var a = Transfer("a"); var f = a with { Id = "fix", Type = "UNIVERSAL_TRANSFER_DOCUMENT_FIX", FixNumber = "1", FixDate = Start.AddDays(1), EventAt = Start.AddDays(1) }; var c = Correction("c", a, true, false) with { EventAt = Start.AddDays(2), ReferencedUpdFixNumber = "1", ReferencedUpdFixDate = Start.AddDays(1) }; Assert.Equal(ShipmentCheckState.NotShipped, Check(a, f, c)); }
    [Fact] public void CorrectionReferencingOlderUpdFixIsUnknown() { var a = Transfer("a"); var f = a with { Id = "fix1", Type = "UNIVERSAL_TRANSFER_DOCUMENT_FIX", FixNumber = "1", FixDate = Start.AddDays(1), EventAt = Start.AddDays(1) }; var f2 = f with { Id = "fix2", FixNumber = "2", FixDate = Start.AddDays(2), EventAt = Start.AddDays(2) }; var c = Correction("c", a, true, false) with { EventAt = Start.AddDays(3), ReferencedUpdFixNumber = "1", ReferencedUpdFixDate = Start.AddDays(1) }; Assert.Equal(ShipmentCheckState.Unknown, Check(a, f, f2, c)); }
    [Fact] public void CancellationBeforeTargetIsUnknown() => Assert.Equal(ShipmentCheckState.Unknown, Check(Transfer("a", 2), Cancel("c", "a", 1)));
    [Fact] public void CancellationOfCancellationBeforeTargetIsUnknown() => Assert.Equal(ShipmentCheckState.Unknown, Check(Transfer("a"), Cancel("b", "a", 2), Cancel("c", "b", 1)));
    [Fact] public void CorrectionBeforeOriginalIsUnknown() { var a = Transfer("a", 2); Assert.Equal(ShipmentCheckState.Unknown, Check(a, Correction("c", a, true, false))); }
    [Fact] public void UnsupportedCodeTypeIsUnknown() => Assert.Equal(ShipmentCheckState.Unknown, Check(Transfer("a") with { Products = [new(Code, "NEW_MODE")] }));
}
