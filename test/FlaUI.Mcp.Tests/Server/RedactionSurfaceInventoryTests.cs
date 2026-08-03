using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

/// <summary>SP3 Roslyn syntax sweep (spec §7.2): the guarantee is that every read of a content-bearing
/// UIA property in src/ sits inside a member on one of two CLOSED LISTS — the egress-accessor allowlist
/// below (returns already-redacted values) or the pinned identity readers. This test WALKS the actual
/// source tree with Roslyn and enforces that mechanically, so the guarantee stops depending on developer
/// vigilance. It is expected to FAIL today: the allowlist seed is deliberately incomplete and this sweep
/// exists to surface every site that needs triage (add to the allowlist, or fix the leak).
///
/// Deliberately syntax-only (no semantic model / compilation): it distinguishes patterns by their
/// TEXTUAL shape, not by resolved types. That is a known source of both false positives (e.g. a
/// `RedactionRule.Name` config field, or re-reading an already-redacted wire-DTO field, both syntactically
/// indistinguishable from a raw UIA `.Name` read) and possible false negatives (e.g. `.Value` read off a
/// local variable with no "Patterns"/"Properties"/"LegacyIAccessible" in the same expression chain). One
/// narrow, evidence-backed exemption is carved out below (calls to the closed-list `ElementContent.Name`/
/// `.Value` accessors themselves are not re-flagged as raw reads) — see the comment on
/// <see cref="IsCallToElementContentAccessor"/>.</summary>
[Trait("Category", "SourceSweep")]
public class RedactionSurfaceInventoryTests
{
    // ---- Allowlist — every entry REQUIRES a written reason. This is the whole anti-gaming property
    // of this structure: a Dictionary<string,string> (not a HashSet<string>) means widening the list is
    // a visible, self-documenting statement of WHY the member is safe, never a silent one-word addition.
    // Do not add an entry without a reason that would survive an adversarial reviewer asking "why not?".
    private static readonly Dictionary<string, string> AllowedMembers = new(StringComparer.Ordinal)
    {
        ["ElementContent.Name"] = "egress accessor — returns the already-redacted value",
        ["ElementContent.Value"] = "egress accessor — returns the already-redacted value",
        ["ElementContent.Text"] = "egress accessor — returns the already-redacted value",
        ["ElementContent.Classify"] = "egress accessor — returns the already-redacted value",
        ["ElementContent.SensitivityOf"] = "egress accessor — returns the already-redacted value",
        ["ElementContent.Safe"] = "egress accessor — returns the already-redacted value",
        ["RefRegistry.ResolveDescriptor"] = "identity reader — raw name for ref re-resolution (BC-1)",
        ["RefRegistry.ResolveStrict"] = "identity reader — raw name for ref re-resolution (BC-1)",
        ["RefRegistry.FastPathMatches"] = "identity reader — raw name for ref re-resolution (BC-1)",
        ["SnapshotEngine.Build"] = "egress accessor — returns the already-redacted value",
        ["SnapshotEngine.FormatNode"] = "egress accessor — returns the already-redacted value",
        ["SnapshotDiff.Identity"] = "identity reader — raw name for ref re-resolution (BC-1)",
        ["SnapshotDiff.IdentityKey"] = "identity reader — raw name for ref re-resolution (BC-1)",
        ["SnapshotDiff.Subtree"] = "identity reader — raw name for ref re-resolution (BC-1)",
        ["SnapshotDiff.ShownName"] = "egress accessor — returns the already-redacted value",
        ["WaitCoordinator.Matches"] = "identity reader — raw name for ref re-resolution (BC-1)",
        ["WaitCoordinator.Signature"] = "identity reader — raw name for ref re-resolution (BC-1)",
        ["WatchPayloadBuilder.Build"] = "egress accessor — returns the already-redacted value",
        ["LiveEventSourceReader.MintRef"] = "identity reader — raw name for ref re-resolution (BC-1)",
        ["PerceptionManager.FindAsync"] = "egress accessor — returns the already-redacted value",
        ["PerceptionManager.ResolveSelectorOnSta"] = "identity reader — raw name for ref re-resolution (BC-1)",
        ["PerceptionManager.ReadText"] = "egress accessor — returns the already-redacted value",
        ["PerceptionManager.ReadGridCell"] = "egress accessor — returns the already-redacted value",
        ["PerceptionManager.EvaluateSelectorValueAsync"] = "egress accessor — returns the already-redacted value",
        ["VerifyReader.FromElement"] = "identity reader — raw name for ref re-resolution (BC-1)",
        ["TerminalTabReader.Title"] = "egress accessor — returns the already-redacted value",

        // ---- SP3 reconciliation, Part C: the driver's classification of the sweep's remaining
        // findings. Reasons are verbatim from the driver — do not rephrase. ----
        ["FindQuerySpec.HasNameConstraint"] = "not a UIA read: _q.Name is the CALLER'S SEARCH TERM (FindQuery.Name), never an element property",
        ["FindQuerySpec.MatchesPostFilter"] = "not a UIA read: _q.Name is the caller's search term; the `name` argument compared against it was already redacted by the caller",
        ["RedactionRuleFile.Parse"] = "not a UIA read: r.Name is the RULE's configured name from the operator's own JSON file",
        ["SensitivityClassifier.Classify"] = "not a UIA read: rule.Name is the matched rule's configured name, which becomes Sensitivity.RuleName",
        ["SnapshotEngine.IsInteractiveNode"] = "reads SnapshotNode.Name but ONLY as IsNullOrWhiteSpace emptiness; exposes no content, not even via branching on it",
        ["SnapshotEngine.SupportedPatterns"] = "not a UIA read: c.Name is a hardcoded pattern label from a (string Name, Func<bool>) tuple - Invoke, Value, etc.",
        ["WindowManager.ResolveFocusedWindowAsync"] = "window TITLE via Properties.Name, not element content - the census's canonical 'neither'. NOTE: filed as an open anomaly (.clavity/local-anomalies.md) because a title can carry a document/customer name and no rule can target it today",
        ["CheckRedactionRulesCommand.PrintResults"] = "not a UIA read: r.Name is the rule's configured name from the operator's own file",
        ["FindTools.DesktopFind"] = "reads FindMatch.Name, ALREADY redacted at construction in PerceptionManager.FindAsync - a DTO re-read, not a live element read",
        ["SnapshotTools.DesktopSnapshotDiff"] = "reads DiffDescriptor.Name, ALREADY redacted at construction by SnapshotDiff.ShownName - a DTO re-read, not a live element read",
    };

