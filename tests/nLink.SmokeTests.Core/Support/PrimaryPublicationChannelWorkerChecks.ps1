param([string]$Mode,$Custody,$Evaluations,$Context,$Prepared)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
foreach($name in @('Custody','Evaluations','Context','Prepared')) {
    Set-Variable -Name $name -Option Private -Value (Get-Variable -Name $name -ValueOnly)
}
$private:completion=$null
try {
    switch($Mode) {
        'channel-cancel-before' { $Custody.CancelPublication() }
        'channel-native-cancel' { $Context.NativeOwner.Cancel() }
        'channel-source-cancel' { $Context.SourceReader.Dispose() }
        'channel-writer-cancel' { $Context.WriterReader.Dispose() }
        'channel-worker-cancel' { $Context.SourceOwner.Dispose() }
        'channel-context-copy' { $Context=$Context.PSObject.Copy() }
        'channel-early-take' {
            $ready=[Threading.EventWaitHandle]::OpenExisting($env:NLINK_RETAINED_SOURCE_REQUEST+'.test-ready')
            $release=[Threading.EventWaitHandle]::OpenExisting($env:NLINK_RETAINED_SOURCE_REQUEST+'.test-release')
            try { [void]$ready.Set(); if(-not $release.WaitOne(20000)) { throw 'Parent did not freeze live channel' } }
            finally { $ready.Dispose(); $release.Dispose() }
        }
    }
    if($Mode -cne 'channel-no-completion') {
        $private:completion=New-FileTransferPrimaryPublicationCompletion $Custody $Evaluations $Context $Prepared
    }
    $expected=$Mode -cnotin @('channel-no-completion','channel-cancel-before','channel-native-cancel','channel-source-cancel',
        'channel-writer-cancel','channel-worker-cancel','channel-context-copy','channel-early-take','channel-publish-busy')
    if(($null -ne $private:completion) -ne $expected) { throw "Channel issuance drift: $Mode : $($Context.PublicationCompletionOmissionReason)" }
    $facts=@{Issued=$expected;OptionalMilliseconds=$Context.Work.ElapsedMilliseconds}
    if($expected) {
        $receipt=$private:completion.Completion
        $json=[Text.Encoding]::ASCII.GetString($private:completion.CopyPayload()); $body=$json | ConvertFrom-Json
        if($body.request.format -cne 'nlink-local-publication-producer-request-v1' -or
            $body.request.outputNamespace -cne ($env:NLINK_RETAINED_SOURCE_REQUEST+'.publication') -or
            (ConvertTo-FileTransferLocalCarrierJson $body) -cne $json -or
            $receipt.RequestSha256 -cne (Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $body.request)) -or
            $receipt.PayloadSha256 -cne (Get-FileTransferLocalCarrierHash $json) -or
            $receipt.InputDescriptorsSha256 -cne (Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $body.inputs)) -or
            $receipt.SelectedRowsSha256 -cne (Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $body.selection)) -or
            $receipt.CatalogSha256 -cne (Get-FileTransferLocalCarrierHash (ConvertTo-FileTransferLocalCarrierJson $body.inputs.catalog)) -or
            (ConvertTo-FileTransferLocalCarrierJson $body.request.writer) -cne (ConvertTo-FileTransferLocalCarrierJson $body.inputs.writer)) {
            throw 'Parent-bound producer request or independent completion digest changed'
        }
        $again=$Custody.TryPreparePublication($Evaluations,$Context,$Context.FullLines,$Context.CompactLines,
            $Context.FullView,$Context.CompactView,$Context.NativeOwner,$Context.NativeInputs,$Context.WriterReader,
            $Prepared.FullRowJson,$Prepared.CompactRowJson,$Prepared.WitnessJson,$Prepared.ProvenanceJson,'{}',1000)
        if($null -ne $again -or -not $Custody.OwnsPublication($private:completion)) { throw 'Channel issuance replay changed original ownership' }
        $facts.PayloadSha256=$receipt.PayloadSha256; $facts.PayloadBytes=$receipt.PayloadBytes
        $facts.FullInvocationId=$receipt.FullInvocationId.ToString('D'); $facts.CompactInvocationId=$receipt.CompactInvocationId.ToString('D')
        if($Mode -ceq 'channel-revoke-after') { $Custody.CancelPublication() }
        if($Mode -ceq 'channel-abandoned-gate') {
            # Retain a test-only mutex until this exact process exits. The parent
            # must reject abandonment, not treat the recovered gate as readiness.
            $script:abandonedPublicationGate=[Threading.Mutex]::OpenExisting($env:NLINK_RETAINED_SOURCE_REQUEST+'.publication.gate')
            if(-not $script:abandonedPublicationGate.WaitOne(0)) { throw 'Test abandonment gate unavailable' }
        }
    }
    return $facts
} finally {
    $Custody.Dispose()
    if($null -ne $private:completion -and $null -ne $private:completion.CopyPayload()) { throw 'Local payload survived cleanup' }
}
