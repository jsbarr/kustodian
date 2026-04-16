using System.Text.RegularExpressions;

public static class NamingConventionDiagnostics
{
    static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static IEnumerable<DiagnosticMessage> Check(QueryFacts facts, AnalyseRequest request)
    {
        var namingConvention = request.NamingConvention;
        if (string.IsNullOrEmpty(namingConvention)) return [];

        Regex regex;
        try
        {
            regex = new Regex(namingConvention, RegexOptions.None, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return [new DiagnosticMessage(
                Level: "ERROR",
                Type: "ColumnNamingConvention",
                Message: $"Invalid naming convention regex '{namingConvention}': {ex.Message}",
                AffectedColumns: [])];
        }

        var violating = new List<string>();
        // Only check columns that are genuinely new: skip raw table columns (GetTable != null)
        // and renamed pass-throughs (OriginalColumns.Count > 0), since those names aren't
        // under the query author's control.
        foreach (var col in facts.Columns.Where(c => facts.Globals.GetTable(c) == null && c.OriginalColumns.Count == 0))
        {
            try
            {
                if (!regex.IsMatch(col.Name)) violating.Add(col.Name);
            }
            catch (RegexMatchTimeoutException)
            {
                return [new DiagnosticMessage(
                    Level: "ERROR",
                    Type: "ColumnNamingConvention",
                    Message: $"Naming convention regex '{namingConvention}' timed out",
                    AffectedColumns: [])];
            }
        }

        if (violating.Count == 0) return [];

        return [new DiagnosticMessage(
            Level: "WARN",
            Type: "ColumnNamingConvention",
            Message: $"One or more columns do not match naming convention '{namingConvention}': {string.Join(", ", violating)}",
            AffectedColumns: violating.ToArray())];
    }
}
