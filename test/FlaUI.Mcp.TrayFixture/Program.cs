// Copyright (c) 2026 Costas Kirgoussios
//
// This software is licensed under the PolyForm Noncommercial License 1.0.0.
// You may obtain a copy of the License at:
// https://polyformproject.org/licenses/noncommercial/1.0.0/

using FlaUI.Mcp.TrayFixture;

// Arguments: --kind uia|classic|window   --tooltip "<text>"   (SPEC §7)
string kind = "uia";
string tooltip = $"FlaUI Tray Fixture {Guid.NewGuid():N}";
for (int i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--kind") kind = args[i + 1];
    else if (args[i] == "--tooltip") tooltip = args[i + 1];
}
if (kind is not ("uia" or "classic" or "window"))
{
    Console.Error.WriteLine($"unknown --kind '{kind}' (uia|classic|window)");
    return 2;
}
ApplicationConfiguration.Initialize();
Application.Run(new TrayForm(kind, tooltip));
return 0;
