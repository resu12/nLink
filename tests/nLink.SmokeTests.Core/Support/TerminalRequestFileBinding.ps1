param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($env:NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF -ceq '1' -and
    ($env:NLINK_TEST_SELECTED_NATIVE_WRITER -cne '1' -or $env:NLINK_TEST_SELECTED_ORIGIN_JOIN -ceq '1')) {throw 'authenticated_payload_profile_scope'}
if($null -ne ('NLink.Tools.LocalInputReadLease' -as [type]) -or
    $null -ne ('NLink.Tests.HeldFileNativeParent' -as [type])) {throw 'Terminal test types already loaded'}
# Use the real loader and native sources, including the processor's explicitly
# guarded metadata-clock and membership-position capture. Add test partials only in isolation.
$loader=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1'))
$needle='Add-Type -Path @('
if($loader.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) {throw 'Native loader seam changed'}
$extra=@('HeldFileNativeParent.cs','TerminalRequestFileBinding.cs','TerminalPrimaryFileHandoff.cs','TerminalCollectorRead.cs','TerminalCollectorFinalizer.cs','TerminalCollectorOutputs.cs','TerminalCollectorPrimary.cs','TerminalCollectorOrigins.cs','TerminalCollectorMembership.cs','TerminalCaptureTiming.cs','TerminalCollectorMembershipOrigins.cs','TerminalPayloadJoin.cs') | ForEach-Object {
    "'"+(Join-Path $PSScriptRoot $_).Replace("'","''")+"',"
}
$loader=$loader.Replace($needle,$needle+"`n"+($extra -join "`n"))
$loader=$loader.Replace('/define:NLINK_FRONTIER_LEDGER','/define:NLINK_FRONTIER_LEDGER,NLINK_TEST_TERMINAL_REQUEST')
if($env:NLINK_TEST_NATIVE_WITNESS_ADAPTER -ceq '1') {
    $adapter="'"+(Join-Path $PSScriptRoot 'NativeWitnessWriterAdapter.cs').Replace("'","''")+"',"
    $loader=$loader.Replace($needle,$needle+"`n"+$adapter)
    $loader=$loader.Replace('/define:NLINK_FRONTIER_LEDGER,NLINK_TEST_TERMINAL_REQUEST',
        '/define:NLINK_FRONTIER_LEDGER,NLINK_TEST_TERMINAL_REQUEST,NLINK_TEST_NATIVE_WITNESS_ADAPTER')
}
if($env:NLINK_TEST_SELECTED_NATIVE_WRITER -ceq '1') {
    if($env:NLINK_TEST_NATIVE_WITNESS_ADAPTER -cne '1' -or $env:NLINK_TEST_MEMBERSHIP_BORROWER -cne '1' -or
        $env:NLINK_TEST_COLLECTOR_PRIMARY -cne '1' -or $env:NLINK_TEST_NATIVE_PRIMARY_FILES -ceq '1' -or
        $env:NLINK_TEST_COLLECTOR_MEMBERSHIP -ceq '1' -or $env:NLINK_TEST_TERMINAL_PAYLOAD_JOIN -ceq '1' -or
        $env:NLINK_TEST_PRE_VERDICT_INTEGRITY -ceq '1' -or $env:NLINK_PUBLICATION_TERMINAL_SOURCE_PROBE -ceq '1') {
        throw 'selected_native_writer_profile_scope'
    }
    $selected=@('SelectedNativeWriter.cs','SelectedMembershipBorrower.cs','SelectedCollectorOrigin.cs') | ForEach-Object {
        "'"+(Join-Path $PSScriptRoot $_).Replace("'","''")+"',"
    }
    $loader=$loader.Replace($needle,$needle+"`n"+($selected -join "`n"))
    $loader=$loader.Replace('/define:NLINK_FRONTIER_LEDGER,NLINK_TEST_TERMINAL_REQUEST,NLINK_TEST_NATIVE_WITNESS_ADAPTER',
        '/define:NLINK_FRONTIER_LEDGER,NLINK_TEST_TERMINAL_REQUEST,NLINK_TEST_NATIVE_WITNESS_ADAPTER,NLINK_TEST_SELECTED_NATIVE_WRITER,NLINK_TEST_MEMBERSHIP_BORROWER')
    if($env:NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF -ceq '1') {
        $loader=$loader.Replace($needle,$needle+"`n'"+(Join-Path $PSScriptRoot 'SelectedPayloadOwner.cs').Replace("'","''")+"',")
        $loader=$loader.Replace($needle,$needle+"`n'"+(Join-Path $PSScriptRoot 'SelectedTerminalJoin.cs').Replace("'","''")+"',")
        $loader=$loader.Replace('NLINK_TEST_SELECTED_NATIVE_WRITER,NLINK_TEST_MEMBERSHIP_BORROWER',
            'NLINK_TEST_SELECTED_NATIVE_WRITER,NLINK_TEST_MEMBERSHIP_BORROWER,NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF')
    }
}
$loader=$loader.Replace('$PSScriptRoot',("'"+(Join-Path $RepoRoot 'tools/FileTransferOps').Replace("'","''")+"'"))
. ([scriptblock]::Create($loader))