    // Spec §7.2: never flag these — read throughout, carry no content.
    private static readonly HashSet<string> StructuralProperties = new(StringComparer.Ordinal)
    {
        "ControlType", "BoundingRectangle", "IsEnabled", "IsOffscreen", "ProcessId", "RuntimeId",
        "AutomationId", "IsPassword", "HelpText", "IsSelected", "HasKeyboardFocus",
    };

    // The three sanctioned optional-classifier sites (spec §7.2 / Task 4b history).
    private const string SanctionedType1 = "PerceptionManager"; // constructor
    private const string SanctionedType2 = "WatchPump";          // constructor
    private const string SanctionedType3 = "SnapshotEngine";     // .Build method

    private static readonly Lazy<SweepResult> Result = new(RunSweep);

    [Fact]
    public void SourceTree_Has_The_Expected_Shape()
    {
        var r = Result.Value;
        Assert.True(r.SrcFileCount > 20,
            $"Expected > 20 .cs files under '{Path.Combine(r.RepoRoot, "src")}', found {r.SrcFileCount}. " +
            "A sweep that silently matches zero/few files is the false-GREEN this test exists to prevent.");
    }

    [Fact]
    public void Parsed_Source_Has_No_Toolchain_Skew()
    {
        var r = Result.Value;
        Assert.True(r.ParseDiagnostics.Count == 0,
            "TOOLCHAIN SKEW (not a redaction failure) — the following files did not parse cleanly with the " +
            "pinned Microsoft.CodeAnalysis.CSharp version and were SKIPPED by the sweep below:\n" +
            string.Join("\n", r.ParseDiagnostics));
    }

