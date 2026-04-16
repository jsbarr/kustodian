using Kusto.Language;
using Kusto.Language.Syntax;

public static class Analyser
{
        // Runs the full analysis pipeline: parse → syntax check → semantic checks.
    // Syntax errors short-circuit the pipeline because impacted entity and naming checks
    // depend on the output schema, which is undefined when the query is malformed.
    public static AnalysisResult Analyse(AnalyseRequest request, Dictionary<string, GlobalState> environments)
    {
        if (string.IsNullOrEmpty(request.Query)) throw new ArgumentException("'query' is required");
        if (string.IsNullOrEmpty(request.ImpactedEntityField)) throw new ArgumentException("'impactedEntityField' is required");
        if (string.IsNullOrEmpty(request.Environment)) throw new ArgumentException("'environment' is required");
        if (!environments.TryGetValue(request.Environment, out var globals))
            throw new ArgumentException($"Unknown environment: '{request.Environment}'");
        var facts = QueryFacts.Build(request.Query, globals);
        var syntaxMessages = facts.RawDiagnostics.Select(d => new DiagnosticMessage(
            Level: d.Severity.ToString() == "Error" ? "ERROR" : "WARN",
            Type: "Syntax",
            Message: d.Message,
            AffectedColumns: [],
            Start: d.Start,
            End: d.Start + d.Length)).ToList();
        if (syntaxMessages.Any(m => m.Level == "ERROR"))
            return new AnalysisResult(syntaxMessages, null);

        var messages = syntaxMessages
            .Concat(ImpactedEntityDiagnostics.Check(facts, request))
            .Concat(NamingConventionDiagnostics.Check(facts, request))
            .ToList();
        var output = request.Provenance == false
            ? facts.Output.Select(c => c with { Provenance = null }).ToList<ColumnWithProvenance>()
            : (IReadOnlyList<ColumnWithProvenance>)facts.Output;
        var syntaxTree = request.Debug == true ? SerializeSyntax(facts.RawSyntax) : null;
        return new AnalysisResult(messages, output, syntaxTree);
    }

    static SyntaxTreeNode SerializeSyntax(SyntaxElement element)
    {
        if (element is SyntaxToken token)
            return new SyntaxTreeNode(Kind: "SyntaxToken", Token: token.Text, Children: null);

        var children = Enumerable.Range(0, element.ChildCount)
            .Select(i => element.GetChild(i))
            .Where(c => c != null)
            .Select(c => SerializeSyntax(c!))
            .ToArray();
        return new SyntaxTreeNode(Kind: element.GetType().Name, Token: null, Children: children);
    }
}
