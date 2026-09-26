<#
.SYNOPSIS
    Packs this template, installs it into a throwaway template hive, generates a project from it
    and asserts that the wizard's choices actually reached the generated output.

.DESCRIPTION
    Template-repository tooling. Excluded from the package and from every generated project.

    What it proves, in order:
      1. Every "replaces" literal in .template.config/template.json still occurs in the template
         content. A replace whose literal has drifted matches nothing and fails silently - which is
         how every generated project came to be named GXApplication (Pass 44 F4).
      2. The package builds (build/pack.csproj, exactly as the README says) and installs into a
         CUSTOM HIVE under the work folder. The machine's own template store is never touched.
      3. `dotnet new gxblazor` generates with the given options, and the output:
           - carries no migrator, package, project reference or test code for a provider that was
             not chosen (SQLite always ships: it is the database the test harnesses run on);
           - names its databases after the project, and writes DBProvider, DefaultTimeZone and
             AllowSelfRegistration as chosen;
           - carries no template conditional left unprocessed, no Docker artefact and no
             template-repository-only file;
           - does not carry the project name inside upstream attribution text.
      4. The generated solution builds with 0 errors.

    Generates into a SHORT path under %TEMP% on purpose: the SQLite native assets nest deeply
    enough under bin\ that a generated project in a long path fails to build with MSB3021
    (MAX_PATH), which is a property of the location, not of the template.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tooling\smoke-generate.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tooling\smoke-generate.ps1 -Database mssql -KeepOutput
