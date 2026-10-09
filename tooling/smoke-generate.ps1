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
      3. `dotnet new gxblazor` generates - with the TEMPLATE'S DEFAULTS for every option not given
         on the command line - and the output:
           - carries no migrator, package, project reference or test code for a provider that was
             not chosen, and no test project that uses SQLite (pass 47: the suites run on PostgreSQL);
           - follows the GX configuration layout (Pass 45): appsettings.json is structure only, with
             every secret-bearing key present and empty; Staging and Production are committed with
             empty placeholders; appsettings.Development.json holds the local values - the chosen
             provider's shape, the project's database names, port 5434 and an EMPTY password;
           - writes DBProvider, DefaultTimeZone and AllowSelfRegistration as chosen (defaults:
             postgresql, Africa/Lagos, false);
           - carries Server.UI.csproj's IIS deployment settings and never publishes the local file;
           - has a UserSecretsId of its own: not the template's, and not the next project's either;
           - carries no template conditional left unprocessed, no Docker artefact and no
             template-repository-only file;
           - does not carry the project name inside upstream attribution text;
           - carries the outbound-address guard test, and no Seq sink, Gravatar address, MaxMind
             client, Google Fonts link or qrcodejs script in src/ (pass 48); the guard passes alone.
      4. The generated solution builds with 0 errors. Without GX_TEST_PG its four test suites FAIL,
         naming the variable; against the -TestServer (default GX_TEST_PG) they all pass, each on its
         own gx_test_<project>_* database.
      5. In a fresh `git init` of the generated folder, appsettings.Development.json is ignored.

    Generates into a SHORT path under %TEMP% on purpose: native assets can nest deeply
    enough under bin\ that a generated project in a long path fails to build with MSB3021
    (MAX_PATH), which is a property of the location, not of the template.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tooling\smoke-generate.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tooling\smoke-generate.ps1 -TestServer "Host=localhost;Port=5434;Username=postgres;Password=<yours>" -KeepOutput
