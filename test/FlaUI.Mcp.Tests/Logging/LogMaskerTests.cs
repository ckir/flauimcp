// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using System;
using System.Collections.Generic;
using System.Linq;
using FlaUI.Mcp.Core.Logging;
using FlaUI.Mcp.Server.Logging;
using Xunit;

namespace FlaUI.Mcp.Tests.Logging;

public class LogMaskerTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)7, 32).ToArray();
    private static LogMasker Masker(Func<string?, bool>? sensitiveProcess = null) =>
        new(new ContentHasher(Key), sensitiveProcess ?? (p => p == "keepass"));

    private static LogScope Scope(bool sensitive, bool content = false, bool targetless = false) =>
        new("c-00000001", "desktop_click", sensitive, content, targetless);

    [Fact]
    public void Hash_is_keyed_8_hex_and_deterministic_per_key()
    {
        var a = new ContentHasher(Key).Mask("1234");
        Assert.Equal(4, a.Len);
        Assert.Matches("^[0-9a-f]{8}$", a.Sha8);
        Assert.Equal(a, new ContentHasher(Key).Mask("1234"));
        Assert.NotEqual(a.Sha8, new ContentHasher(Enumerable.Repeat((byte)8, 32).ToArray()).Mask("1234").Sha8);
        // Not a plain SHA-256 prefix (spec §6.1: unkeyed hashes of short secrets are reversible).
        var plain = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("1234"u8.ToArray()))[..8].ToLowerInvariant();
        Assert.NotEqual(plain, a.Sha8);
    }

    [Fact]
    public void Ui_text_is_raw_when_not_sensitive_and_masked_when_sensitive()
    {
        var f = new[] { LogEvent.Ui("name", "Save As"), LogEvent.F("candidates", 0) };
        var raw = Masker().Apply("selector.resolve", f, Scope(false));
        Assert.Equal("Save As", raw[0].Value);
        var masked = Masker().Apply("selector.resolve", f, Scope(true));
        Assert.IsType<MaskedValue>(masked[0].Value);
        Assert.Equal(0, masked[1].Value);
    }

    [Fact]
    public void No_scope_and_no_own_process_fails_closed()
    {
        var m = Masker().Apply("selector.resolve", new[] { LogEvent.Ui("name", "x") }, scope: null);
        Assert.IsType<MaskedValue>(m[0].Value);
    }

    [Fact]
    public void An_events_own_process_field_decides_out_of_scope()
    {
        var notepad = Masker().Apply("window.bind",
            new[] { LogEvent.F("process", "notepad"), LogEvent.Ui("title", "a.txt") }, scope: null);
        Assert.Equal("a.txt", notepad[1].Value);
        var keepass = Masker().Apply("window.bind",
            new[] { LogEvent.F("process", "keepass"), LogEvent.Ui("title", "db.kdbx") }, scope: null);
        Assert.IsType<MaskedValue>(keepass[1].Value);
    }

    [Fact]
    public void An_own_sensitive_process_masks_even_inside_a_non_sensitive_call()
    {
        var m = Masker().Apply("action.blocked",
            new[] { LogEvent.F("fgProcess", "keepass"), LogEvent.Ui("fgTitle", "Unlock") }, Scope(false));
        Assert.IsType<MaskedValue>(m[1].Value);
    }

    [Fact]
    public void Exception_message_is_masked_on_a_content_call_whatever_the_window()
    {
        // spec §6.4
        var f = new[] { LogEvent.Ui("msg", "Value 'hunter2' rejected") };
        Assert.IsType<MaskedValue>(Masker().Apply("tool.exception", f, Scope(false, content: true))[0].Value);
        Assert.Equal("Value 'hunter2' rejected", Masker().Apply("tool.exception", f, Scope(false))[0].Value);
    }

    [Fact]
    public void Internal_tool_end_message_is_masked_on_a_content_call()
    {
        var f = new[] { LogEvent.F("outcome", "INTERNAL"), LogEvent.Ui("msg", "x") };
        Assert.IsType<MaskedValue>(Masker().Apply("tool.end", f, Scope(false, content: true))[1].Value);
        var g = new[] { LogEvent.F("outcome", "SelectorNoMatch"), LogEvent.Ui("msg", "x") };
        Assert.Equal("x", Masker().Apply("tool.end", g, Scope(false, content: true))[1].Value);
    }

    [Fact]
    public void Null_ui_text_stays_null()
    {
        Assert.Null(Masker().Apply("e", new[] { LogEvent.Ui("title", null) }, Scope(true))[0].Value);
    }
}
