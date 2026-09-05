namespace LimbusModEditor.Domain.Formats;

public enum ModFormatKind
{
    Unknown,
    Bank,
    Rebank,
    Carra,
    Carra2,
    Lunartique,
    Directory
}

public sealed record FormatDescriptor(
    ModFormatKind Kind,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    bool CanRead,
    bool CanWrite);

public sealed record FormatDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Path = null);

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed class ValidationReport
{
    public List<FormatDiagnostic> Diagnostics { get; } = [];
    public bool IsValid => Diagnostics.All(x => x.Severity != DiagnosticSeverity.Error);
}