    [Fact]
    public void SourceTree_EnforcesRedactionSurfaceInvariants()
    {
        var r = Result.Value;
        var sections = new List<string>();
        int total = 0;

        void Add(string title, List<string> violations)
        {
            if (violations.Count == 0) return;
            total += violations.Count;
            sections.Add($"{title} ({violations.Count}):\n" + string.Join("\n", violations));
        }

        Add("RULE 1 — content read outside a listed egress/identity member", r.Rule1);
        Add("RULE 2 — `dynamic` usage in src/", r.Rule2);
        Add("RULE 3 — caller-supplied property-id parameter forwarded to a generic accessor", r.Rule3);
        Add("RULE 4 — generic accessor's property-id argument is not a static field reference", r.Rule4);
        Add("RULE 4b — classifier-argument guarantee (omitted / re-declared default-null / literal null)", r.Rule4b);

        Assert.True(total == 0,
            $"Redaction surface sweep found {total} violation(s) across {sections.Count} rule(s):\n\n" +
            string.Join("\n\n", sections));
    }

    // ============================== sweep engine ==============================

    private sealed record SweepResult(
        string RepoRoot, int SrcFileCount, List<string> ParseDiagnostics,
        List<string> Rule1, List<string> Rule2, List<string> Rule3, List<string> Rule4, List<string> Rule4b);

    private static SweepResult RunSweep()
    {
        string repoRoot = FindRepoRoot();
        string srcRoot = Path.Combine(repoRoot, "src");

        var files = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildArtifactPath(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var parseDiagnostics = new List<string>();
        var rule1 = new List<string>();
        var rule2 = new List<string>();
        var rule3 = new List<string>();
        var rule4 = new List<string>();
        var rule4b = new List<string>();

        foreach (var file in files)
        {
            string text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text, path: file);
            var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            string relPath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            if (errors.Count > 0)
            {
                foreach (var d in errors)
                    parseDiagnostics.Add($"{relPath}: {d}");
                continue; // cannot reliably walk a tree with syntax errors
            }

            var visitor = new SweepVisitor(relPath, Path.GetFileNameWithoutExtension(file), AllowedMembers);
            visitor.Visit(tree.GetRoot());
            rule1.AddRange(visitor.Rule1);
            rule2.AddRange(visitor.Rule2);
            rule3.AddRange(visitor.Rule3);
            rule4.AddRange(visitor.Rule4);
            rule4b.AddRange(visitor.Rule4b);
        }

        return new SweepResult(repoRoot, files.Count, parseDiagnostics, rule1, rule2, rule3, rule4, rule4b);
    }

