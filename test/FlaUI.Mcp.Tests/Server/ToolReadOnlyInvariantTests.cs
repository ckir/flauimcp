using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FlaUI.Mcp.Server;
using FlaUI.Mcp.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace FlaUI.Mcp.Tests.Server;

/// <summary>Structural invariant over the WHOLE MCP tool surface: every tool must declare its mutation
/// posture, and every mutating tool must actually enforce <c>--read-only-mode</c>. This is the
/// "it can't recur" guard for the v0.7.3 capstone finding that <c>WindowTools</c>' launch/focus/close
/// bypassed the read-only gate (bare <c>[McpServerTool]</c> + <c>ToolResponse.Guard</c> instead of
/// <c>Destructive</c> + <c>GuardWrite</c>). Reflected — a NEW tool that forgets the gate fails HERE,
/// not in production, without anyone having to remember to add it to a hand-maintained list.</summary>
public class ToolReadOnlyInvariantTests
{
    private static readonly ServerOptions ReadOnlyOpts = new(ReadOnly: true, AllowElevation: false);

    private static (Type Type, MethodInfo Method, bool IsReadOnly, bool IsDestructive)[] Tools()
        => typeof(WindowTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttributesData().Any(d => d.AttributeType == typeof(McpServerToolAttribute)))
                .Select(m => (t, m, Explicit(m, "ReadOnly"), Explicit(m, "Destructive"))))
            .ToArray();

    // Read whether the named arg was EXPLICITLY set in source (via CustomAttributeData), NOT the
    // materialized property: the SDK's McpServerToolAttribute.Destructive DEFAULTS to true (the MCP
    // spec's conservative default), so the materialized value can't distinguish "declared destructive"
    // from "left at default". The explicit named-argument list is the true author intent.
    private static bool Explicit(MethodInfo m, string name)
    {
        var data = m.GetCustomAttributesData().First(d => d.AttributeType == typeof(McpServerToolAttribute));
        foreach (var na in data.NamedArguments)
            if (na.MemberName == name && na.TypedValue.Value is bool b) return b;
        return false;
    }

    [Fact]
    public void There_is_at_least_one_discovered_tool()
        => Assert.NotEmpty(Tools()); // guard against the reflection silently matching nothing

    [Fact]
    public void Every_tool_declares_exactly_one_of_ReadOnly_or_Destructive()
    {
        var offenders = Tools()
            .Where(t => t.IsReadOnly == t.IsDestructive) // both false (unannotated) OR both true (contradiction)
            .Select(t => $"{t.Type.Name}.{t.Method.Name} (ReadOnly={t.IsReadOnly}, Destructive={t.IsDestructive})")
            .ToList();
        Assert.True(offenders.Count == 0,
            "Every [McpServerTool] must be exactly one of ReadOnly=true or Destructive=true:\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public async Task Every_destructive_tool_short_circuits_to_WriteBlockedReadOnly_in_read_only_mode()
    {
        foreach (var (type, method, _, _) in Tools().Where(t => t.IsDestructive))
        {
            Assert.True(method.ReturnType == typeof(Task<string>),
                $"{type.Name}.{method.Name} is Destructive but does not return Task<string>; " +
                "extend this invariant to cover its result shape.");

            var instance = Construct(type);
            var args = method.GetParameters()
                .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                .ToArray();

            var result = await (Task<string>)method.Invoke(instance, args)!;

            Assert.True(result.Contains("WriteBlockedReadOnly"),
                $"{type.Name}.{method.Name} is marked Destructive but did NOT block in --read-only-mode. " +
                $"Route it through ToolResponse.GuardWrite(_options, ...). Got: {result}");
        }
    }

    // Construct a tool with a ReadOnly ServerOptions and null for every other dependency. GuardWrite
    // short-circuits BEFORE the body touches any dep, so a correctly-gated tool never dereferences the
    // nulls; a tool that (wrongly) uses plain Guard would throw on the null dep and surface INTERNAL
    // instead of WriteBlockedReadOnly — which is exactly the failure this invariant catches.
    private static object Construct(Type toolType)
    {
        var ctor = toolType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(p => p.ParameterType == typeof(ServerOptions) ? ReadOnlyOpts
                       : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();
        return ctor.Invoke(args)!;
    }
}
