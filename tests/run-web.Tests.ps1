BeforeAll {
    $scriptPath = Join-Path $PSScriptRoot '../run-web.ps1'
}

Describe 'run-web.ps1' {
    It 'supports a side-effect-free preview with its documented defaults' {
        { & $scriptPath -WhatIf } | Should -Not -Throw
    }

    It 'rejects duplicate listener ports before starting applications' {
        { & $scriptPath -AdminPort 5001 -GuestPort 5001 -ApiPort 5003 -WhatIf } |
            Should -Throw -ExpectedMessage '*must use different ports*'
    }
}
