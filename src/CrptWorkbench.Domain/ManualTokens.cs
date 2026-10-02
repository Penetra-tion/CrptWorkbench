namespace CrptWorkbench.Domain;

public static class ManualTokens
{
    public static string Key(Guid profileId, ApiSystem system) => system switch
    {
        ApiSystem.TrueApi => $"profile:{profileId}:true-api:manual-token",
        ApiSystem.Suz => $"profile:{profileId}:suz:manual-token",
        _ => throw new ArgumentOutOfRangeException(nameof(system))
    };

    public static string Validate(string? token)
    {
        var value = token?.Trim(' ');
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
            value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Введите только токен без Bearer и пробелов внутри.", nameof(token));
        return value;
    }
}