    private static bool IsBuildArtifactPath(string filePath)
    {
        var parts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => string.Equals(p, "obj", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p, "bin", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FlaUI.Mcp.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        Assert.Fail(
            $"RedactionSurfaceInventoryTests could not find 'FlaUI.Mcp.slnx' walking up from " +
            $"AppContext.BaseDirectory ('{AppContext.BaseDirectory}'). A sweep that cannot locate the repo " +
            "root would silently match zero files — refusing to proceed rather than false-GREEN.");
        return null!; // unreachable
    }

    // ============================== the walker ==============================

    private sealed class SweepVisitor : CSharpSyntaxWalker
    {
        private readonly string _relPath;
        private readonly Dictionary<string, string> _allowed;
        private readonly Stack<string> _typeStack = new();
        private readonly Stack<string> _memberStack = new();

        public readonly List<string> Rule1 = new();
        public readonly List<string> Rule2 = new();
        public readonly List<string> Rule3 = new();
        public readonly List<string> Rule4 = new();
        public readonly List<string> Rule4b = new();

        public SweepVisitor(string relPath, string fileBaseNameFallback, Dictionary<string, string> allowed)
        {
            _relPath = relPath;
            _allowed = allowed;
            _typeStack.Push(fileBaseNameFallback); // fallback for top-level statements (e.g. Program.cs)
        }

        private string CurrentType => _typeStack.Peek();
        private string CurrentMemberName => _memberStack.Count > 0 ? _memberStack.Peek() : "<top-level>";
        private string CurrentKey => $"{CurrentType}.{CurrentMemberName}";

        // ---- type scope ----
        public override void VisitClassDeclaration(ClassDeclarationSyntax node) { _typeStack.Push(node.Identifier.Text); base.VisitClassDeclaration(node); _typeStack.Pop(); }
        public override void VisitStructDeclaration(StructDeclarationSyntax node) { _typeStack.Push(node.Identifier.Text); base.VisitStructDeclaration(node); _typeStack.Pop(); }
        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) { _typeStack.Push(node.Identifier.Text); base.VisitRecordDeclaration(node); _typeStack.Pop(); }
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) { _typeStack.Push(node.Identifier.Text); base.VisitInterfaceDeclaration(node); _typeStack.Pop(); }

