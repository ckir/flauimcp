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
        // ⚠⚠ AUDITED ENTRY-BY-ENTRY AFTER THE FINAL AGY-CAPSTONE, AND THE AUDIT FOUND A REAL LEAK.
        // Every reason below USED to be one of exactly two boilerplate strings, pasted across 26 members.
        // That defeated the anti-gaming property above completely: the reason is only load-bearing if it
        // is individually TRUE, and a pasted reason is not a justification, it is a shape.
        //
        // The cost was not theoretical. EvaluateSelectorValueAsync carried "returns the already-redacted
        // value" while it in fact returned the RAW value for any rule-redacted element — so the sweep, the
        // one mechanism built to make that impossible, was silenced ON THE EXACT MEMBER that was leaking,
        // by a sentence that was false. Several others were merely wrong rather than dangerous
        // (Safe is a try/catch wrapper, not an accessor; Signature is a stability hash, not ref
        // re-resolution; VerifyReader.FromElement is an egress reader, not an identity reader).
        //
        // RULE FOR THE NEXT PERSON: if you cannot write a reason that is specifically true of THIS member,
        // the member does not belong on this list — fix the member instead. Never paste a neighbour's.
        ["ElementContent.Name"] = "EGRESS accessor: this IS the closed-list read; returns [REDACTED] whenever the decision says redact, raw only when it does not",
        ["ElementContent.Value"] = "EGRESS accessor: as .Name, plus the empty-value Name fallback, which reuses the name a rule already read rather than opening a second read window",
        ["ElementContent.Text"] = "EGRESS accessor: wraps a caller-supplied text read and withholds it on a redact decision",
        ["ElementContent.Classify"] = "DECISION, not a read of content: reads raw identity + IsPassword solely to decide, and returns a Sensitivity. No caller can obtain content through it",
        ["ElementContent.SensitivityOf"] = "DECISION only: Classify's result for a caller that never reads the content (the pixel path). Returns Sensitivity, never a value",
        ["ElementContent.Safe"] = "NOT A READ: exception-swallowing wrappers used BY the accessors above. They add no egress of their own",
        ["RefRegistry.ResolveDescriptor"] = "IDENTITY: raw name is the ref's identity when AutomationId is absent (BC-1). Used to re-find an element, never emitted",
        ["RefRegistry.ResolveStrict"] = "IDENTITY: strict re-resolution by live RuntimeId + descriptor; the raw name never leaves the registry",
        ["RefRegistry.FastPathMatches"] = "IDENTITY: descriptor equality check on the re-resolution fast path; compares, never returns, the raw name",
        ["SnapshotEngine.Build"] = "CONSTRUCTION: builds SnapshotNode, which deliberately KEEPS the raw name for BC-1 identity and carries the Sensitivity beside it. Redaction is applied downstream, at FormatNode and at each DTO projection — so this member is safe only because those are on this list too",
        ["SnapshotEngine.FormatNode"] = "EGRESS accessor: the tree's redaction point — emits ShownName ([REDACTED] on a redact decision) plus the redacted:rule:/redacted:unreadable marker",
        ["SnapshotDiff.Identity"] = "IDENTITY: keys a node on its raw name so a redacted node still matches ITSELF across two snapshots (BC-1). The key is internal to the diff",
        ["SnapshotDiff.IdentityKey"] = "IDENTITY: as .Identity — the composed key, never emitted",
        ["SnapshotDiff.Subtree"] = "IDENTITY: scopes the diff by walking on identity keys; reads no content",
        ["SnapshotDiff.ShownName"] = "EGRESS accessor: the diff's redaction point — reads Sensitivity.Redact, not Source == Os, so operator rules apply",
        ["WaitCoordinator.Matches"] = "WAIT PREDICATE, DEF-3 gated: `by:name` is short-circuited by !n.Sensitivity.Redact, so the raw-name compare is REACHED ONLY for non-redacted nodes. automationId/controlType stay matchable for BC-1 targetability",
        ["WaitCoordinator.Signature"] = "STABILITY HASH — explicitly NOT ref re-resolution (the old reason here was wrong). Includes the raw name under includeText so wait_for_stable can detect that a value CHANGED. Substituting the token would make two different secrets compare equal and report `stable` while content is actively changing, which breaks that tool's never-report-a-wrong-belief contract. The hash is internal and never reaches the wire: it reveals THAT something changed, never what. Reviewed and upheld as a deliberate trade at the final capstone",
        ["WatchPayloadBuilder.Build"] = "EGRESS accessor: the watch payload's redaction point; emits [REDACTED] and preserves the null-vs-empty wire contract via the Absent flag",
        ["LiveEventSourceReader.MintRef"] = "IDENTITY: mints a ref from the raw name; the payload's visible name comes from WatchPayloadBuilder.Build above, not from here",
        ["PerceptionManager.FindAsync"] = "EGRESS accessor: match names are redacted at construction AND redacted elements are excluded from name-constrained queries (DEF-3)",
        ["PerceptionManager.ResolveSelectorOnSta"] = "RESOLUTION: returns an ELEMENT, never its content. Rule-aware since Task 9 — the name predicate is classified, not compared raw",
        ["PerceptionManager.ReadText"] = "EGRESS accessor: delegates to ElementContent.Text, so the withhold decision is made in one place",
        ["PerceptionManager.ReadGridCell"] = "EGRESS accessor: delegates to ElementContent.Value for the cell's content",
        ["PerceptionManager.EvaluateSelectorValueAsync"] = "EGRESS accessor: returns NULL — never the value — when the classifier says redact, so wait_for(until:valueEquals) cannot confirm a guess. ⚠ THIS ENTRY'S PREVIOUS REASON WAS FALSE: the gate was OS-only, so rule-redacted values were returned RAW. Fixed at the final capstone (finding L1); this reason is now true only because that fix landed",
        ["VerifyReader.FromElement"] = "EGRESS accessor (the old reason called it an identity reader, which was wrong): reads text for post-action verification and reports Redacted so InputTools skips the compare rather than reading protected content",
        ["TerminalTabReader.Title"] = "EGRESS accessor: the 13th site — a terminal tab title is content-bearing and is classified like any other name",

        // ---- SP3 reconciliation, Part C: the driver's classification of the sweep's remaining
        // findings. Reasons are verbatim from the driver — do not rephrase. ----
        ["FindQuerySpec.HasNameConstraint"] = "not a UIA read: _q.Name is the CALLER'S SEARCH TERM (FindQuery.Name), never an element property",
        ["FindQuerySpec.MatchesPostFilter"] = "not a UIA read: _q.Name is the caller's search term; the `name` argument compared against it was already redacted by the caller",
        ["RedactionRuleFile.Parse"] = "not a UIA read: r.Name is the RULE's configured name from the operator's own JSON file",
        ["CollisionMarker.BuildJson"] = "not a UIA read: m.Name is MarketplaceSource.Name, and MarketplaceSource is constructed at exactly two production sites - KnownMarketplaces.Read (a JSON property KEY from Claude Code's known_marketplaces.json) and CollisionMarker.ParseMarketplace (our own marker file, written from the first). No AutomationElement is involved on either path; the whole Install namespace never touches UIA except CheckRedactionRulesCommand, which never constructs one. Re-verify with: grep -rn \"new MarketplaceSource(\" src/",
        ["ClaudeCollisionRemedy.TryReinstall"] = "not a UIA read: mkt.Name and live.Name are MarketplaceSource.Name - the marketplace ALIAS from known_marketplaces.json, a local config record naming a plugin source. Same provenance as CollisionMarker.BuildJson above; see that entry for the two-construction-site trace.",
        ["SensitivityClassifier.Classify"] = "not a UIA read: rule.Name is the matched rule's configured name, which becomes Sensitivity.RuleName",
        ["SnapshotEngine.IsInteractiveNode"] = "reads SnapshotNode.Name but ONLY as IsNullOrWhiteSpace emptiness; exposes no content, not even via branching on it",
        ["SnapshotEngine.SupportedPatterns"] = "not a UIA read: c.Name is a hardcoded pattern label from a (string Name, Func<bool>) tuple - Invoke, Value, etc.",
        // ⚠⚠ THIS REASON HAS THE SAME SHAPE AS THE FALSE ONE IT REPLACES, AND THAT IS A HAZARD, NOT A
        // COINCIDENCE. The removed text read "window TITLE via Properties.Name, not element content" and was
        // FALSE, because the element being read was the FOCUSED ELEMENT. The text below is TRUE, because the
        // element being read is the WINDOW ROOT. The distinction IS the entire justification. A future
        // reviewer must check WHICH ELEMENT this member reads before honouring the exemption — not merely
        // that a plausible sentence is present. Two reasons in this file have already turned out false, and
        // one of them silenced the sweep on the exact member that was leaking.
        ["WindowManager.ResolveFocusedWindowAsync"] = "IDENTITY reader: reads the WINDOW ROOT's Name, which IS the window's title, and only as a fallback when the Win32 caption is empty (a framework drawing its own title bar). NOT the focused element's Name - that was the false version of this reason, and the leak it hid is fixed in SP4/A5",
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
        Add("RULE 5 — bare redaction-token literal in src/ outside ElementContent", r.Rule5);

        Assert.True(total == 0,
            $"Redaction surface sweep found {total} violation(s) across {sections.Count} rule(s):\n\n" +
            string.Join("\n\n", sections));
    }

    // ============================== RULE 5 pins (SP4/A6) ==============================

    /// Parse a snippet and run the REAL visitor with the REAL allowlist over it.
    ///
    /// ⚠ <paramref name="relPath"/> is NOT decoration. RULE 5's exemption is keyed on the type name AND its
    /// defining FILE, so a snippet declaring `ElementContent` at some other path is correctly FLAGGED. A
    /// caller checking the exemption must pass the real defining path; everything else uses the default.
    private static List<string> Rule5Over(string source, string relPath = "src/Fake.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: relPath);
        Assert.DoesNotContain(tree.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var visitor = new SweepVisitor(relPath, "Fake", AllowedMembers);
        visitor.Visit(tree.GetRoot());
        return visitor.Rule5;
    }

    /// <summary>⚠ THE ANTI-DEAD-ON-ARRIVAL PIN, and the reason this rule is written separately from every
    /// other rule in this file. SnapshotDiff.ShownName is IN AllowedMembers (:64) and it is one of the three
    /// sites A6 exists to catch. If RULE 5 were routed through the `_allowed.ContainsKey` suppression at
    /// :454 / :567 — the obvious way to write it, reusing the machinery already here — it would fire on
    /// nothing that matters and report GREEN forever.</summary>
    [Fact]
    public void Rule5_fires_inside_a_member_that_the_content_allowlist_exempts()
    {
        Assert.True(AllowedMembers.ContainsKey("SnapshotDiff.ShownName"),
            "this pin is only meaningful while SnapshotDiff.ShownName is allowlisted; it no longer is");

        var hits = Rule5Over(@"
namespace X;
public static class SnapshotDiff
{
    private static string ShownName(SnapshotNode n) => n.Sensitivity.Redact ? ""[REDACTED]"" : n.Name;
}");

        var hit = Assert.Single(hits);
        Assert.Contains("SnapshotDiff.ShownName", hit);
    }

    /// <summary>The rule is AST-aware, and that is what keeps it from breaking the build on documentation.
    /// A token inside a `//` or `///` comment is TRIVIA, never a LiteralExpressionSyntax, so it is never
    /// visited. Twelve such comments exist in src/ today.</summary>
    [Fact]
    public void Rule5_ignores_the_token_in_comments_and_doc_comments()
    {
        var hits = Rule5Over(@"
namespace X;
/// <summary>A password field's name is ""[REDACTED]"" on the wire.</summary>
public static class Documented
{
    // the snapshot renders [REDACTED] here
    private static string Safe() => ""nothing to see"";
}");

        Assert.Empty(hits);
    }

    /// <summary>The exemption set has exactly one entry: the type that DEFINES the token, IN ITS OWN FILE.
    /// Anywhere else in src/, a literal copy of it is the coupling this rule removes.
    ///
    /// ⚠ The third assertion is the anti-gaming half, and it is why the exemption is keyed on the file and
    /// not just the type name: `CurrentType` holds only the SHORT class name, so declaring a second
    /// `class ElementContent` anywhere in src/ and putting the literal inside it would otherwise satisfy
    /// the letter of the guard while leaving a hole in it.</summary>
    [Fact]
    public void Rule5_exempts_only_the_type_that_defines_the_token_in_its_own_file()
    {
        const string decl = @"
namespace X;
public static class ElementContent { public const string RedactedToken = ""[REDACTED]""; }";

        // The real definition, at its real path: exempt.
        Assert.Empty(Rule5Over(decl, "src/FlaUI.Mcp.Core/Perception/ElementContent.cs"));

        // Any other type: flagged.
        Assert.Single(Rule5Over(@"
namespace X;
public static class SomethingElse { public const string Copy = ""[REDACTED]""; }"));

        // ⚠ THE SAME TYPE NAME AT A DIFFERENT PATH: flagged. An impostor cannot borrow the exemption.
        Assert.Single(Rule5Over(decl, "src/FlaUI.Mcp.Server/Tools/Impostor.cs"));
    }

    /// <summary>The OTHER half of the same exemption, and the three assertions above cannot see it.
    ///
    /// ⚠⚠ MEASURED VACUOUS BEFORE THIS FACT EXISTED. Deleting
    /// <c>string.Equals(CurrentType, TokenDefiningType, ...)</c> from <c>inTokenDefiningType</c> — leaving
    /// the exemption keyed on the FILE PATH alone — left all six RULE 5 tests GREEN. The reason is that the
    /// negative cases above vary the PATH: "any other type" is evaluated at the default <c>src/Fake.cs</c>,
    /// which fails the path half anyway, so the type half is never the thing that decides.
    ///
    /// This fact varies the TYPE while HOLDING THE PATH FIXED at the real defining file, which is the only
    /// shape that isolates it. Under that mutation an impostor class declared inside
    /// <c>ElementContent.cs</c> would be silently exempted — exactly the hole the file-keying was added to
    /// close, reopened from the other side.</summary>
    [Fact]
    public void Rule5_flags_a_different_type_in_the_token_defining_file()
    {
        var hits = Rule5Over(@"
namespace X;
public static class Impostor { public const string Copy = ""[REDACTED]""; }",
            "src/FlaUI.Mcp.Core/Perception/ElementContent.cs");

        var hit = Assert.Single(hits);
        Assert.Contains("Impostor", hit);
    }

    // ============================== sweep engine ==============================

    private sealed record SweepResult(
        string RepoRoot, int SrcFileCount, List<string> ParseDiagnostics,
        List<string> Rule1, List<string> Rule2, List<string> Rule3, List<string> Rule4, List<string> Rule4b,
        List<string> Rule5);

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
        var rule5 = new List<string>();

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
            rule5.AddRange(visitor.Rule5);
        }

        return new SweepResult(repoRoot, files.Count, parseDiagnostics, rule1, rule2, rule3, rule4, rule4b, rule5);
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
        public readonly List<string> Rule5 = new();

        // The token itself, taken from the ONE place that defines it rather than spelled again here — a
        // fourth independent copy inside the rule that forbids independent copies would be its own joke.
        // The sweep only scans src/, so this reference in test/ is not self-flagging.
        private static readonly string RedactionToken = FlaUI.Mcp.Core.Perception.ElementContent.RedactedToken;
        private const string TokenDefiningType = "ElementContent";
        private const string TokenDefiningFile = "src/FlaUI.Mcp.Core/Perception/ElementContent.cs";

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

        // ---- Rule 5 (SP4/A6): the redaction token may not appear as a bare string literal anywhere in
        // src/ outside the type that DEFINES it.
        //
        // ⚠⚠ DELIBERATELY INDEPENDENT OF `_allowed`. The allowlist exempts a member from the CONTENT-READ
        // rules because it is a sanctioned egress accessor; that says nothing about whether the member may
        // hard-code the token string. The two concerns are orthogonal, and conflating them would be fatal
        // rather than untidy: SnapshotEngine.FormatNode (:60), SnapshotDiff.ShownName (:64) and
        // PerceptionManager.ResolveSelectorOnSta (:70) are ALL allowlisted, and they are ALL the sites this
        // rule exists to catch. Routing it through the `_allowed.ContainsKey` suppression at :454 / :567
        // would leave it firing on nothing and reporting GREEN forever.
        // Rule5_fires_inside_a_member_that_the_content_allowlist_exempts pins exactly that.
        //
        // AST-awareness is free here rather than new machinery: a token in a `//` or `///` comment is
        // TRIVIA and never reaches a LiteralExpressionSyntax, so documentation is untouched.
        //
        // HONEST LIMITS, so this is never mistaken for a guarantee:
        //  · BYPASSABLE BY CONSTRUCTION — writing the token as a concatenation, or as a second constant,
        //    defeats it. It catches ACCIDENT, not intent.
        //  · NOT A LEAK GUARD — the existing egress tests already compare against the constant, so a typo
        //    at any single site already fails a test. This removes a coupling.
        //  · An interpolated string's text is InterpolatedStringTextSyntax, not a literal, so a token
        //    embedded in a $"..." is not seen. No such site exists in src/ today.
        public override void VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            // The exemption is keyed on the type name AND its file. CurrentType holds only the SHORT
            // class name, so a type-name-only exemption is defeated by declaring a second
            // `class ElementContent` anywhere in src/ and putting the literal inside it — which satisfies
            // the letter of the guard while leaving a hole in it. Requiring the defining file closes that
            // without a semantic model.
            //
            // ⚠ The PATH half compares case-INSENSITIVELY on purpose. `relPath` comes from
            // Path.GetRelativePath, which preserves the casing ON DISK, so an ordinal compare would fail —
            // and fail the BUILD, by flagging ElementContent's own constant — on a clone whose folder is
            // `Src`. Windows paths are case-insensitive, so nothing is lost: an impostor still needs a
            // different PATH, not merely different casing, and the TYPE-name half stays ordinal.
            bool inTokenDefiningType =
                string.Equals(CurrentType, TokenDefiningType, StringComparison.Ordinal)
                && _relPath.EndsWith(TokenDefiningFile, StringComparison.OrdinalIgnoreCase);

            if (node.IsKind(SyntaxKind.StringLiteralExpression)
                && !inTokenDefiningType
                && node.Token.ValueText.Contains(RedactionToken, StringComparison.Ordinal))
            {
                int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                Rule5.Add($"{_relPath}:{line} member={CurrentKey} — string literal contains the redaction " +
                          "token; use ElementContent.RedactedToken (concatenate it if the literal is a " +
                          "longer sentence — const + const is a compile-time constant, so this works in an " +
                          "attribute argument too)");
            }
            base.VisitLiteralExpression(node);
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
