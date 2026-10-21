// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Omen.Core.Rules;
using Omen.GUI.Models;

namespace Omen.GUI.Services;

/// <summary>
/// The GUI's one door into RuleCompiler for everything the rule-file editor needs: validation
/// and IntelliSense. Both go through RuleCompiler.BuildCompilationAsync, so both see exactly the
/// same references/parse options/compilation options a real build would - this class only
/// translates Roslyn's own types (Diagnostic, ISymbol) into the small display-ready models the
/// views bind to; it never builds its own MetadataReferences or CSharpCompilationOptions.
/// </summary>
public sealed class RuleEditorService
{
    // One RuleCompiler for the process: building its reference list walks every trusted platform
    // assembly, which is worth paying once, not per open document.
    private readonly RuleCompiler _compiler = new();

    public async Task<IReadOnlyList<RuleDiagnostic>> ValidateAsync(
        string? projectRoot, string filePath, string text, CancellationToken ct = default)
    {
        var diagnostics = await _compiler.ValidateFileAsync(projectRoot, filePath, text, ct);

        return diagnostics
            .Where(d => d.Severity != DiagnosticSeverity.Hidden)
            .Select(ToRuleDiagnostic)
            .OrderBy(d => d.Line)
            .ThenBy(d => d.Column)
            .ToList();
    }

    private static RuleDiagnostic ToRuleDiagnostic(Diagnostic d)
    {
        var span = d.Location.GetLineSpan().StartLinePosition;
        var severity = d.Severity switch
        {
            DiagnosticSeverity.Error => RuleDiagnosticSeverity.Error,
            DiagnosticSeverity.Warning => RuleDiagnosticSeverity.Warning,
            _ => RuleDiagnosticSeverity.Info
        };

        return new RuleDiagnostic(
            severity,
            $"{d.Id}: {d.GetMessage()}",
            span.Line + 1,
            span.Character + 1,
            d.Location.SourceSpan.Start,
            Math.Max(d.Location.SourceSpan.Length, 1));
    }

    // ---- IntelliSense -----------------------------------------------------------------------
    //
    // Built directly on SemanticModel against the same Compilation ValidateAsync uses (see
    // RuleCompiler.BuildCompilationAsync) - no Workspaces/MEF completion-service host, which
    // would be a second, separately-configured analysis surface. LookupSymbols/GetTypeInfo are
    // plain public Microsoft.CodeAnalysis.CSharp APIs; Omen.Core already depends on that package.

    public async Task<IReadOnlyList<RuleCompletionItem>> GetCompletionsAsync(
        string? projectRoot, string filePath, string text, int position, CancellationToken ct = default)
    {
        var (compilation, tree) = await _compiler.BuildCompilationAsync(projectRoot, filePath, text, ct);

        return await Task.Run(() =>
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(ct);

            var container = FindMemberAccessContainerType(root, model, position, ct);

            var symbols = container != null
                ? model.LookupSymbols(position, container, includeReducedExtensionMethods: true)
                : model.LookupSymbols(position);

            return symbols
                .Where(s => !s.IsImplicitlyDeclared && !string.IsNullOrEmpty(s.Name))
                .GroupBy(s => s.Name)
                .Select(g => g.First())
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new RuleCompletionItem(s.Name, s.Kind.ToString(), s.ToDisplayString()))
                .ToList() as IReadOnlyList<RuleCompletionItem>;
        }, ct);
    }

    /// <summary>
    /// If <paramref name="position"/> sits right after a '.' that is a member-access dot (e.g.
    /// "runtime.|" or "PluginFlavorKind.|"), returns the type/namespace to look member completions
    /// up on. Returns null for anything else, which tells the caller to fall back to
    /// scope-based completion (locals, parameters, visible types, keywords).
    /// </summary>
    private static INamespaceOrTypeSymbol? FindMemberAccessContainerType(
        SyntaxNode root, SemanticModel model, int position, CancellationToken ct)
    {
        if (position <= 0 || position > root.FullSpan.End)
            return null;

        var token = root.FindToken(position - 1);
        if (!token.IsKind(SyntaxKind.DotToken))
            return null;

        var expression = token.Parent switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
            QualifiedNameSyntax qualifiedName => (SyntaxNode)qualifiedName.Left,
            _ => null
        };
        if (expression is null)
            return null;

        // A static member access (PluginFlavorKind.Static) resolves as a type/namespace via
        // GetSymbolInfo; an instance access (runtime.PrivateDependencies) resolves via
        // GetTypeInfo instead. Try both rather than guessing which the expression is.
        var symbolInfo = model.GetSymbolInfo(expression, ct);
        if (symbolInfo.Symbol is INamespaceOrTypeSymbol staticContainer)
            return staticContainer;

        return model.GetTypeInfo(expression, ct).Type as INamespaceOrTypeSymbol;
    }

    public async Task<RuleQuickInfo?> GetQuickInfoAsync(
        string? projectRoot, string filePath, string text, int position, CancellationToken ct = default)
    {
        var (compilation, tree) = await _compiler.BuildCompilationAsync(projectRoot, filePath, text, ct);

        return await Task.Run(() =>
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(ct);
            var token = root.FindToken(position);
            if (token.Span.Length == 0 || !token.Span.IntersectsWith(new TextSpan(position, 0)))
                return null;

            var node = token.Parent;
            if (node is null)
                return null;

            var symbol = model.GetSymbolInfo(node, ct).Symbol ?? model.GetDeclaredSymbol(node, ct);
            if (symbol is null)
                return null;

            var xml = symbol.GetDocumentationCommentXml(cancellationToken: ct);
            var summary = ExtractSummary(xml);
            return new RuleQuickInfo(symbol.ToDisplayString(), summary);
        }, ct);
    }

    private static string? ExtractSummary(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var summary = doc.Descendants("summary").FirstOrDefault()?.Value;
            return summary is null ? null : string.Join(' ', summary.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