#>
[CmdletBinding()]
param(
    # Each wizard option is passed to `dotnet new` only when given here. Left out, the template's
    # own default is what gets generated - and asserted.
    [ValidateSet('', 'postgresql', 'mssql', 'sqlite')]
    [string] $Database = '',

    [string] $ProjectName = 'SmokeApp',

    [string] $DefaultTimeZone = '',

    [ValidateSet('', 'true', 'false')]
    [string] $AllowSelfRegistration = '',

    # Where the package, the hive and the generated project go. Defaults to a fresh short folder
    # under %TEMP%, deleted afterwards unless -KeepOutput is given.
    [string] $WorkRoot = (Join-Path $env:TEMP ('gxsmoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))),

    [switch] $KeepOutput,

    # Skip building the generated solution (and so its tests) - for iterating on the content
    # assertions only.
    [switch] $NoBuild,

    # Build, but skip the generated project's test suites.
    [switch] $NoTests,

    # The PostgreSQL server the generated suites run against (pass 47): host, port, username and
    # password, and NO database - the same form as GX_TEST_PG, which is the default. Each suite
    # creates its own gx_test_<project>_* database there and never drops it. Never printed.
    [string] $TestServer = $env:GX_TEST_PG
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

# The template's defaults, which is what a generation that leaves an option out must produce.
$expectedDatabase = if ($Database) { $Database } else { 'postgresql' }
$expectedTimeZone = if ($DefaultTimeZone) { $DefaultTimeZone } else { 'Africa/Lagos' }
$expectedSelfRegistration = if ($AllowSelfRegistration) { $AllowSelfRegistration } else { 'false' }
$expectedDbName = $ProjectName -replace '[^A-Za-z0-9_]', ''

# The id the template's own Server.UI.csproj carries, which template.json "guids" must replace.
$templateUserSecretsId = [Guid]'8118d19e-a6db-4446-bdb6-fa62b17f843d'

# Keys whose last segment names a credential - the same rule as CommittedAppSettingsTests.
$secretBearing = '(ConnectionString|ApiKey|LicenseKey|Password|Secret|Token|AccountKey|SasKey)$'

# The keys a server must supply: present in appsettings.json, and empty there (G1).
$requiredStructure = @(
    'DatabaseSettings:ConnectionString', 'DatabaseSettings:LogConnectionString',
    'AppConfigurationSettings:ApplicationUrl',
    'Mail:FromAddress', 'Mail:ApiKey', 'Mail:Domain',
    'Storage:ConnectionString')

function Fail([string] $message) { $failures.Add($message); Write-Host "  FAIL  $message" -ForegroundColor Red }
function Pass([string] $message) { Write-Host "  ok    $message" -ForegroundColor Green }
function Step([string] $message) { Write-Host ''; Write-Host "== $message" -ForegroundColor Cyan }
function Check([bool] $condition, [string] $message, [string] $detail = '') {
    if ($condition) { Pass $message } else { Fail ($message + $(if ($detail) { " - $detail" } else { '' })) }
}

function Invoke-Native([string] $exe, [string[]] $arguments, [string] $logFile, [string] $workingDirectory = $null) {
    # Output to a log, not the console: a failing build is read from the log, and the console
    # stays readable. Returns the exit code.
    #
    # 'Continue' for the duration: Windows PowerShell turns every line a native command writes to
    # stderr into an ErrorRecord, and under 'Stop' the first one - even an empty line from a
    # successful `dotnet new` - aborts the script. The exit code is the verdict, not stderr.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    if ($workingDirectory) { Push-Location $workingDirectory }
    try { & $exe @arguments *> $logFile }
    finally {
        if ($workingDirectory) { Pop-Location }
        $ErrorActionPreference = $previous
    }
    return $LASTEXITCODE
}

function Invoke-Dotnet([string[]] $arguments, [string] $logFile) { return Invoke-Native 'dotnet' $arguments $logFile }

# Text files the template engine processes, relative to a root. Build output is never content.
function Get-ContentFiles([string] $root) {
    Get-ChildItem -Path $root -Recurse -File -Force |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git|\.vs|node_modules)\\' } |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.razor', '.cshtml', '.json', '.props', '.targets', '.slnx', '.md', '.yml', '.xml', '.config' }
}

# appsettings files carry // comments, which Windows PowerShell's ConvertFrom-Json rejects. Strips
# them outside string literals only - a URL inside a value keeps its //.
function ConvertFrom-JsonWithComments([string] $path) {
    $text = [IO.File]::ReadAllText($path)
    $sb = New-Object System.Text.StringBuilder
    $inString = $false
    $i = 0
    while ($i -lt $text.Length) {
        $c = $text[$i]
        if ($inString) {
            [void]$sb.Append($c)
            if ($c -eq [char]'\' -and $i + 1 -lt $text.Length) { [void]$sb.Append($text[$i + 1]); $i += 2; continue }
            if ($c -eq [char]'"') { $inString = $false }
            $i++; continue
        }
        if ($c -eq [char]'"') { $inString = $true; [void]$sb.Append($c); $i++; continue }
        if ($c -eq [char]'/' -and $i + 1 -lt $text.Length -and $text[$i + 1] -eq [char]'/') {
            while ($i -lt $text.Length -and $text[$i] -ne [char]"`n") { $i++ }
            continue
        }
        if ($c -eq [char]'/' -and $i + 1 -lt $text.Length -and $text[$i + 1] -eq [char]'*') {
            $end = $text.IndexOf('*/', $i + 2)
            $i = if ($end -lt 0) { $text.Length } else { $end + 2 }
            continue
        }
        [void]$sb.Append($c); $i++
    }
    return ($sb.ToString() | ConvertFrom-Json)
}

# Flattens parsed JSON to configuration-style keys ("Section:Key") mapped to leaf values.
function Get-Leaves($node, [string] $prefix, [hashtable] $into) {
    if ($node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($p in $node.PSObject.Properties) {
            $key = if ($prefix) { "${prefix}:$($p.Name)" } else { $p.Name }
            Get-Leaves $p.Value $key $into
        }
    }
    else { $into[$prefix] = $node }
}

function Read-Settings([string] $path) {
    $leaves = @{}
    Get-Leaves (ConvertFrom-JsonWithComments $path) '' $leaves
    return $leaves
}

function Get-UserSecretsId([string] $projectRoot) {
    $csproj = [xml][IO.File]::ReadAllText((Join-Path $projectRoot 'src\Server.UI\Server.UI.csproj'))
    $id = @($csproj.Project.PropertyGroup | ForEach-Object { $_.UserSecretsId } | Where-Object { $_ }) | Select-Object -First 1
    return $id
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
    # What the template instantiates: the repository minus its tooling, plus the files
    # .template.config/local-settings/ contributes (template.json's second source). The
    # maintainer's own src/Server.UI/appsettings.Development.json is excluded from generation, so
    # a literal found only there would be a false pass.
    $contentText = (Get-ContentFiles $repo |
        Where-Object { $_.FullName -notmatch '\\(GXTemplate-passes|docs|doc|build|tooling)\\' } |
        Where-Object { $_.FullName -notmatch '\\\.template\.config\\' -or $_.FullName -match '\\\.template\.config\\local-settings\\' } |
        Where-Object { $_.FullName -notmatch '\\src\\Server\.UI\\appsettings\.Development\.json$' } |
        ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
    foreach ($symbol in $templateJson.symbols.PSObject.Properties) {
        $literal = $symbol.Value.replaces
        if (-not $literal) { continue }
        if ($contentText.Contains($literal)) { Pass "$($symbol.Name) matches '$literal'" }
        else { Fail "$($symbol.Name): its replaces literal '$literal' occurs nowhere in the template content, so it replaces nothing" }
    }
    foreach ($guid in @($templateJson.guids | Where-Object { $_ })) {
        if ($contentText.Contains($guid)) { Pass "guids entry $guid occurs in the template content" }
        else { Fail "guids entry $guid occurs nowhere in the template content, so it replaces nothing" }
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
    $options = @()
    if ($Database) { $options += @('--Database', $Database) }
    if ($DefaultTimeZone) { $options += @('--DefaultTimeZone', $DefaultTimeZone) }
    if ($AllowSelfRegistration) { $options += @('--AllowSelfRegistration', $AllowSelfRegistration) }
    Step ("generate: -n $ProjectName " + $(if ($options) { $options -join ' ' } else { '(template defaults)' }))
    $generate = @('new', 'gxblazor', '-n', $ProjectName, '-o', $out) + $options + @('--debug:custom-hive', $hive)
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
        if ($key -eq $expectedDatabase) {
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
    # Pass 47: the test suites run on PostgreSQL only. Migrators.SqLite still ships in src/ until
    # pass 48 removes the provider, but no test project may reach it or SQLite any more.
    Step 'no test project uses SQLite (pass 47)'
    $testFiles = @($files | Where-Object { $_.FullName -like "$out\tests\*" })
    # Code only: comments may still say what the tests used to do.
    $sqliteInTests = @($testFiles | Select-String -Pattern 'Migrators\.SqLite|Microsoft\.Data\.Sqlite|UseSqlite\(|SqliteConnection' |
        Where-Object { $_.Line -notmatch '^\s*(//|\*|///|<!--)' })
    if ($sqliteInTests.Count -gt 0) { foreach ($s in $sqliteInTests | Select-Object -First 10) { Fail "SQLite in $($s.Path.Substring($out.Length + 1)):$($s.LineNumber): $($s.Line.Trim())" } }
    else { Pass "none in $($testFiles.Count) test files" }

    Step 'the test databases are named after the project'
    $expectedTestProject = ($ProjectName.ToLowerInvariant() -replace '[^a-z0-9_]', '')
    if ($expectedTestProject.Length -gt 40) { $expectedTestProject = $expectedTestProject.Substring(0, 40) }
    $namesFile = Join-Path $out 'tests\TestSupport\TestDatabaseNames.cs'
    $namesText = if (Test-Path $namesFile) { [IO.File]::ReadAllText($namesFile) } else { '' }
    Check ($namesText.Contains("public const string Project = `"$expectedTestProject`";")) "TestDatabaseNames.Project is '$expectedTestProject' (gx_test_${expectedTestProject}_*)" "not found in $namesFile"
    $tokenLeft = @($files | Select-String -SimpleMatch -Pattern 'cleanarchitectureblazor')
    if ($tokenLeft.Count -gt 0) { Fail "the test-database token survived in $($tokenLeft[0].Path.Substring($out.Length + 1)):$($tokenLeft[0].LineNumber)" }
    else { Pass 'no cleanarchitectureblazor token anywhere' }

    Step 'no template conditional survived processing'
    $markers = @($files | Select-String -Pattern '#(if|elif|elseif) \(Use(SqlServer|PostgreSql)|<!--#(if|else|elif|endif)|^\s*//#(if|else|elseif|elif|endif)')
    if ($markers.Count -gt 0) { foreach ($m in $markers | Select-Object -First 10) { Fail "unprocessed marker in $($m.Path.Substring($out.Length + 1)):$($m.LineNumber)" } }
    else { Pass 'none' }

    # ------------------------------------------------------------------ the GX configuration layout
    Step 'the four appsettings files exist, each in its role'
    $ui = Join-Path $out 'src\Server.UI'
    $settingsFiles = [ordered]@{}
    foreach ($name in 'appsettings.json', 'appsettings.Staging.json', 'appsettings.Production.json', 'appsettings.Development.json') {
        $path = Join-Path $ui $name
        if (Test-Path $path) { Pass "$name exists"; $settingsFiles[$name] = Read-Settings $path }
        else { Fail "$name was not generated" }
    }

    foreach ($name in 'appsettings.json', 'appsettings.Staging.json', 'appsettings.Production.json') {
        if (-not $settingsFiles.Contains($name)) { continue }
        $filled = @($settingsFiles[$name].GetEnumerator() | Where-Object { $_.Key -match $secretBearing -and "$($_.Value)" -ne '' } | ForEach-Object { $_.Key })
        Check ($filled.Count -eq 0) "$name (committed): every secret-bearing key is empty" ("carries a value in: " + ($filled -join ', '))
    }

    if ($settingsFiles.Contains('appsettings.json')) {
        $main = $settingsFiles['appsettings.json']
        foreach ($key in $requiredStructure) {
            Check ($main.ContainsKey($key) -and "$($main[$key])" -eq '') "appsettings.json keeps $key, empty" $(if ($main.ContainsKey($key)) { "value '$($main[$key])'" } else { 'key missing' })
        }
        Check ($main['DatabaseSettings:DBProvider'] -eq $expectedDatabase) "DBProvider is $expectedDatabase" "found '$($main['DatabaseSettings:DBProvider'])'"
        Check ($main['AppConfigurationSettings:DefaultTimeZone'] -eq $expectedTimeZone) "DefaultTimeZone is $expectedTimeZone" "found '$($main['AppConfigurationSettings:DefaultTimeZone'])'"
        Check ("$($main['AppConfigurationSettings:AllowSelfRegistration'])".ToLowerInvariant() -eq $expectedSelfRegistration) "AllowSelfRegistration is $expectedSelfRegistration" "found '$($main['AppConfigurationSettings:AllowSelfRegistration'])'"
        $mainText = [IO.File]::ReadAllText((Join-Path $ui 'appsettings.json'))
        Check ($mainText.Contains('STRUCTURE ONLY')) 'appsettings.json carries the header explaining where values come from'
    }

    foreach ($environment in 'Staging', 'Production') {
        $name = "appsettings.$environment.json"
        if (-not $settingsFiles.Contains($name)) { continue }
        Check ($settingsFiles[$name]['Serilog:Properties:Environment'] -eq $environment) "$name sets Serilog:Properties:Environment to $environment" "found '$($settingsFiles[$name]['Serilog:Properties:Environment'])'"
    }

    if ($settingsFiles.Contains('appsettings.Development.json')) {
        $dev = $settingsFiles['appsettings.Development.json']
        $business = "$($dev['DatabaseSettings:ConnectionString'])"
        $log = "$($dev['DatabaseSettings:LogConnectionString'])"
        switch ($expectedDatabase) {
            'postgresql' {
                foreach ($pair in @(@('business', $business, $expectedDbName), @('log', $log, "${expectedDbName}_Logs"))) {
                    $label, $value, $db = $pair
                    Check ($value -match '(^|;)Host=localhost(;|$)') "Development $label connection string: Host=localhost" "found '$value'"
                    Check ($value -match '(^|;)Port=5434(;|$)') "Development $label connection string: Port=5434" "found '$value'"
                    Check ($value -match ('(^|;)Database=' + [regex]::Escape($db) + '(;|$)')) "Development $label connection string: Database=$db" "found '$value'"
                    Check ($value -match '(^|;)Username=postgres(;|$)') "Development $label connection string: Username=postgres" "found '$value'"
                    Check ($value -match '(^|;)Password=(;|$)') "Development $label connection string: Password is EMPTY" "found '$value'"
                }
            }
            'mssql' {
                Check ($business -match ('(^|;)Database=' + [regex]::Escape($expectedDbName) + '(;|$)') -and $business -match 'Trusted_Connection=True') "Development business connection string: LocalDB, Database=$expectedDbName" "found '$business'"
                Check ($log -match ('(^|;)Database=' + [regex]::Escape("${expectedDbName}_Logs") + '(;|$)') -and $log -match 'Trusted_Connection=True') "Development log connection string: LocalDB, Database=${expectedDbName}_Logs" "found '$log'"
            }
            'sqlite' {
                Check ($business -eq "Data Source=$expectedDbName.db") "Development business connection string: Data Source=$expectedDbName.db" "found '$business'"
                Check ($log -eq "Data Source=${expectedDbName}_Logs.db") "Development log connection string: Data Source=${expectedDbName}_Logs.db" "found '$log'"
            }
        }
    }

    # GXTemplateDatabase is the token template.json replaces, so it must be gone from every file.
    # GXApplication is the name every project shared before Pass 44; the README still tells that
    # history, so it is looked for in the settings files only.
    $leftovers = @($files | Select-String -SimpleMatch -Pattern 'GXTemplateDatabase') +
                 @(Get-ChildItem $ui -Filter 'appsettings*.json' | Select-String -SimpleMatch -Pattern 'GXApplication')
    if ($leftovers.Count -gt 0) { foreach ($l in $leftovers | Select-Object -First 5) { Fail "template database name left in $($l.Path.Substring($out.Length + 1)):$($l.LineNumber)" } }
    else { Pass 'no GXTemplateDatabase token anywhere, and no GXApplication database in any settings file' }

    Step 'Server.UI.csproj carries the IIS deployment settings'
    $csproj = [xml][IO.File]::ReadAllText((Join-Path $ui 'Server.UI.csproj'))
    $properties = @($csproj.Project.PropertyGroup)
    $rid = @($properties | ForEach-Object { $_.RuntimeIdentifier } | Where-Object { $_ }) | Select-Object -First 1
    $offline = @($properties | ForEach-Object { $_.EnableMSDeployAppOffline } | Where-Object { $_ }) | Select-Object -First 1
    Check ($rid -eq 'win-x64') 'RuntimeIdentifier is win-x64' "found '$rid'"
    Check ($offline -eq 'true') 'EnableMSDeployAppOffline is true' "found '$offline'"
    $skip = @($csproj.SelectNodes("//MsDeploySkipRules[@Include='SkipWebConfig']"))
    Check ($skip.Count -eq 1 -and $skip[0].ObjectName -eq 'filePath' -and $skip[0].AbsolutePath -eq 'web\.config$') 'MsDeploySkipRules SkipWebConfig: ObjectName filePath, AbsolutePath web\.config$'
    $never = @($csproj.SelectNodes("//Content[@Update='appsettings.Development.json']"))
    Check ($never.Count -eq 1 -and $never[0].CopyToPublishDirectory -eq 'Never') 'Content Update="appsettings.Development.json" CopyToPublishDirectory="Never"'

    Step 'appsettings.Development.json is excluded from publish (evaluated item metadata)'
    $itemsLog = Join-Path $WorkRoot 'content-items.json'
    if ((Invoke-Dotnet @('msbuild', (Join-Path $ui 'Server.UI.csproj'), '-getItem:Content', '-nologo') $itemsLog) -ne 0) {
        Fail "dotnet msbuild -getItem:Content failed - see $itemsLog"
    }
    else {
        $content = @(([IO.File]::ReadAllText($itemsLog) | ConvertFrom-Json).Items.Content)
        $devItem = @($content | Where-Object { $_.Identity -eq 'appsettings.Development.json' })
        $mainItem = @($content | Where-Object { $_.Identity -eq 'appsettings.json' })
        Check ($mainItem.Count -eq 1 -and $mainItem[0].CopyToPublishDirectory -ne 'Never') 'control: appsettings.json is publishable content' "found '$($mainItem[0].CopyToPublishDirectory)'"
        Check ($devItem.Count -eq 1 -and $devItem[0].CopyToPublishDirectory -eq 'Never') 'appsettings.Development.json has CopyToPublishDirectory=Never' $(if ($devItem.Count -eq 1) { "found '$($devItem[0].CopyToPublishDirectory)'" } else { "$($devItem.Count) items" })
    }

    Step 'the project has a UserSecretsId of its own'
    $secretsId = Get-UserSecretsId $out
    $parsed = [Guid]::Empty
    Check ([Guid]::TryParse($secretsId, [ref]$parsed)) "UserSecretsId is a GUID ($secretsId)"
    Check ($parsed -ne $templateUserSecretsId) "UserSecretsId differs from the template's $templateUserSecretsId" "found '$secretsId'"
    $twin = Join-Path $WorkRoot 'twin'
    if ((Invoke-Dotnet (@('new', 'gxblazor', '-n', $ProjectName, '-o', $twin) + $options + @('--debug:custom-hive', $hive)) (Join-Path $WorkRoot 'generate-twin.log')) -ne 0) {
        Fail "the second generation failed - see $(Join-Path $WorkRoot 'generate-twin.log')"
    }
    else {
        $twinId = Get-UserSecretsId $twin
        Check ($twinId -and $twinId -ne $secretsId) "a second generation of the same name gets a different UserSecretsId ($twinId)" "both are '$secretsId'"
        Remove-Item -Recurse -Force $twin -ErrorAction SilentlyContinue
    }
    $idHits = @($files | Select-String -SimpleMatch -Pattern $templateUserSecretsId.ToString())
    Check ($idHits.Count -eq 0) "the template's UserSecretsId occurs nowhere in the output" $(if ($idHits) { "$($idHits[0].Path.Substring($out.Length + 1)):$($idHits[0].LineNumber)" })

    Step 'no Docker artefacts and no template-repository-only files'
    foreach ($name in 'Dockerfile', 'docker-compose.yml', 'docker-compose.override.yml', 'docker-compose.dcproj', '.dockerignore', 'launchSettings.json',
                      'GXTemplate-passes', 'tooling', 'Directory.Build.props', 'build', 'docs', '.template.config') {
        if (Test-Path (Join-Path $out $name)) { Fail "$name was generated" } else { Pass "no $name" }
    }
    $dockerHits = @($projectFiles | Select-String -Pattern 'Docker' -SimpleMatch)
    if ($dockerHits.Count -gt 0) { Fail "Docker still referenced in $($dockerHits[0].Path.Substring($out.Length + 1))" } else { Pass 'no Docker property in any project file' }

    Step "upstream attribution text does not carry '$ProjectName'"
    $attribution = @($files | Select-String -Pattern 'upstream|neozhu|Jason Taylor|CleanArchitectureWithBlazorServer' |
        Where-Object { $_.Line -match [regex]::Escape($ProjectName) })
    if ($attribution.Count -gt 0) { foreach ($a in $attribution) { Fail "$($a.Path.Substring($out.Length + 1)):$($a.LineNumber): $($a.Line.Trim())" } }
    else { Pass 'none' }

    # Pass 48: the upstream Seq sink, the seeded Gravatar pictures and every other default call-out
    # stay gone, and the guard that keeps them gone ships with the project. src/ only: the guard
    # test itself names the Seq host and Gravatar in its explanation.
    Step 'nothing in the generated source calls out, and the guard ships'
    $guard = @(Get-ChildItem -Path (Join-Path $out 'tests') -Recurse -File -Filter 'OutboundAddressTests.cs')
    Check ($guard.Count -eq 1) 'OutboundAddressTests.cs is generated' "found $($guard.Count)"
    $srcFiles = @($files | Where-Object { $_.FullName.StartsWith((Join-Path $out 'src') + '\') })
    foreach ($probe in @(
            @{ Name = 'Seq sink'; Pattern = 'WriteTo\.Seq|Serilog\.Sinks\.Seq|seq\.blazorserver' },
            @{ Name = 'Gravatar address'; Pattern = 'gravatar\.com' },
            @{ Name = 'MaxMind GeoIP client'; Pattern = 'MaxMind|GeoIP2' },
            @{ Name = 'Google Fonts link or qrcodejs CDN script'; Pattern = 'fonts\.googleapis|qrcodejs' })) {
        $hits = @($srcFiles | Select-String -Pattern $probe.Pattern)
        Check ($hits.Count -eq 0) "no $($probe.Name) in src ($($srcFiles.Count) files)" $(if ($hits) { "$($hits[0].Path.Substring($out.Length + 1)):$($hits[0].LineNumber)" })
    }

    # ------------------------------------------------------------------ 4. build and test
    if (-not $NoBuild) {
        Step 'the generated solution builds'
        $buildLog = Join-Path $WorkRoot 'build.log'
        $exit = Invoke-Dotnet @('build', (Join-Path $out "$ProjectName.slnx"), '-nologo') $buildLog
        $summary = Select-String -Path $buildLog -Pattern '^\s+\d+ Error\(s\)' | Select-Object -Last 1
        $built = $exit -eq 0 -and $summary -and $summary.Line.Trim() -eq '0 Error(s)'
        if ($built) { Pass "0 errors ($buildLog)" }
        else {
            Select-String -Path $buildLog -Pattern ': error ' | Select-Object -First 10 | ForEach-Object { Write-Host "        $($_.Line.Trim())" }
            Fail "build failed with exit code $exit - see $buildLog"
        }

        # Before `git init` below, on purpose: this is the state `dotnet new` leaves a project in,
        # and CommittedAppSettingsTests has a path for a folder that is not a repository yet.
        if ($built -and -not $NoTests) {
            $suites = 'Application.UnitTests', 'Infrastructure.UnitTests', 'Application.IntegrationTests', 'Server.UI.IntegrationTests'
            $savedServer = $env:GX_TEST_PG
            try {
                # Pass 48. Run alone and without a server, so its result is its own and not hidden
                # in a suite total: it reads files, never a database.
                Step 'the outbound-address guard passes in the generated project'
                Remove-Item Env:GX_TEST_PG -ErrorAction SilentlyContinue
                $testLog = Join-Path $WorkRoot 'test-outbound-guard.log'
                $exit = Invoke-Dotnet @('test', (Join-Path $out 'tests\Application.UnitTests'), '--no-build', '-nologo', '--filter', 'FullyQualifiedName~OutboundAddressTests') $testLog
                $line = Select-String -Path $testLog -Pattern '^(Passed|Failed|Skipped)!' | Select-Object -Last 1
                $text = if ($line) { $line.Line.Trim() -replace '\s+', ' ' } else { 'no summary line' }
                Check ($exit -eq 0 -and $line -and $line.Line -match '^Passed!.*Passed:\s+3,') "OutboundAddressTests: $text" "exit code $exit - see $testLog"

                # Without a server, every suite must FAIL and say why - never skip, never pass on a
                # fallback (pass 47). Only the database tests fail; the summary must not be Passed.
                Step 'without GX_TEST_PG the generated suites fail loud'
                Remove-Item Env:GX_TEST_PG -ErrorAction SilentlyContinue
                foreach ($suite in $suites) {
                    $testLog = Join-Path $WorkRoot "test-noserver-$suite.log"
                    $exit = Invoke-Dotnet @('test', (Join-Path $out "tests\$suite"), '--no-build', '-nologo') $testLog
                    $line = Select-String -Path $testLog -Pattern '^(Passed|Failed|Skipped)!' | Select-Object -Last 1
                    $text = if ($line) { $line.Line.Trim() -replace '\s+', ' ' } else { 'no summary line' }
                    $named = [bool](Select-String -Path $testLog -SimpleMatch -Pattern 'GX_TEST_PG is not set' -Quiet)
                    Check ($exit -ne 0 -and $line -and $line.Line -match '^Failed!' -and $named) "${suite}: $text, naming GX_TEST_PG" "exit code $exit, named: $named - see $testLog"
                }

                Step 'the test suites pass against the GX_TEST_PG server'
                if (-not $TestServer) {
                    Fail 'no test server: pass -TestServer or set GX_TEST_PG (host, port, username, password; no database)'
                }
                else {
                    $env:GX_TEST_PG = $TestServer
                    foreach ($suite in $suites) {
                        $testLog = Join-Path $WorkRoot "test-$suite.log"
                        $exit = Invoke-Dotnet @('test', (Join-Path $out "tests\$suite"), '--no-build', '-nologo') $testLog
                        $line = Select-String -Path $testLog -Pattern '^(Passed|Failed|Skipped)!' | Select-Object -Last 1
                        $text = if ($line) { $line.Line.Trim() -replace '\s+', ' ' } else { 'no summary line' }
                        if ($exit -eq 0 -and $line -and $line.Line -match '^Passed!') { Pass "${suite}: $text" }
                        else {
                            Select-String -Path $testLog -Pattern '^\s+Failed ' | Select-Object -First 10 | ForEach-Object { Write-Host "        $($_.Line.Trim())" }
                            Fail "${suite}: $text (exit code $exit) - see $testLog"
                        }
                    }
                }
            }
            finally {
                if ($null -ne $savedServer) { $env:GX_TEST_PG = $savedServer } else { Remove-Item Env:GX_TEST_PG -ErrorAction SilentlyContinue }
            }
        }
    }

    # ------------------------------------------------------------------ 5. gitignored
    Step 'appsettings.Development.json is gitignored in a fresh repository'
    # The user's global excludes file is switched off, so the answer is the generated .gitignore's.
    $noGlobal = Join-Path $WorkRoot 'no-global-excludes'
    if ((Invoke-Native 'git' @('init', '--quiet') (Join-Path $WorkRoot 'git-init.log') $out) -ne 0) {
        Fail "git init failed in $out"
    }
    else {
        $exit = Invoke-Native 'git' @('-c', "core.excludesFile=$noGlobal", 'check-ignore', '--quiet', 'src/Server.UI/appsettings.Development.json') (Join-Path $WorkRoot 'git-check-ignore.log') $out
        Check ($exit -eq 0) 'git check-ignore src/Server.UI/appsettings.Development.json: ignored' "exit code $exit (1 means NOT ignored)"
        foreach ($committed in 'appsettings.json', 'appsettings.Staging.json', 'appsettings.Production.json') {
            $exit = Invoke-Native 'git' @('-c', "core.excludesFile=$noGlobal", 'check-ignore', '--quiet', "src/Server.UI/$committed") (Join-Path $WorkRoot 'git-check-ignore.log') $out
            Check ($exit -eq 1) "control: $committed is NOT ignored" "exit code $exit"
        }
    }
}
catch {
    Fail ("stopped: " + ($_ | Out-String).Trim())
}
finally {
    Write-Host ''
    if ($failures.Count -eq 0) { Write-Host "SMOKE PASSED ($expectedDatabase)" -ForegroundColor Green }
    else { Write-Host "SMOKE FAILED ($expectedDatabase): $($failures.Count) failure(s)" -ForegroundColor Red }

    if ($KeepOutput -or $failures.Count -gt 0) { Write-Host "Output kept at $WorkRoot" }
    else { Remove-Item -Recurse -Force $WorkRoot -ErrorAction SilentlyContinue }
}

if ($failures.Count -gt 0) { exit 1 }
exit 0
