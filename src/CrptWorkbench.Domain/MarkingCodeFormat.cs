namespace CrptWorkbench.Domain;

public static class MarkingCodeFormat
{
    public static bool HasBracketedApplicationIdentifiers(string code)
    {
        if (code.StartsWith("(01)", StringComparison.Ordinal) || code.StartsWith("(21)", StringComparison.Ordinal))
            return true;

        // AI 21 follows AI 01 and its 14-digit GTIN. Later brackets belong to the serial number.
        return code.Length >= 20 && code.StartsWith("01", StringComparison.Ordinal) &&
            code.AsSpan(2, 14).IndexOfAnyExceptInRange('0', '9') < 0 &&
            code.AsSpan(16).StartsWith("(21)", StringComparison.Ordinal);
    }
}
