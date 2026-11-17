param([string]$RepoRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$type=[NLink.Tools.LocalInputReadLease].Assembly.GetType('NLink.Tools.FrontierLedger.GuiSmokeLedgerLfRead')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$ctor=$type.GetConstructor($flags,$null,[type[]]@([IO.Stream],[Action],[bool],[Action]),$null)
$read=$type.GetMethod('ReadLine',$flags)
$utf8=[Text.UTF8Encoding]::new($false,$true)
$script:cases=0
function Check-Frame([byte[]]$Bytes,[bool]$AllowFinal,[bool]$ExpectedSuccess,[string[]]$Expected) {
    $stream=[IO.MemoryStream]::new($Bytes,$false); $lines=[Collections.Generic.List[string]]::new(); $success=$true
    try {
        $reader=$ctor.Invoke(@($stream,[Action]{},$AllowFinal,$null))
        try {while($null -ne ($line=$read.Invoke($reader,$null))) {$lines.Add($line)}} catch {$success=$false}
        if($success -ne $ExpectedSuccess) {throw 'Framing success mismatch'}
        if($success) {
            if($lines.Count -ne $Expected.Count) {throw 'Framing count mismatch'}
            for($i=0;$i -lt $lines.Count;$i++) {if($lines[$i] -cne $Expected[$i]) {throw 'Framing content mismatch'}}
        }
        $script:cases++
    } finally {$stream.Dispose()}
}
Check-Frame ($utf8.GetBytes("a`nb`n")) $false $true @('a','b')
Check-Frame ($utf8.GetBytes("a`nb")) $false $false @()
Check-Frame ($utf8.GetBytes("a`r`nb")) $true $true @('a','b')
Check-Frame ($utf8.GetBytes("a`nb`n")) $true $true @('a','b')
Check-Frame ($utf8.GetBytes(([string][char]0xfeff)+"a`r`nb")) $true $true @('a','b')
Check-Frame ([byte[]]@()) $true $true @()
Check-Frame ([byte[]]@(97,10,195)) $true $false @()
Check-Frame ([byte[]]@(97,10,195,10)) $true $false @()
$maximum='a'*262144
Check-Frame ($utf8.GetBytes($maximum)) $true $true @($maximum)
Check-Frame ($utf8.GetBytes($maximum+'a')) $true $false @()
$split=('a'*32767)+[char]0xe9
Check-Frame ($utf8.GetBytes($split)) $true $true @($split)
$crlf='a'*32767
Check-Frame ($utf8.GetBytes($crlf+"`r`nb")) $true $true @($crlf,'b')
'frontier-primary-framing='+$script:cases
# Count only scratch-stream construction in an exact compiler projection of
# the shared primitive. The subclass changes no read/validation behavior.
$source=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/GuiSmokeLogLedgerStreamingProcessor.cs'))
$start=$source.IndexOf('    internal sealed class GuiSmokeLedgerLfRead',[StringComparison]::Ordinal)
$end=$source.IndexOf('    public sealed class GuiSmokeLedgerWriterResult',$start,[StringComparison]::Ordinal)
if($start -lt 0 -or $end -le $start) {throw 'LF allocation projection section changed'}
$body=$source.Substring($start,$end-$start)
if(([regex]::Matches($body,'new MemoryStream\(\)')).Count -ne 1) {throw 'LF scratch construction changed'}
$projected=$body.Replace('new MemoryStream()','new CountedScratchStream()')
if($projected.Replace('new CountedScratchStream()','new MemoryStream()') -cne $body) {throw 'LF projection changed behavior'}
Add-Type -TypeDefinition (@'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace NLink.Tests.LfAllocation {
internal static class GuiSmokeLedgerStreamRead { internal const int MaximumLines=262144, MaximumLineCodeUnits=262144; }
internal sealed class CountedScratchStream : MemoryStream { internal static int Created; internal CountedScratchStream() { Created++; } }
'@+$projected+@'
public static class FramingAllocationProbe {
    private static void Check(bool ok, string reason) { if(!ok) throw new InvalidOperationException(reason); }
    public static string Run() {
        var utf8=new UTF8Encoding(false,true);
        CountedScratchStream.Created=0; int calls=0, boundaries=0;
        using(var source=new MemoryStream(utf8.GetBytes(new string('a',4096).Replace("a","ab\n")),false)) {
            var reader=new GuiSmokeLedgerLfRead(source,()=>calls++,false,()=>boundaries++);
            for(int i=0;i<4096;i++) Check(reader.ReadLine()=="ab" && reader.LineStarts[i]==3L*i,"buffered framing/index changed");
            Check(reader.ReadLine()==null && calls==8193 && boundaries==4,"buffered check cadence changed");
            Check(CountedScratchStream.Created==0,"buffered LF rows allocated scratch streams: "+CountedScratchStream.Created);
        }
        CountedScratchStream.Created=0; calls=boundaries=0;
        string split=new string('x',70000);
        using(var source=new MemoryStream(utf8.GetBytes(split+"\nz\n"),false)) {
            var reader=new GuiSmokeLedgerLfRead(source,()=>calls++,false,()=>boundaries++);
            Check(reader.ReadLine()==split && reader.ReadLine()=="z" && reader.ReadLine()==null,"split framing changed");
            Check(reader.LineStarts.Count==2 && reader.LineStarts[1]==70001 && calls==7 && boundaries==8,"split check cadence/index changed");
            Check(CountedScratchStream.Created==1,"scratch allocation not limited to fragmented line");
        }
        CountedScratchStream.Created=0;
        using(var source=new MemoryStream(new byte[]{97,10,195,10},false)) {
            var reader=new GuiSmokeLedgerLfRead(source,()=>{},false);
            Check(reader.ReadLine()=="a","valid prefix changed"); bool rejected=false;
            try { reader.ReadLine(); } catch(DecoderFallbackException) { rejected=true; }
            Check(rejected && reader.LineStarts.Count==1 && CountedScratchStream.Created==0,"invalid UTF8 committed or allocated scratch");
        }
        calls=0; bool stopped=false;
        using(var source=new MemoryStream(utf8.GetBytes("a\n"),false)) {
            var reader=new GuiSmokeLedgerLfRead(source,()=>{if(++calls==2) throw new IOException("revoked");},false);
            try { reader.ReadLine(); } catch(IOException) { stopped=true; }
            Check(stopped && calls==2,"completion currentness rejection was bypassed");
        }
        CountedScratchStream.Created=0;
        using(var source=new MemoryStream(utf8.GetBytes(new string('\n',262145)),false)) {
            var reader=new GuiSmokeLedgerLfRead(source,()=>{},false);
            for(int i=0;i<262144;i++) Check(reader.ReadLine()=="","empty LF row changed");
            bool rejected=false; try { reader.ReadLine(); } catch(InvalidDataException) { rejected=true; }
            Check(rejected && reader.LineStarts.Count==262144 && CountedScratchStream.Created==0,"line limit relaxed");
        }
        return "buffered=0;fragmented=1;utf8=reject;currentness=reject;line_limit=262144";
    }
}}
'@)
'frontier-lf-allocation='+[NLink.Tests.LfAllocation.FramingAllocationProbe]::Run()
