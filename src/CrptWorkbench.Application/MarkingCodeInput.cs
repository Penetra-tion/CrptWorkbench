namespace CrptWorkbench.Application;

public static class MarkingCodeInput
{
    private const char GroupSeparator = '\u001D';

    public static string TrimAfterGroupSeparator(string value)
    {
        var separator = value.IndexOf(GroupSeparator);
        return separator < 0 ? value : value[..separator];
    }
}