        // ---- member scope: a read inside a LOCAL FUNCTION or LAMBDA resolves to the enclosing member
        // (no push for LocalFunctionStatement / lambdas / anonymous methods below). ----
        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            bool isSanctioned = CurrentType == SanctionedType3 && node.Identifier.Text == "Build";
            _memberStack.Push(node.Identifier.Text);
            CheckClassifierDefaultNull(node.ParameterList, isSanctioned);
            CheckPropertyIdPassthrough(node.ParameterList, (SyntaxNode?)node.Body ?? node.ExpressionBody);
            base.VisitMethodDeclaration(node);
            _memberStack.Pop();
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            bool isSanctioned = CurrentType == SanctionedType1 || CurrentType == SanctionedType2;
            _memberStack.Push(node.Identifier.Text);
            CheckClassifierDefaultNull(node.ParameterList, isSanctioned);
            CheckPropertyIdPassthrough(node.ParameterList, (SyntaxNode?)node.Body ?? node.ExpressionBody);
            base.VisitConstructorDeclaration(node);
            _memberStack.Pop();
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            _memberStack.Push(node.Identifier.Text);
            base.VisitPropertyDeclaration(node);
            _memberStack.Pop();
        }

        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            _memberStack.Push("this[]");
            base.VisitIndexerDeclaration(node);
            _memberStack.Pop();
        }

        public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        {
            _memberStack.Push($"operator {node.OperatorToken.Text}");
            base.VisitOperatorDeclaration(node);
            _memberStack.Pop();
        }

        public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        {
            _memberStack.Push("operator conversion");
            base.VisitConversionOperatorDeclaration(node);
            _memberStack.Pop();
        }

        public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            CheckClassifierDefaultNull(node.ParameterList, isSanctioned: false); // no sanctioned site is a local function
            CheckPropertyIdPassthrough(node.ParameterList, (SyntaxNode?)node.Body ?? node.ExpressionBody);
            base.VisitLocalFunctionStatement(node); // NO push — resolves to the enclosing member
        }

        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
            CheckClassifierDefaultNull(node.ParameterList, isSanctioned: false);
            base.VisitParenthesizedLambdaExpression(node); // NO push
        }

        // ---- Rule 1: content reads ----
        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            string id = node.Name.Identifier.Text;
            if (!IsCallToElementContentAccessor(node) && !(id == "Value" && IsCapabilityOrWriteShape(node)))
                CheckNameOrValue(node, id, node.Expression.ToString());
            base.VisitMemberAccessExpression(node);
        }

        // SP3 reconciliation, Part A: three narrow `.Value` shapes that are not content reads by any
        // reading — excluded before Rule 1 ever sees them (not silenced via the allowlist, because they
        // are not violations to begin with):
        //   `.Value.IsSupported`        — a capability flag.
        //   `.Value.Pattern.IsReadOnly` — a capability flag.
        //   `.Value.Pattern.SetValue(...)` invocation — a WRITE, not a read.
        private static bool IsCapabilityOrWriteShape(MemberAccessExpressionSyntax node)
        {
            if (node.Parent is MemberAccessExpressionSyntax isSupported && isSupported.Expression == node
                && isSupported.Name.Identifier.Text == "IsSupported")
                return true;

            if (node.Parent is MemberAccessExpressionSyntax pat1 && pat1.Expression == node
                && pat1.Name.Identifier.Text == "Pattern")
            {
                if (pat1.Parent is MemberAccessExpressionSyntax isReadOnly && isReadOnly.Expression == pat1
                    && isReadOnly.Name.Identifier.Text == "IsReadOnly")
                    return true;

                if (pat1.Parent is MemberAccessExpressionSyntax setValue && setValue.Expression == pat1
                    && setValue.Name.Identifier.Text == "SetValue"
                    && setValue.Parent is InvocationExpressionSyntax inv && inv.Expression == setValue)
                    return true;
            }

            return false;
        }

        public override void VisitMemberBindingExpression(MemberBindingExpressionSyntax node)
        {
            string id = node.Name.Identifier.Text;
            CheckNameOrValue(node, id, PrecedingTextForConditionalAccess(node));
            base.VisitMemberBindingExpression(node);
        }

        // Evidence for this exemption: src/FlaUI.Mcp.Server/Install/CheckRedactionRulesCommand.cs's
        // MatchWindow doc comment states its ElementContent.Name(...) call "stays inside the closed
        // egress-accessor set (spec §7.2) and needs no new exemption from the source sweep" — i.e. the
        // codebase's own authors intend CALLS to the closed-list accessors (which return
        // ALREADY-REDACTED values) to be judged by the allowlist governing their DEFINITION, not
        // re-flagged at every call site. Narrow: only fires for `ElementContent.Name(...)` /
        // `ElementContent.Value(...)` used as an INVOCATION callee.
        private static bool IsCallToElementContentAccessor(MemberAccessExpressionSyntax node)
        {
            string id = node.Name.Identifier.Text;
            if (id != "Name" && id != "Value") return false;
            if (node.Parent is not InvocationExpressionSyntax inv || inv.Expression != node) return false;
            return SimpleTypeNameOfExpr(node.Expression) == "ElementContent";
        }

        private static string PrecedingTextForConditionalAccess(SyntaxNode node)
        {
            var cur = node.Parent;
            while (cur is not null && cur is not ConditionalAccessExpressionSyntax)
                cur = cur.Parent;
            return cur is ConditionalAccessExpressionSyntax cae ? cae.Expression.ToString() : string.Empty;
        }

        private void CheckNameOrValue(SyntaxNode node, string identifier, string precedingText)
        {
            if (identifier == "Name")
            {
                Flag(Rule1, node, "raw content read via `.Name`");
            }
            else if (identifier == "Value")
            {
                bool patternsValue = EndsWithSegment(precedingText, "Patterns");
                bool propertiesValue = EndsWithSegment(precedingText, "Properties");
                bool legacyValue = precedingText.Contains("LegacyIAccessible");
                if (patternsValue || propertiesValue || legacyValue)
                {
                    string which = patternsValue ? "Patterns.Value" : propertiesValue ? "Properties.Value" : "LegacyIAccessible…Value";
                    Flag(Rule1, node, $"raw content read via `.Value` ({which})");
                }
            }
        }

        private static bool EndsWithSegment(string text, string segment)
            => text == segment
            || text.EndsWith("." + segment, StringComparison.Ordinal)
            || text.EndsWith("?." + segment, StringComparison.Ordinal);

        // ---- Rule 1 (GetText) / Rule 4 (generic accessor) ----
        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            string? methodName = InvokedMethodName(node);

            if (methodName == "GetText")
                Flag(Rule1, node, "raw content read via `GetText(...)` invocation");
            else if (methodName is "GetCurrentPropertyValue" or "TryGetCurrentPropertyValue")
                CheckGenericAccessorArgument(node, methodName);

            CheckSnapshotEngineBuildCallSite(node, methodName);

            base.VisitInvocationExpression(node);
        }

        private static string? InvokedMethodName(InvocationExpressionSyntax node) => node.Expression switch
        {
            MemberAccessExpressionSyntax ma => (ma.Name as SimpleNameSyntax)?.Identifier.Text,
            MemberBindingExpressionSyntax mb => (mb.Name as SimpleNameSyntax)?.Identifier.Text,
            SimpleNameSyntax sn => sn.Identifier.Text,
            _ => null,
        };

        // ---- Rule 2: dynamic ----
        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            // Text-match only (no semantic model available): "dynamic" is a purely CONTEXTUAL keyword —
            // syntactically indistinguishable from an identifier literally named `dynamic` without a
            // semantic binder. ToString() (not just Identifier.Text) is used so an escaped `@dynamic`
            // identifier — which DOES print its leading `@` — is correctly excluded.
            if (node.Identifier.Text == "dynamic" && node.ToString() == "dynamic")
                Flag(Rule2, node, "`dynamic` usage — a runtime-bound receiver is invisible to this syntax walk");
            base.VisitIdentifierName(node);
        }

        // ---- Rule 3: caller-supplied property-id passthrough ----
        private void CheckPropertyIdPassthrough(ParameterListSyntax paramList, SyntaxNode? searchRoot)
        {
            if (searchRoot is null) return;
            foreach (var p in paramList.Parameters)
            {
                if (p.Type is null || !p.Type.ToString().Contains("PropertyId")) continue;
                string paramName = p.Identifier.Text;
                bool forwards = searchRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Any(inv => InvokedMethodName(inv) is "GetCurrentPropertyValue" or "TryGetCurrentPropertyValue"
                             && inv.ArgumentList.Arguments.Any(a => a.Expression is IdentifierNameSyntax idn && idn.Identifier.Text == paramName));
                if (!forwards) continue;
                if (_allowed.ContainsKey(CurrentKey)) continue; // rule 3 is scoped to "outside the two lists"
                int line = Line(p);
                Rule3.Add($"{_relPath}:{line} member={CurrentKey} — parameter '{paramName}' ({p.Type}) is a UIA property-id type forwarded to a generic accessor");
            }
        }

        // ---- Rule 4: generic accessor must be resolved by a static field reference ----
        private void CheckGenericAccessorArgument(InvocationExpressionSyntax node, string methodName)
        {
            if (node.ArgumentList is null || node.ArgumentList.Arguments.Count == 0)
            {
                Flag(Rule4, node, $"{methodName}(...) called with no property-id argument to verify");
                return;
            }
            var first = node.ArgumentList.Arguments[0];
            // "static field reference" shape: Type.Field (a MemberAccessExpression whose receiver is a
            // plain/qualified name, not a local, parameter, or expression).
            bool isStaticFieldShape = first.Expression is MemberAccessExpressionSyntax fma
                && fma.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax or QualifiedNameSyntax;
            if (!isStaticFieldShape)
                Flag(Rule4, node, $"{methodName}(...) property-id argument '{first.Expression}' is not statically resolvable to a static field reference — needs an explicit reasoned suppression");
        }

        // ---- Rule 4b: classifier-argument guarantee ----
        private void CheckClassifierDefaultNull(ParameterListSyntax? paramList, bool isSanctioned)
        {
            if (paramList is null) return;
            foreach (var p in paramList.Parameters)
            {
                if (p.Type is null) continue;
                string typeText = p.Type.ToString().Replace("?", "").Trim();
                bool isNullDefault = p.Default?.Value is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression);
                if (typeText != "SensitivityClassifier" || !isNullDefault) continue;
                if (isSanctioned) continue;
                int line = Line(p);
                Rule4b.Add($"{_relPath}:{line} member={CurrentKey} — declares a SensitivityClassifier parameter '{p.Identifier.Text}' with a null default outside the three sanctioned sites (PerceptionManager ctor / WatchPump ctor / SnapshotEngine.Build)");
            }
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            string typeName = SimpleTypeName(node.Type);
            if (typeName == SanctionedType1)
                CheckClassifierCallSite(node, node.ArgumentList, minPositionalCountForExplicit: 4, "new PerceptionManager(...)");
            else if (typeName == SanctionedType2)
                CheckClassifierCallSite(node, node.ArgumentList, minPositionalCountForExplicit: 7, "new WatchPump(...)");
            base.VisitObjectCreationExpression(node);
        }

        // A call site to SnapshotEngine.Build, qualified ("SnapshotEngine.Build(...)") or unqualified
        // ("Build(...)" from within SnapshotEngine itself, e.g. SnapshotEngine.Walk).
        private void CheckSnapshotEngineBuildCallSite(InvocationExpressionSyntax node, string? methodName)
        {
            bool isQualified = node.Expression is MemberAccessExpressionSyntax ma
                && ma.Name.Identifier.Text == "Build" && SimpleTypeNameOfExpr(ma.Expression) == SanctionedType3;
            bool isUnqualified = node.Expression is IdentifierNameSyntax idn
                && idn.Identifier.Text == "Build" && CurrentType == SanctionedType3;
            if (isQualified || isUnqualified)
                CheckClassifierCallSite(node, node.ArgumentList, minPositionalCountForExplicit: 6, "SnapshotEngine.Build(...)");
        }

        private void CheckClassifierCallSite(SyntaxNode node, ArgumentListSyntax? argList, int minPositionalCountForExplicit, string label)
        {
            ArgumentSyntax? named = null;
            if (argList is not null)
                foreach (var a in argList.Arguments)
                    if (a.NameColon?.Name.Identifier.Text == "classifier") { named = a; break; }

            bool explicitlyPassed;
            ArgumentSyntax? theArg;
            if (named is not null)
            {
                explicitlyPassed = true;
                theArg = named;
            }
            else
            {
                int positionalCount = argList?.Arguments.Count(a => a.NameColon is null) ?? 0;
                explicitlyPassed = positionalCount >= minPositionalCountForExplicit;
                theArg = explicitlyPassed ? argList!.Arguments[minPositionalCountForExplicit - 1] : null;
            }

            int line = Line(node);
            if (!explicitlyPassed)
            {
                Rule4b.Add($"{_relPath}:{line} member={CurrentKey} — {label} omits an explicit classifier argument (falls back to SensitivityClassifier.OsOnly at runtime)");
            }
            else if (theArg?.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression))
            {
                Rule4b.Add($"{_relPath}:{line} member={CurrentKey} — {label} passes a literal `null` classifier argument");
            }
        }

        // ---- shared helpers ----
        private static string? SimpleTypeNameOfExpr(ExpressionSyntax expr) => expr switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            QualifiedNameSyntax qn => qn.Right.Identifier.Text,
            GenericNameSyntax gn => gn.Identifier.Text,
            _ => null,
        };

        private static string SimpleTypeName(TypeSyntax type) => type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            QualifiedNameSyntax qn => qn.Right.Identifier.Text,
            GenericNameSyntax gn => gn.Identifier.Text,
            _ => type.ToString(),
        };

        private void Flag(List<string> bucket, SyntaxNode node, string what)
        {
            if (_allowed.ContainsKey(CurrentKey)) return;
            bucket.Add($"{_relPath}:{Line(node)} member={CurrentKey} — {what}");
        }

        private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }
}
