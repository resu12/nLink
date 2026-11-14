namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferPinnedPortableFreshnessTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task PinnedPortable_RequiresExactArtifactAndUnchangedBuildInputs(string host)
    {
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'Harness syntax errors'}
            foreach($name in @('Assert-Phase5PinnedPortableInputs','Assert-Phase5RouteAcceptanceExecutableFresh')){
                $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$false)|Where-Object Name -eq $name)
                if($definition.Count -ne 1){throw 'Freshness function missing or duplicated'}
                . ([scriptblock]::Create($definition[0].Extent.Text))
            }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $scratch=Join-Path ([IO.Path]::GetTempPath()) ('nlink-pinned-portable-'+[guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($scratch)
            try {
                foreach($case in @('exact','analyzer_delta','harness_delta','test_delta','default_fresh','default_stale',
                    'partial_binding','wrong_archive_hash','wrong_exe_hash','wrong_source','production_delta',
                    'build_delta','untracked_input','member_changed','member_missing','extra_member','archive_traversal','duplicate_member')){
                    $root=Join-Path $scratch $case; [void][IO.Directory]::CreateDirectory($root)
                    $repo=Join-Path $root 'repo'; [void][IO.Directory]::CreateDirectory((Join-Path $repo 'src'))
                    [void][IO.Directory]::CreateDirectory((Join-Path $repo 'build'))
                    [void][IO.Directory]::CreateDirectory((Join-Path $repo 'tools'))
                    [IO.File]::WriteAllText((Join-Path $repo 'src/app.cs'),'original')
                    [IO.File]::WriteAllText((Join-Path $repo 'build/package.ps1'),'original')
                    [IO.File]::WriteAllText((Join-Path $repo 'tools/Run-FileTransferRouteAcceptance.ps1'),'original')
                    [IO.File]::WriteAllText((Join-Path $repo 'VERSION'),'0.7.0')
                    & git -C $repo init -q
                    & git -C $repo add .
                    & git -C $repo -c user.name=Fixture -c user.email=fixture@invalid commit -q -m fixture
                    if($LASTEXITCODE -ne 0){throw 'Fixture Git setup failed'}
                    $source=(& git -C $repo rev-parse HEAD).Trim()
                    $portable=Join-Path $root 'portable'; [void][IO.Directory]::CreateDirectory($portable)
                    $exe=Join-Path $portable 'nLink.exe'
                    [IO.File]::WriteAllText($exe,'synthetic-not-executable')
                    [IO.File]::WriteAllText((Join-Path $portable 'appsettings.json'),'original')
                    $zip=Join-Path $root 'portable.zip'
                    [IO.Compression.ZipFile]::CreateFromDirectory($portable,$zip)
                    $zipHash=(Get-FileHash $zip).Hash; $exeHash=(Get-FileHash $exe).Hash
                    (Get-Item $exe).LastWriteTimeUtc=[DateTime]::UtcNow.AddDays(-2)
                    $expected=$case -in @('exact','analyzer_delta','harness_delta','test_delta','default_fresh')
                    switch($case){
                        'analyzer_delta' {[IO.File]::WriteAllText((Join-Path $repo 'tools/FileTransferFrontierGraceHandoff.ps1'),'analyzer')}
                        'harness_delta' {[IO.File]::WriteAllText((Join-Path $repo 'tools/Run-FileTransferRouteAcceptance.ps1'),'harness')}
                        'test_delta' {$test=Join-Path $repo 'tests/nLink.SmokeTests.Core/FileTransferPinnedPortableFreshnessTests.cs';[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($test));[IO.File]::WriteAllText($test,'test')}
                        'default_fresh' {(Get-Item $exe).LastWriteTimeUtc=[DateTime]::UtcNow.AddMinutes(1)}
                        'partial_binding' {$zipHash=''}
                        'wrong_archive_hash' {$zipHash='0'*64}
                        'wrong_exe_hash' {$exeHash='0'*64}
                        'wrong_source' {$source='0'*40}
                        'production_delta' {[IO.File]::WriteAllText((Join-Path $repo 'src/app.cs'),'changed')}
                        'build_delta' {[IO.File]::WriteAllText((Join-Path $repo 'build/package.ps1'),'changed')}
                        'untracked_input' {[IO.File]::WriteAllText((Join-Path $repo 'src/new.cs'),'new')}
                        'member_changed' {[IO.File]::WriteAllText((Join-Path $portable 'appsettings.json'),'modified')}
                        'member_missing' {[IO.File]::Delete((Join-Path $portable 'appsettings.json'))}
                        'extra_member' {[IO.File]::WriteAllText((Join-Path $portable 'extra.dll'),'extra')}
                        {$_ -in @('archive_traversal','duplicate_member')} {
                            $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Update)
                            try{[void]$archive.CreateEntry($(if($case -eq 'archive_traversal'){'../outside'}else{'nLink.exe'}))}finally{$archive.Dispose()}
                            $zipHash=(Get-FileHash $zip).Hash
                        }
                    }
                    $args=@{RepoRoot=$repo;RunRoot=$root;ResolvedExePath=$exe}
                    if($case -notin @('default_fresh','default_stale')){
                        $args.SourceCommit=$source;$args.ArchivePath=$zip;$args.ArchiveSha256=$zipHash;$args.ExecutableSha256=$exeHash
                    }
                    $accepted=$false
                    try{Assert-Phase5RouteAcceptanceExecutableFresh @args; $accepted=$true}catch{if($expected){throw}}
                    if($accepted -ne $expected){throw "Freshness parity mismatch: $case"}
                    if($accepted -and $case -ne 'default_fresh'){
                        $receipt=Get-Content (Join-Path $root 'phase5-portable-freshness-preflight.json') -Raw|ConvertFrom-Json
                        if($receipt.reason -cne 'pinned_portable_unchanged_application_build_inputs' -or $receipt.pinnedMemberCount -ne 2 -or $receipt.pinnedSourceCommit -cne $source){throw 'Pinned receipt mismatch'}
                    }
                    "pass $case"
                }
            }
            finally {
                $full=[IO.Path]::GetFullPath($scratch)
                $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
                if(-not $full.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture cleanup scope invalid'}
                Remove-Item -LiteralPath $full -Recurse -Force
            }
            "host=$($PSVersionTable.PSVersion)"
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("pass duplicate_member", result.Stdout, StringComparison.Ordinal);
    }
}
