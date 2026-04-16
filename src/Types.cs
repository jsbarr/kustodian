public record AnalyseRequest(string Query, string ImpactedEntityField, string Environment, string? NamingConvention = null, bool? Provenance = null, bool? Debug = null);

public record DiagnosticMessage(
    string Level,
    string Type,
    string Message,
    string[] AffectedColumns,
    int? Start = null,
    int? End = null);

public record Position(int Abs, int Line, int Column);

public record ProvenanceNode(
    string Column,
    string? Table = null,
    string? Operator = null,
    Position? Position = null,
    ProvenanceNode[]? Sources = null);

public record ColumnWithProvenance(string Name, string Type, ProvenanceNode? Provenance);

public record SyntaxTreeNode(string Kind, string? Token, SyntaxTreeNode[]? Children);

public record AnalysisResult(IReadOnlyList<DiagnosticMessage> Messages, IReadOnlyList<ColumnWithProvenance>? OutputColumns, SyntaxTreeNode? SyntaxTree = null);
