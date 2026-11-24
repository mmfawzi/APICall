using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ApiScheduler.Core.Sql;

/// <summary>
/// Guards the user-entered query: it must be exactly ONE read-only SELECT statement.
/// Parsed with Microsoft's real T-SQL parser (not regex), so comments/strings cannot hide extra statements.
/// </summary>
public static partial class SqlSafety
{
    public static IReadOnlyList<string> ValidateSelect(string? sql)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(sql)) { errors.Add("Query is empty."); return errors; }

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var parseErrors);
        foreach (var e in parseErrors) errors.Add($"Line {e.Line}, col {e.Column}: {e.Message}");
        if (errors.Count > 0) return errors;

        var script = (TSqlScript)fragment;
        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count != 1) { errors.Add("Only a single SELECT statement is allowed (no GO, no ';' followed by another statement)."); return errors; }

        if (statements[0] is not SelectStatement select)
        {
            errors.Add($"Only SELECT is allowed; found {statements[0].GetType().Name.Replace("Statement", "").ToUpperInvariant()}.");
            return errors;
        }
        if (select.Into != null) errors.Add("SELECT ... INTO is not allowed.");

        var visitor = new ForbiddenVisitor();
        select.Accept(visitor);
        errors.AddRange(visitor.Errors.Distinct());
        return errors;
    }

    private sealed class ForbiddenVisitor : TSqlFragmentVisitor
    {
        public List<string> Errors { get; } = new();
        public override void Visit(OpenRowsetTableReference node) => Errors.Add("OPENROWSET is not allowed.");
        public override void Visit(BulkOpenRowset node) => Errors.Add("OPENROWSET(BULK) is not allowed.");
        public override void Visit(OpenQueryTableReference node) => Errors.Add("OPENQUERY is not allowed.");
        public override void Visit(AdHocTableReference node) => Errors.Add("OPENDATASOURCE is not allowed.");
        public override void Visit(OpenXmlTableReference node) => Errors.Add("OPENXML is not allowed.");
        public override void Visit(InternalOpenRowset node) => Errors.Add("Internal OPENROWSET is not allowed.");
    }

    [GeneratedRegex(@"^[A-Za-z_@#][A-Za-z0-9_@#$]{0,127}$")]
    private static partial Regex IdentifierPart();

    /// <summary>
    /// Validates and brackets a (possibly multi-part) identifier, e.g. "dbo.NG_MC_SURVEY_SMS_LOG"
    /// → "[dbo].[NG_MC_SURVEY_SMS_LOG]". Identifiers can't be SQL parameters, so this is the injection guard.
    /// </summary>
    public static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Identifier is empty.");
        var parts = name.Split('.');
        if (parts.Length > 3) throw new ArgumentException($"Invalid identifier '{name}'.");
        return string.Join('.', parts.Select(p =>
        {
            var raw = p.Trim();
            if (raw.StartsWith('[') && raw.EndsWith(']')) raw = raw[1..^1];
            if (!IdentifierPart().IsMatch(raw)) throw new ArgumentException($"Invalid identifier '{name}'.");
            return "[" + raw + "]";
        }));
    }

    public static bool IsValidIdentifier(string name)
    {
        try { QuoteIdentifier(name); return true; } catch (ArgumentException) { return false; }
    }
}