#>
[CmdletBinding()]
param(
    [ValidateSet('postgresql', 'mssql', 'sqlite')]
    [string] $Database = 'postgresql',

    [string] $ProjectName = 'SmokeApp',

    [string] $DefaultTimeZone = 'Africa/Lagos',

    [ValidateSet('true', 'false')]
    [string] $AllowSelfRegistration = 'false',

    # Where the package, the hive and the generated project go. Defaults to a fresh short folder
    # under %TEMP%, deleted afterwards unless -KeepOutput is given.
    [string] $WorkRoot = (Join-Path $env:TEMP ('gxsmoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))),

    [switch] $KeepOutput,

    # Skip building the generated solution - for iterating on the content assertions only.
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

function Fail([string] $message) { $failures.Add($message); Write-Host "  FAIL  $message" -ForegroundColor Red }
function Pass([string] $message) { Write-Host "  ok    $message" -ForegroundColor Green }
function Step([string] $message) { Write-Host ''; Write-Host "== $message" -ForegroundColor Cyan }

function Invoke-Dotnet([string[]] $arguments, [string] $logFile) {
    # Output to a log, not the console: a failing build is read from the log, and the console
    # stays readable. Returns the exit code.
    #
    # 'Continue' for the duration: Windows PowerShell turns every line a native command writes to
    # stderr into an ErrorRecord, and under 'Stop' the first one - even an empty line from a
    # successful `dotnet new` - aborts the script. The exit code is the verdict, not stderr.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & dotnet @arguments *> $logFile }
    finally { $ErrorActionPreference = $previous }
    return $LASTEXITCODE
}

# Text files the template engine processes, relative to a root. Build output is never content.
function Get-ContentFiles([string] $root) {
    Get-ChildItem -Path $root -Recurse -File -Force |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git|\.vs|node_modules)\\' } |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.razor', '.cshtml', '.json', '.props', '.targets', '.slnx', '.md', '.yml', '.xml', '.config' }
}

$providers = @{
    mssql      = @{ Migrator = 'Migrators.MSSQL';      Packages = @('Microsoft.EntityFrameworkCore.SqlServer', 'EntityFrameworkCore.Exceptions.SqlServer', 'Serilog.Sinks.MSSqlServer');                                    Code = 'DbProviderKeys\.SqlServer|Microsoft\.Data\.SqlClient|(?<![A-Za-z])SqlConnection\b|UseSqlServer\(|BuildSqlServerColumnOptions|SqlServerBatchPeriod|Serilog\.Sinks\.MSSqlServer' }
    postgresql = @{ Migrator = 'Migrators.PostgreSQL'; Packages = @('Npgsql.EntityFrameworkCore.PostgreSQL', 'EntityFrameworkCore.Exceptions.PostgreSQL', 'Serilog.Sinks.Postgresql.Alternative', 'EFCore.NamingConventions'); Code = 'DbProviderKeys\.Npgsql|using Npgsql|NpgsqlConnection|UseNpgsql\(|BuildNpgsqlColumnWriters|NpgsqlTableName|PostgresException' }
}

try {
    New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
    $pkgDir = Join-Path $WorkRoot 'pkg'
    $hive = Join-Path $WorkRoot 'hive'
    $out = Join-Path $WorkRoot $ProjectName
    Write-Host "Work folder: $WorkRoot"

    # ------------------------------------------------------------------ 1. replaces literals
    Step 'template.json replaces literals occur in the template content'
    $templateJson = Get-Content -Raw (Join-Path $repo '.template.config\template.json') | ConvertFrom-Json
    $contentText = (Get-ContentFiles $repo |
        Where-Object { $_.FullName -notmatch '\\(\.template\.config|GXTemplate-passes|docs|doc|build|tooling)\\' } |
        ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
    foreach ($symbol in $templateJson.symbols.PSObject.Properties) {
        $literal = $symbol.Value.replaces
        if (-not $literal) { continue }
        if ($contentText.Contains($literal)) { Pass "$($symbol.Name) matches '$literal'" }
        else { Fail "$($symbol.Name): its replaces literal '$literal' occurs nowhere in the template content, so it replaces nothing" }
    }

    # ------------------------------------------------------------------ 2. pack and install
    Step 'pack and install into a custom hive'
    if ((Invoke-Dotnet @('pack', (Join-Path $repo 'build\pack.csproj'), '-o', $pkgDir, '-nologo') (Join-Path $WorkRoot 'pack.log')) -ne 0) {
        throw "dotnet pack failed - see $(Join-Path $WorkRoot 'pack.log')"
    }
    $package = Get-ChildItem $pkgDir -Filter 'GX.Blazor.Template.*.nupkg' | Select-Object -First 1
    Pass "packed $($package.Name)"
    if ((Invoke-Dotnet @('new', 'install', $package.FullName, '--debug:custom-hive', $hive) (Join-Path $WorkRoot 'install.log')) -ne 0) {
        throw "dotnet new install failed - see $(Join-Path $WorkRoot 'install.log')"
    }
    Pass "installed into $hive"

    # ------------------------------------------------------------------ 3. generate
    Step "generate: -n $ProjectName --Database $Database --DefaultTimeZone $DefaultTimeZone --AllowSelfRegistration $AllowSelfRegistration"
    $generate = @('new', 'gxblazor', '-n', $ProjectName, '-o', $out,
                  '--Database', $Database, '--DefaultTimeZone', $DefaultTimeZone,
                  '--AllowSelfRegistration', $AllowSelfRegistration, '--debug:custom-hive', $hive)
    if ((Invoke-Dotnet $generate (Join-Path $WorkRoot 'generate.log')) -ne 0) {
        throw "dotnet new gxblazor failed - see $(Join-Path $WorkRoot 'generate.log')"
    }
    Pass "generated $out"

    $files = @(Get-ContentFiles $out)
    $projectFiles = @($files | Where-Object { $_.Extension -eq '.csproj' -or $_.Extension -eq '.slnx' })
    $codeFiles = @($files | Where-Object { $_.Extension -in '.cs', '.razor' })

    Step 'providers that were not chosen are absent'
    foreach ($key in $providers.Keys) {
        $p = $providers[$key]
        $migrator = Join-Path $out "src\Migrators\$($p.Migrator)"
        if ($key -eq $Database) {
            if (Test-Path $migrator) { Pass "$($p.Migrator) present (chosen)" } else { Fail "$($p.Migrator) missing although $key was chosen" }
            continue
        }
        if (Test-Path $migrator) { Fail "$($p.Migrator) was generated although $key was not chosen" } else { Pass "no $($p.Migrator)" }
        foreach ($f in $projectFiles) {
            $text = [IO.File]::ReadAllText($f.FullName)
            if ($text.Contains($p.Migrator)) { Fail "$($f.FullName.Substring($out.Length + 1)) still references $($p.Migrator)" }
            foreach ($package in $p.Packages) {
                if ($text -match ('Include="' + [regex]::Escape($package) + '"')) { Fail "$($f.FullName.Substring($out.Length + 1)) still references package $package" }
            }
        }
        # One deliberate exception: DatabaseSettingsValidationTests names the providers that were
        # NOT generated, as test cases, to assert that startup validation refuses them. The key is a
        # string constant every project keeps; there is no provider code behind it.
        $codeHits = @($codeFiles | Select-String -CaseSensitive -Pattern $p.Code |
            Where-Object { $_.Line -notmatch '^\s*(//|\*|///)' } |
            Where-Object { -not ($_.Path -like '*\DatabaseSettingsValidationTests.cs' -and $_.Line -match '^\s*\[TestCase\(DbProviderKeys\.\w+\)\]\s*$') })
        if ($codeHits.Count -gt 0) {
            foreach ($hit in $codeHits | Select-Object -First 10) { Fail "$key code left in $($hit.Path.Substring($out.Length + 1)):$($hit.LineNumber): $($hit.Line.Trim())" }
        }
        else { Pass "no $key provider code in src/ or tests/" }
    }
    if (Test-Path (Join-Path $out 'src\Migrators\Migrators.SqLite')) { Pass 'Migrators.SqLite present (always: the test harnesses run on SQLite)' }
    else { Fail 'Migrators.SqLite missing - the HTTP integration harness needs it whatever the provider' }

    Step 'no template conditional survived processing'
    $markers = @($files | Select-String -Pattern '#(if|elif) \(Use(SqlServer|PostgreSql)|<!--#(if|else|elif|endif)')
    if ($markers.Count -gt 0) { foreach ($m in $markers | Select-Object -First 10) { Fail "unprocessed marker in $($m.Path.Substring($out.Length + 1)):$($m.LineNumber)" } }
    else { Pass 'none' }

    Step 'wizard values reached appsettings.json'
    $appsettings = [IO.File]::ReadAllText((Join-Path $out 'src\Server.UI\appsettings.json'))
    $expectations = [ordered]@{
        "DBProvider is $Database"                           = ('"DBProvider": "' + $Database + '"')
        "business database is named $ProjectName"           = ('Database=' + $ProjectName + ';')
        "log database is named ${ProjectName}_Logs"         = ('Database=' + $ProjectName + '_Logs;')
        "DefaultTimeZone is $DefaultTimeZone"               = ('"DefaultTimeZone": "' + $DefaultTimeZone + '"')
        "AllowSelfRegistration is $AllowSelfRegistration"   = ('"AllowSelfRegistration": ' + $AllowSelfRegistration)
    }
    if ($Database -eq 'sqlite') {
        $expectations["business database is named $ProjectName"] = ('Data Source=' + $ProjectName + '.db')
        $expectations["log database is named ${ProjectName}_Logs"] = ('Data Source=' + $ProjectName + '_Logs.db')
    }
    foreach ($name in $expectations.Keys) {
        if ($appsettings.Contains($expectations[$name])) { Pass $name } else { Fail "$name - expected to find: $($expectations[$name])" }
    }
    if ($appsettings.Contains('GXApplication')) { Fail 'appsettings.json still names the GXApplication database' } else { Pass 'no GXApplication database name' }

    Step 'no Docker artefacts and no template-repository-only files'
    foreach ($name in 'Dockerfile', 'docker-compose.yml', 'docker-compose.override.yml', 'docker-compose.dcproj', '.dockerignore', 'launchSettings.json',
                      'GXTemplate-passes', 'tooling', 'Directory.Build.props', 'build', 'docs') {
        if (Test-Path (Join-Path $out $name)) { Fail "$name was generated" } else { Pass "no $name" }
    }
    $dockerHits = @($projectFiles | Select-String -Pattern 'Docker' -SimpleMatch)
    if ($dockerHits.Count -gt 0) { Fail "Docker still referenced in $($dockerHits[0].Path.Substring($out.Length + 1))" } else { Pass 'no Docker property in any project file' }

    Step "upstream attribution text does not carry '$ProjectName'"
    $attribution = @($files | Select-String -Pattern 'upstream|neozhu|Jason Taylor|CleanArchitectureWithBlazorServer' |
        Where-Object { $_.Line -match [regex]::Escape($ProjectName) })
    if ($attribution.Count -gt 0) { foreach ($a in $attribution) { Fail "$($a.Path.Substring($out.Length + 1)):$($a.LineNumber): $($a.Line.Trim())" } }
    else { Pass 'none' }

    # ------------------------------------------------------------------ 4. build
    if (-not $NoBuild) {
        Step 'the generated solution builds'
        $buildLog = Join-Path $WorkRoot 'build.log'
        $exit = Invoke-Dotnet @('build', (Join-Path $out "$ProjectName.slnx"), '-nologo') $buildLog
        $summary = Select-String -Path $buildLog -Pattern '^\s+\d+ Error\(s\)' | Select-Object -Last 1
        if ($exit -eq 0 -and $summary -and $summary.Line.Trim() -eq '0 Error(s)') { Pass "0 errors ($buildLog)" }
        else {
            Select-String -Path $buildLog -Pattern ': error ' | Select-Object -First 10 | ForEach-Object { Write-Host "        $($_.Line.Trim())" }
            Fail "build failed with exit code $exit - see $buildLog"
        }
    }
}
catch {
    Fail ("stopped: " + ($_ | Out-String).Trim())
}
finally {
    Write-Host ''
    if ($failures.Count -eq 0) { Write-Host "SMOKE PASSED ($Database)" -ForegroundColor Green }
    else { Write-Host "SMOKE FAILED ($Database): $($failures.Count) failure(s)" -ForegroundColor Red }

    if ($KeepOutput -or $failures.Count -gt 0) { Write-Host "Output kept at $WorkRoot" }
    else { Remove-Item -Recurse -Force $WorkRoot -ErrorAction SilentlyContinue }
}

if ($failures.Count -gt 0) { exit 1 }
exit 0
