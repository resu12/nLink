# Native polling contract review

2026-10-01. Keep the existing managed zero-time waits. Neither direct native
polling nor `Thread.Sleep(0)` followed by native polling preserves the reviewed
contract. Both PowerShell hosts reproduce an interruption mismatch, and both
native variants bypass the synchronization-context wait callback. No production
polling API, validation cadence, parser budget or acceptance rule changes in
this checkpoint.

This completes the narrowly scoped review proposed by the
[validation cost RCA](file-transfer-native-validation-cost-rca-20261001.md).
It does not establish that every possible optimization is impossible. It
rejects these two replacements as equivalent implementations and leaves the
first failed scale gate at the existing 5,001 ms local-clock rejection.

## Required behavior

The relevant checks poll parent exit, owner close, app exit and cancellation
handles. Their callers retain fresh registration, channel, file and custody
checks, with the original short-circuit order and entry/exit validation points.
A replacement must preserve the outcome of each managed wait, its interruption
and context behavior, and handle lifetime. It must not convert a native error
into evidence that the owner is current. Mutex acquisition and other wait
types are outside this optimization scope.

The .NET 8 and .NET 10 managed implementations hold a safe-handle reference and
delegate to a requesting synchronization context before using the runtime wait
path. These are source-level obligations, not assumptions that the current
worker always has a null context. [.NET 8 source](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/WaitHandle.cs),
[.NET 10 source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Threading/WaitHandle.cs).

## Isolated observations

The fixture uses only its own unnamed manual-reset events, helper threads and
one deliberately invalid test handle. It receives no original owner, file,
process lease, custody input or verdict. The hosts are PowerShell 5.1 on CLR
4.0.30319.42000 and PowerShell 7.6.6 on .NET 10.0.12. The test runner targets
.NET 8; its target framework is not the PowerShell 7 child runtime version.

Both hosts produce the following results:

| Condition | Managed wait | Direct native poll | Sleep then native poll |
| --- | --- | --- | --- |
| Unsignaled and signaled events | Expected state | Expected state | Expected state |
| Disposed event | ObjectDisposedException | ObjectDisposedException | ObjectDisposedException |
| Interruption pending before call | ThreadInterruptedException | Timeout with interruption still pending | ThreadInterruptedException |
| Interruption after a completed sleep probe but before the poll | ThreadInterruptedException at the reference managed poll | Not needed for this comparison | Timeout with interruption still pending |
| Context callback requests timeout for a signaled event | Callback runs and returns false | Callback skipped and signal reported | Callback skipped and signal reported |

The gap probe deliberately holds the helper thread between the completed
`Sleep(0)` and native poll, then interrupts it. A following managed wait throws,
confirming that the native poll did not consume or reject that interruption.
This is a deterministic counterexample to the composition, not a measurement
of the frequency of that race in the application.

The custom context receives one handle and timeout zero on both hosts. The
Framework host passes `waitAll=true`, while the .NET 10 host passes
`waitAll=false`. The fixture records this difference rather than forcing a
single runtime's callback arguments onto both hosts.

For a deliberately zero-valued test handle, native polling returns
`WAIT_FAILED` with error 6, while the managed test returns false on both hosts.
This is not a claim about a reachable invalid owner handle or a new production
vulnerability. It demonstrates why raw native result codes are not themselves
the managed API contract. Windows documents distinct success, timeout,
abandonment and failure results. [Windows wait documentation](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject).

## Handle lifetime and remaining limits

Passing `SafeWaitHandle` to P/Invoke protects the native resource during that
call through automatic reference counting. Passing an unprotected raw pointer
would not retain that guarantee. This does not establish original lease
membership or preserve interruption, cancellation timing, context callbacks
or surrounding custody checks. [SafeHandle documentation](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.safehandle).

`Sleep(0)` also introduces a scheduler yield. Its documented scheduling
behavior and the verified interruption gap make it unsuitable as an equivalent
precheck. [Thread documentation](https://github.com/dotnet/dotnet-api-docs/blob/main/xml/System.Threading/Thread.xml).

Concurrent disposal during a native call, original process handles, apartment
message pumping, application scheduling and all other native failure classes
are not exhaustively tested here. No equivalence claim is made for them.
Existing raw native polls elsewhere in the code do not authorize replacing
these managed waits or broadening their scope.

## Verification and next step

Two unique focused tests pass, covering 19 observations per host. The first
development run failed because the fixture assumed that every invalid-handle
managed wait throws. That assumption is removed. A later observation also
exposed the Framework context argument difference; the fixture now records
the actual arguments. These are fixture corrections, not application failures.
The failed and passing test receipts are preserved separately.

The [verification receipt](../artifacts/native-polling-contract-20261001/verification.json)
pins the review, tests, unchanged accumulated source and normal Core/Infra
assemblies. The normal test assembly is rebuilt for the new test entry point;
the diagnostic helper remains excluded from normal assemblies. All 99
historical live artifact pins are rechecked. No large scale scenario or live
GUI/NKN run is repeated because no production fix is implemented.

Recommended next step: review the parser time-budget policy separately,
including the relationship between its 5,000 ms local limit and the original
10,000 ms cumulative registration limit. Keep managed waits, validation points,
finite overall bounds and rejection rules unchanged during that review. A
larger fixture being close to completion is not itself approval to raise a
limit or accept incomplete acquisition. Any proposed budget change must state
its new bound and failure controls before implementation.

Production integration remains incomplete: the scale parser has not completed,
content-family coverage and reported-candidate binding remain unproven, and
historical route verdicts remain unchanged. There is no commit or push.
