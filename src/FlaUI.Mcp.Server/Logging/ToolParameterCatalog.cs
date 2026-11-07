// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Reflection;
using System.Text;
using FlaUI.Mcp.Core.Logging;
using ModelContextProtocol.Server;

namespace FlaUI.Mcp.Server.Logging;

public enum ParamClass { Structural, UiText, Content, Number, Complex }

public sealed record ParamInfo(string Name, ParamClass Class, IReadOnlyDictionary<string, ParamClass>? Properties = null);

public sealed record ToolEntry(string Name, bool ForegroundTarget, IReadOnlyDictionary<string, ParamInfo> Parameters);

/// <summary>Tool name → parameter classes, read once by reflection from the [McpServerTool] methods
/// (spec §6.1). Names are the snake_case of the method name, which is what the SDK registers (no tool in
/// this repo sets `Name =`).</summary>
public sealed class ToolParameterCatalog
{
    private readonly Dictionary<string, ToolEntry> _tools;
    private ToolParameterCatalog(Dictionary<string, ToolEntry> tools) => _tools = tools;

    public IReadOnlyCollection<ToolEntry> Tools => _tools.Values;
    public ToolEntry? Find(string toolName) => _tools.TryGetValue(toolName, out var t) ? t : null;

    public static ToolParameterCatalog FromAssembly(Assembly assembly) =>
        FromTypes(assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null));

    public static ToolParameterCatalog FromTypes(IEnumerable<Type> types)
    {
        var map = new Dictionary<string, ToolEntry>(StringComparer.Ordinal);
        foreach (var type in types)
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.GetCustomAttribute<McpServerToolAttribute>() is null) continue;
            var ps = new Dictionary<string, ParamInfo>(StringComparer.Ordinal);
            foreach (var p in m.GetParameters())
            {
                if (p.Name is null || p.ParameterType == typeof(McpServer)) continue;
                ps[p.Name] = Describe(p.Name, p.ParameterType, p);
            }
            var name = SnakeCase(m.Name);
            map[name] = new ToolEntry(name, m.GetCustomAttribute<LogForegroundTargetAttribute>() is not null, ps);
        }
        return new ToolParameterCatalog(map);
    }

    internal static ParamClass ClassOf(ICustomAttributeProvider member) =>
        member.IsDefined(typeof(LogStructuralAttribute), false) ? ParamClass.Structural
        : member.IsDefined(typeof(LogUiTextAttribute), false) ? ParamClass.UiText
        : ParamClass.Content;

    private static ParamInfo Describe(string name, Type type, ICustomAttributeProvider attrs)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string) || t == typeof(string[])) return new ParamInfo(name, ClassOf(attrs));
        if (t.IsPrimitive || t == typeof(decimal) || t == typeof(double[]) || t == typeof(int[])) return new ParamInfo(name, ParamClass.Number);
        // Complex record bound from a JSON object: classify per property (spec §6.1).
        var props = new Dictionary<string, ParamClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var pt = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            props[prop.Name] = pt == typeof(string) ? ClassOf(prop) : pt.IsPrimitive ? ParamClass.Number : ParamClass.Content;
        }
        return new ParamInfo(name, ParamClass.Complex, props);
    }

    public static string SnakeCase(string methodName)
    {
        var n = methodName.EndsWith("Async", StringComparison.Ordinal) ? methodName[..^5] : methodName;
        var sb = new StringBuilder(n.Length + 8);
        for (int i = 0; i < n.Length; i++)
        {
            if (char.IsUpper(n[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(n[i]));
        }
        return sb.ToString();
    }
}
