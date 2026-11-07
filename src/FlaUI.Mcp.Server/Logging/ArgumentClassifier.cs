// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Logging;

/// <summary>Builds the logged `args` object (spec §6). Fails closed: an unknown tool, an unknown parameter,
/// an unknown property, or a string where a number was declared is content.</summary>
public static class ArgumentClassifier
{
    public static (JsonObject Args, bool HasContentArgs) Classify(ToolEntry? tool, IDictionary<string, JsonElement>? args,
        bool sensitive, ContentHasher hasher)
    {
        var o = new JsonObject();
        bool content = false;
        if (args is null) return (o, false);
        foreach (var (key, value) in args)
        {
            ParamInfo? p = null;
            tool?.Parameters.TryGetValue(key, out p);
            o[key] = p is null ? Walk(value, ParamClass.Content, sensitive, hasher, ref content)
                   : p.Class == ParamClass.Complex ? Complex(value, p, sensitive, hasher, ref content)
                   : Walk(value, p.Class, sensitive, hasher, ref content);
        }
        return (o, content);
    }

    private static JsonNode? Complex(JsonElement v, ParamInfo p, bool sensitive, ContentHasher hasher, ref bool content)
    {
        if (v.ValueKind != JsonValueKind.Object) return Walk(v, ParamClass.Content, sensitive, hasher, ref content);
        var o = new JsonObject();
        foreach (var prop in v.EnumerateObject())
        {
            var cls = p.Properties!.TryGetValue(prop.Name, out var c) ? c : ParamClass.Content;
            o[prop.Name] = Walk(prop.Value, cls, sensitive, hasher, ref content);
        }
        return o;
    }

    private static JsonNode? Walk(JsonElement v, ParamClass cls, bool sensitive, ContentHasher hasher, ref bool content)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                var s = v.GetString()!;
                switch (cls)
                {
                    case ParamClass.Structural: return JsonValue.Create(s);
                    case ParamClass.UiText: return sensitive ? Masked(hasher.Mask(s)) : JsonValue.Create(s);
                    default:
                        if (s.Length > 0) content = true;
                        return Masked(hasher.Mask(s));
                }
            case JsonValueKind.Number: return JsonNode.Parse(v.GetRawText());
            case JsonValueKind.True: return JsonValue.Create(true);
            case JsonValueKind.False: return JsonValue.Create(false);
            case JsonValueKind.Null: return null;
            case JsonValueKind.Array:
                var a = new JsonArray();
                foreach (var item in v.EnumerateArray()) a.Add(Walk(item, cls, sensitive, hasher, ref content));
                return a;
            case JsonValueKind.Object:
                var o = new JsonObject();
                foreach (var prop in v.EnumerateObject()) o[prop.Name] = Walk(prop.Value, ParamClass.Content, sensitive, hasher, ref content);
                return o;
            default: return null;
        }
    }

    private static JsonObject Masked(MaskedValue m) => new() { ["len"] = m.Len, ["sha8"] = m.Sha8 };
}
