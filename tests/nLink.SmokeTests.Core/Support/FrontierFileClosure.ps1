param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Deterministic read-order model of the exact production closure method, not a
# native custody fixture. Separate real-child tests exercise the native protocol.
$source=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterFrontierFiles.cs'))
$method=[regex]::Match($source,'(?s)private bool FileChannelsCurrent\(\).*?(?=\s+internal bool FileSubmissionAvailable)')
if(-not $method.Success) {throw 'Original closure method missing'}
$model=@'
using System;
public sealed class ClosureReadOrderModel {
    class LocalFrontierFileChannel {
        public const int Ready = 0, Acknowledged = 5;
        public int State = 4;
        public bool IsCurrent = true;
    }
    class NativeRequest {
        public bool Open = true, Completed;
        public Action OnOpen;
        public bool GuiAttachmentOpen { get { if (OnOpen != null) OnOpen(); return Open; } }
        public bool GuiAttachmentCompleted { get { return Completed; } }
    }
    class ExitHandle {
        public Action OnWait;
        public bool Exited;
        public int SafeWaitHandle = 0;
        public bool WaitOne(int unused) { if (OnWait != null) OnWait(); return Exited; }
    }
    class Launch { public ExitHandle Exit = new ExitHandle(); }
    class Request { public NativeRequest native = new NativeRequest(); public Launch launch = new Launch(); }
    LocalFrontierFileChannel[] fileChannels = {new LocalFrontierFileChannel(), new LocalFrontierFileChannel()};
    Request[] fileRequests = {new Request(), new Request()};
    int exitCode;
    bool GetExitCodeProcess(int unused, out int code) {code = exitCode; return true;}
    void CompleteFirst() {
        // Actual protocol order: acknowledgment precedes attachment completion,
        // which precedes successful process exit. No state is reset/revived.
        fileChannels[0].State = LocalFrontierFileChannel.Acknowledged;
        fileRequests[0].native.Open = false;
        fileRequests[0].native.Completed = true;
    }
    public static bool Check(string mode) {
        var model = new ClosureReadOrderModel();
        if (mode == "attachment-transition") model.fileRequests[0].native.OnOpen = model.CompleteFirst;
        if (mode == "exit-transition") model.fileRequests[0].launch.Exit.OnWait = () => {
            model.CompleteFirst(); model.fileRequests[0].launch.Exit.Exited = true;
        };
        if (mode == "missing-ack") {model.fileRequests[0].native.Open = false; model.fileRequests[0].native.Completed = true;}
        if (mode == "failed-exit") {model.CompleteFirst(); model.fileRequests[0].launch.Exit.Exited = true; model.exitCode = 1;}
        if (mode == "cancelled") model.fileChannels[0].IsCurrent = false;
        return model.FileChannelsCurrent();
    }
'@
Add-Type -TypeDefinition ($model+$method.Value+"`n}")
# Preserve the pre-fix cached reads as a negative control. Both valid protocol
# transitions must reproduce its rejection without changing production files.
$before=$method.Value.Replace('int code;', 'int state = channel.State, code;').Replace('channel.State > LocalFrontierFileChannel.Ready', 'state > LocalFrontierFileChannel.Ready').Replace('request.native.GuiAttachmentCompleted && channel.State == LocalFrontierFileChannel.Acknowledged', 'state == LocalFrontierFileChannel.Acknowledged && request.native.GuiAttachmentCompleted').Replace('channel.State != LocalFrontierFileChannel.Acknowledged', 'state != LocalFrontierFileChannel.Acknowledged')
if($before -eq $method.Value) {throw 'Pre-fix negative control was not constructed'}
Add-Type -TypeDefinition ($model.Replace('ClosureReadOrderModel','PreFixClosureReadOrderModel')+$before+"`n}")
$failures=@(foreach($mode in @('attachment-transition','exit-transition','missing-ack','failed-exit','cancelled')) {
    $expected=$mode.EndsWith('-transition')
    if([ClosureReadOrderModel]::Check($mode) -ne $expected) {$mode}
    if([PreFixClosureReadOrderModel]::Check($mode)) {'pre-fix-'+$mode}
})
if($failures.Count) {throw ('Closure read-order failure: '+($failures -join ', '))}
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierExitSignalWait.ps1')
'closure-read-order-ok'
