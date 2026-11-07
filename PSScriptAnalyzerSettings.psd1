<#
 Copyright (c) 2026 Costas Kirgoussios

 This software is licensed under the PolyForm Noncommercial License 1.0.0.
 You may obtain a copy of the License at:
 https://polyformproject.org/licenses/noncommercial/1.0.0/
#>

# PSScriptAnalyzer profile for this repository's scripts. Gate: scripts/psscriptanalyzer.Tests.ps1 fails on ANY
# diagnostic this profile reports, so every exclusion below widens what the gate cannot see. The exact list is
# pinned by that test file; adding a rule here means editing the test too, on purpose.
# Design and the measured baseline: docs/superpowers/specs/2026-11-06-pssa-warning-gate-design.md
@{
    Severity     = @('Error', 'Warning')

    ExcludeRules = @(
        # Interactive console tools and an installer whose output a human reads. The rule targets functions whose
        # output feeds a pipeline; switching to Write-Output would leak text into return values that callers
        # (and release.Tests.ps1) capture.
        'PSAvoidUsingWriteHost'

        # The BOM rule exists for Windows PowerShell 5.1. Every script here runs under pwsh 7 (justfile shell,
        # lefthook.yml, ci.yml), where UTF-8 without a BOM is the default encoding.
        'PSUseBOMForUnicodeEncodedFile'

        # The repo uses a custom -WhatIf switch on every runnable script instead of SupportsShouldProcess
        # (the "Design note" just below the param block of scripts/release.ps1; the -WhatIf convention is pinned by
        # scripts/pester-pin.Tests.ps1). These two rules ask for the mechanism the repo chose not to use.
        'PSUseShouldProcessForStateChangingFunctions'
        'PSUseSupportsShouldProcess'

        # Flags internal helpers (Assert-Preconditions, Get-CodeWithoutComments), not exported cmdlets.
        # Renaming churns the test suite for no safety gain.
        'PSUseSingularNouns'
    )
}
