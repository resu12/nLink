#if NLINK_TEST_NATIVE_PIN_BOUNDARY
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierParentFilesPending
        {
            public sealed class PinFixtureResult
            {
                public string Name, Outcome, Exception;
                public int Checks, PinCount, LockCalls, TypeAttempts, TypeReturns, InformationAttempts, InformationReturns, QueryExceptions;
                public long LockTicks, TypeTicks, InformationTicks, ValidationTicks;
            }
            private static readonly List<PinFixtureResult> pinFixtureRows = new List<PinFixtureResult>();
            private static Action<string> pinFixtureBoundary;
            private static int pinChecks, pinLocks, pinTypes, pinTypeReturns, pinInformation, pinInformationReturns, pinExceptions;
            private static long pinLockTicks, pinTypeTicks, pinInformationTicks, pinValidationTicks;
            private static string pinException;
            private static void NativePinRecordLock(long ticks) { pinLocks++; pinLockTicks += ticks; }
            private static uint NativePinQueryType(SafeFileHandle handle)
            {
                pinTypes++; long started = Stopwatch.GetTimestamp(); uint result;
                try { result = GetFileType(handle); pinTypeReturns++; }
                catch { pinExceptions++; throw; }
                finally { pinTypeTicks += Stopwatch.GetTimestamp() - started; }
                if (pinFixtureBoundary != null) pinFixtureBoundary("after-type");
                return result;
            }
            private static bool NativePinQueryInformation(SafeFileHandle handle, out LocalFrontierPreparedFiles.FileInformation info)
            {
                pinInformation++; long started = Stopwatch.GetTimestamp(); bool result;
                try { result = GetFileInformationByHandle(handle, out info); pinInformationReturns++; }
                catch { pinExceptions++; throw; }
                finally { pinInformationTicks += Stopwatch.GetTimestamp() - started; }
                if (pinFixtureBoundary != null) pinFixtureBoundary("after-information");
                return result;
            }
            private static bool CheckPinFixture(CommitPins pins)
            {
                pinChecks++; long started = Stopwatch.GetTimestamp();
                try { return pins.IsCurrent; }
                catch (Exception error) { pinException = error.GetType().FullName; throw; }
                finally { pinValidationTicks += Stopwatch.GetTimestamp() - started; }
            }
            private static void RequirePinFixture(bool condition, string message)
            { if (!condition) throw new InvalidOperationException(message); }
            private static void PinCase(string name, int count, Action body)
            {
                pinChecks = pinLocks = pinTypes = pinTypeReturns = pinInformation = pinInformationReturns = pinExceptions = 0;
                pinLockTicks = pinTypeTicks = pinInformationTicks = pinValidationTicks = 0; pinException = null; pinFixtureBoundary = null;
                try { body(); }
                finally { pinFixtureBoundary = null; }
                pinFixtureRows.Add(new PinFixtureResult { Name = name, Outcome = "Passed", PinCount = count,
                    Checks = pinChecks, LockCalls = pinLocks, TypeAttempts = pinTypes, TypeReturns = pinTypeReturns,
                    InformationAttempts = pinInformation, InformationReturns = pinInformationReturns, QueryExceptions = pinExceptions,
                    LockTicks = pinLockTicks, TypeTicks = pinTypeTicks, InformationTicks = pinInformationTicks,
                    ValidationTicks = pinValidationTicks, Exception = pinException });
            }
            private sealed class PinFiles : IDisposable
            {
                internal readonly List<FileStream> Files = new List<FileStream>();
                internal readonly CommitPins Pins;
                internal PinFiles(string directory, int count, bool wrongIdentity = false)
                {
                    var entries = new LocalFrontierParentFilePacket.Entry[count];
                    Pins = new CommitPins(new LocalFrontierParentFilePacket(Guid.Empty, Guid.Empty, Guid.Empty, entries));
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var file = new FileStream(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".bin"), FileMode.CreateNew,
                                FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                            Files.Add(file); file.SetLength(8); file.Flush(true);
                            SafeFileHandle handle;
                            if (!DuplicateCommitHandle(GetCurrentProcess(), file.SafeFileHandle, GetCurrentProcess(), out handle, 0x80000000, false, 0))
                                throw new IOException("fixture_duplicate_failed");
                            Pins.Handles.Add(handle);
                            LocalFrontierPreparedFiles.FileInformation info;
                            if (!GetFileInformationByHandle(handle, out info)) throw new IOException("fixture_identity_failed");
                            entries[i] = new LocalFrontierParentFilePacket.Entry(0, -1, Guid.Empty,
                                new LocalFrontierFileManifest.FileEntry(handle.DangerousGetHandle().ToInt64(),
                                    info.Volume ^ (wrongIdentity ? 1U : 0U), info.IndexHigh, info.IndexLow, 8));
                        }
                    }
                    catch { Dispose(); throw; }
                }
                public void Dispose() { Pins.Dispose(); foreach (var file in Files) file.Dispose(); }
            }
            public static PinFixtureResult[] GetPinFixtureRows() { return pinFixtureRows.ToArray(); }
            public static void RunPinFixtures(string directory)
            {
                RequirePinFixture(LocalPublicationInputLimits.ProducerMilliseconds == 20000, "Producer budget changed");
                using (var f = new PinFiles(directory, 1)) PinCase("valid-fresh-queries", 1, () => {
                    RequirePinFixture(CheckPinFixture(f.Pins) && pinTypes == 1 && pinInformationReturns == 1, "Valid pin/query count changed");
                });
                using (var f = new PinFiles(directory, 1)) PinCase("closed-before-validation", 1, () => {
                    f.Pins.Handles[0].Dispose();
                    RequirePinFixture(!CheckPinFixture(f.Pins) && pinTypes == 0 && pinInformation == 0, "Closed pin reached native query");
                });
                using (var f = new PinFiles(directory, 1)) PinCase("dispose-between-native-queries", 1, () => {
                    pinFixtureBoundary = phase => { if (phase == "after-type") f.Pins.Handles[0].Dispose(); };
                    bool rejected = false;
                    try { CheckPinFixture(f.Pins); } catch (ObjectDisposedException) { rejected = true; }
                    RequirePinFixture(rejected && pinTypes == 1 && pinInformation == 1 && pinInformationReturns == 0 && pinExceptions == 1,
                        "Between-query disposal did not retain original SafeHandle rejection");
                });
                using (var f = new PinFiles(directory, 1, true)) PinCase("exact-identity-mismatch", 1, () => {
                    RequirePinFixture(!CheckPinFixture(f.Pins) && pinTypes == 1 && pinInformationReturns == 1, "Identity mismatch admitted");
                });
                using (var f = new PinFiles(directory, 1)) PinCase("length-changed-between-queries", 1, () => {
                    pinFixtureBoundary = phase => { if (phase == "after-type") { f.Files[0].SetLength(9); f.Files[0].Flush(true); } };
                    RequirePinFixture(!CheckPinFixture(f.Pins) && pinTypes == 1 && pinInformationReturns == 1, "Fresh length change hidden");
                });
                using (var pipe = new AnonymousPipeServerStream(PipeDirection.Out))
                using (var handle = new SafeFileHandle(pipe.SafePipeHandle.DangerousGetHandle(), false))
                using (var pins = new CommitPins(new LocalFrontierParentFilePacket(Guid.Empty, Guid.Empty, Guid.Empty,
                    new[] { new LocalFrontierParentFilePacket.Entry(0, -1, Guid.Empty, new LocalFrontierFileManifest.FileEntry(1, 0, 0, 0, 8)) })))
                {
                    pins.Handles.Add(handle);
                    PinCase("non-disk-type-short-circuit", 1, () => {
                        RequirePinFixture(!CheckPinFixture(pins) && pinTypes == 1 && pinInformation == 0, "Non-disk pin reached metadata query");
                    });
                    GC.KeepAlive(pipe);
                }
                using (var f = new PinFiles(directory, 1)) PinCase("owner-disposal-serialized", 1, () => {
                    Thread retirement = null; Exception error = null;
                    using (var entered = new ManualResetEvent(false))
                    {
                        pinFixtureBoundary = phase => {
                            if (phase != "after-type") return;
                            retirement = new Thread(() => { entered.Set(); try { f.Pins.Dispose(); } catch (Exception e) { error = e; } });
                            retirement.IsBackground = true; retirement.Start();
                            RequirePinFixture(entered.WaitOne(10000) && SpinWait.SpinUntil(() =>
                                (retirement.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0 || !retirement.IsAlive, 10000) &&
                                retirement.IsAlive && !f.Pins.Handles[0].IsClosed, "Owner disposal bypassed validation lock");
                        };
                        RequirePinFixture(CheckPinFixture(f.Pins), "Queued owner disposal changed current validation");
                        RequirePinFixture(retirement.Join(10000) && error == null && f.Pins.Handles[0].IsClosed, "Queued disposal failed to retire pin");
                        pinFixtureBoundary = null;
                        RequirePinFixture(!CheckPinFixture(f.Pins) && pinTypes == 1 && pinInformationReturns == 1, "Retired owner remained current");
                    }
                });
                using (var f = new PinFiles(directory, 1)) PinCase("dispose-after-metadata-next-check-visible", 1, () => {
                    pinFixtureBoundary = phase => { if (phase == "after-information") f.Pins.Handles[0].Dispose(); };
                    RequirePinFixture(CheckPinFixture(f.Pins), "Existing completed-metadata point-in-time result changed");
                    pinFixtureBoundary = null;
                    RequirePinFixture(!CheckPinFixture(f.Pins) && pinTypes == 1 && pinInformationReturns == 1, "Next validation missed closed pin");
                });
                foreach (int count in new[] { 12, 42 }) using (var f = new PinFiles(directory, count))
                {
                    PinCase("split-profile-" + count, count, () => {
                        for (int i = 0; i < 256; i++) RequirePinFixture(CheckPinFixture(f.Pins), "Profile validation failed");
                        RequirePinFixture(pinChecks == 256 && pinLocks == 256 && pinTypes == count * 256 &&
                            pinTypeReturns == count * 256 && pinInformation == count * 256 && pinInformationReturns == count * 256 && pinExceptions == 0,
                            "Profile omitted or duplicated native queries");
                    });
                }
            }
        }
    }
}
#endif
