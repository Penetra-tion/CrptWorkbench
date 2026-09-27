using CrptWorkbench.Application;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Web.Components;

public sealed record ResultStatusAppearance(string CssClass, string Icon)
{
    private static readonly ResultStatusAppearance Success = new("tone-success", "✓");
    private static readonly ResultStatusAppearance Error = new("tone-error", "!");
    private static readonly ResultStatusAppearance Waiting = new("tone-warning", "◷");
    private static readonly ResultStatusAppearance Info = new("tone-info", "i");
    private static readonly ResultStatusAppearance Neutral = new("tone-neutral", "−");

    public static ResultStatusAppearance ForCode(CodeInfoItem item) => !CodeInfoResults.IsFound(item)
        ? Error
        : item.CisInfo?.Status switch
        {
            "INTRODUCED" => Success,
            "EMITTED" or "APPLIED" => Info,
            "APPLIED_NOT_PAID" => Waiting,
            _ => Neutral
        };

    public static ResultStatusAppearance ForDocument(DocumentStatusItem item) =>
        DocumentStatusResults.ErrorTexts(item).Count > 0 ? Error : item.Status switch
        {
            "CHECKED_OK" => Success,
            "CHECKED_NOT_OK" or "PARSE_ERROR" or "PROCESSING_ERROR" => Error,
            "IN_PROGRESS" or "WAIT_FOR_CONTINUATION" or "WAIT_ACCEPTANCE" or "WAIT_PARTICIPANT_REGISTRATION" => Waiting,
            "ACCEPTED" => Success,
            _ => Neutral
        };
}
