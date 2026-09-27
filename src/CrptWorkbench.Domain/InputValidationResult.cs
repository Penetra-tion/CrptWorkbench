namespace CrptWorkbench.Domain;

public sealed record InputIssue(string Field, string Message);

public sealed record InputValidationResult(IReadOnlyList<InputIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
    public string? ErrorFor(string field) => Issues.FirstOrDefault(x => x.Field == field)?.Message;
    public void ThrowIfInvalid()
    {
        if (!IsValid) throw new InputValidationException(Issues);
    }
}

public sealed class InputValidationException(IReadOnlyList<InputIssue> issues)
    : ArgumentException(string.Join(" ", issues.Select(x => x.Message)))
{
    public IReadOnlyList<InputIssue> Issues { get; } = issues;
}
