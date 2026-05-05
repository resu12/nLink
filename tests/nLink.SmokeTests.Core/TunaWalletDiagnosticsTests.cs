using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.Diagnostics;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class TunaWalletDiagnosticsTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaWalletLinkStore_PersistsLinkedPathOnlyWithoutSecrets()
    {
        var root = CreateTempRoot();
        try
        {
            var walletPath = Path.Combine(root, "wallet-test-nkn.json");
            var storePath = Path.Combine(root, "state", "tuna-wallet-link.json");
            var store = new JsonTunaWalletLinkStore(() => storePath);

            await store.SaveAsync(TunaWalletLinkState.Linked(walletPath, DateTimeOffset.Parse("2026-05-05T10:00:00Z")));

            var content = await File.ReadAllTextAsync(storePath);
            var loaded = await store.LoadAsync();
            Assert.Equal(Path.GetFullPath(walletPath), loaded.WalletPath);
            Assert.Contains("wallet-test-nkn.json", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seed", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task DiagnosticsTunaWallet_LinkValidateAndUnlink_RedactsShareableCopy()
    {
        var root = CreateTempRoot();
        var previousTunaEnabled = Environment.GetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED");
        try
        {
            PersistenceDiagnostics.ClearForTests();
            SessionTimeline.Clear();
            Environment.SetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", null);
            var walletPath = Path.Combine(root, "wallet-test-nkn.json");
            await File.WriteAllTextAsync(walletPath, "{}");
            var store = new JsonTunaWalletLinkStore(() => Path.Combine(root, "state.json"));
            var verifier = new FakeTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"));
            var vm = CreateViewModel(store, verifier);

            await vm.LinkTunaWalletAsync(walletPath);

            Assert.True(vm.IsTunaWalletLinked);
            Assert.Equal("wallet-test-nkn.json", vm.TunaWalletFileName);
            Assert.Equal("Linked, not verified", vm.TunaWalletStatus);
            Assert.True(vm.ValidateTunaWalletCommand.CanExecute(null));
            Assert.False(vm.CopyTunaWalletAddressCommand.CanExecute(null));

            var password = "session-only".ToCharArray();
            await vm.ValidateTunaWalletAsync(password);

            Assert.All(password, c => Assert.Equal('\0', c));
            Assert.Equal("Verified, funded", vm.TunaWalletStatus);
            Assert.Equal("funded", vm.TunaWalletBalanceCategory);
            Assert.Equal("NKN0123456789PUBLICADDRESS", vm.TunaWalletAddress);
            Assert.True(vm.CopyTunaWalletAddressCommand.CanExecute(null));
            Assert.Single(verifier.PasswordsSeen);
            Assert.Equal("session-only", new string(verifier.PasswordsSeen[0]));

            var copied = vm.BuildDiagnosticsCopyTextForTests();
            Assert.Contains("tuna_runtime_flag: Off", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_fallback_state: Current NKN will be used.", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_wallet_file: wallet-test-nkn.json", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_wallet_balance_category: funded", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_wallet_path: [REDACTED]", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_wallet_address: [REDACTED]", copied, StringComparison.Ordinal);
            Assert.DoesNotContain(root, copied, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NKN0123456789PUBLICADDRESS", copied, StringComparison.Ordinal);
            Assert.DoesNotContain("session-only", copied, StringComparison.Ordinal);

            await vm.UnlinkTunaWalletAsync();
            Assert.False(vm.IsTunaWalletLinked);
            Assert.Equal("Not linked", vm.TunaWalletStatus);
            Assert.False(File.Exists(Path.Combine(root, "state.json")));
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            SessionTimeline.Clear();
            Environment.SetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", previousTunaEnabled);
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task DiagnosticsTunaWallet_ValidationFailureIsNonFatalAndDoesNotPersistPassword()
    {
        var root = CreateTempRoot();
        try
        {
            var walletPath = Path.Combine(root, "wallet.json");
            await File.WriteAllTextAsync(walletPath, "{}");
            var store = new JsonTunaWalletLinkStore(() => Path.Combine(root, "state.json"));
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("wrong_password"));
            var vm = CreateViewModel(store, verifier);

            await vm.LinkTunaWalletAsync(walletPath);
            var password = "bad-password".ToCharArray();
            await vm.ValidateTunaWalletAsync(password);

            Assert.All(password, c => Assert.Equal('\0', c));
            Assert.Equal("Validation failed", vm.TunaWalletStatus);
            Assert.True(vm.ShowTunaWalletFailure);
            Assert.Equal("wrong_password", vm.TunaWalletLastFailure);

            var stateText = await File.ReadAllTextAsync(Path.Combine(root, "state.json"));
            Assert.DoesNotContain("bad-password", stateText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void DiagnosticsRedactor_Redacts_TunaWalletPathAndAddressMetadata()
    {
        var sample = string.Join(Environment.NewLine, new[]
        {
            "wallet_path=C:\\Users\\Juraj\\Desktop\\Remote help\\artifacts\\tuna-poc\\wallet-test-nkn.json",
            "tuna_wallet_path=C:\\Users\\Juraj\\Desktop\\Remote help\\artifacts\\tuna-poc\\wallet-test-nkn.json",
            "wallet_address=NKNabcdef123456789",
            "tuna_wallet_address=NKNabcdef987654321",
            "\"walletAddress\":\"NKNjson123456789\"",
            "normal=value",
        });

        var redacted = DiagnosticsRedactor.Redact(sample);

        Assert.DoesNotContain("Remote help", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wallet-test-nkn.json", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NKNabcdef", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("NKNjson", redacted, StringComparison.Ordinal);
        Assert.Contains("normal=value", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaRuntimeFlag_DefaultsOffEvenWhenWalletStateExists()
    {
        using var enabled = new EnvironmentOverride("NLINK_NKN_TUNA_ENABLED", null);
        var options = NknTunaAccelerationOptions.Load();

        Assert.False(options.Enabled);
    }

    private static DiagnosticsPageViewModel CreateViewModel(
        ITunaWalletLinkStore store,
        ITunaWalletVerifier verifier)
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        Environment.SetEnvironmentVariable("NLINK_TRANSPORT", null);
        try
        {
            return new DiagnosticsPageViewModel(
                ScreenShareEvidenceLocator.CreateDefault(),
                static () => { },
                TransportRuntimeConfig.Select(),
                tunaWalletLinkStore: store,
                tunaWalletVerifier: verifier);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nlink-tuna-wallet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root) &&
                root.Contains("nlink-tuna-wallet-tests", StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup only.
        }
    }

    private sealed class FakeTunaWalletVerifier : ITunaWalletVerifier
    {
        private readonly TunaWalletValidationResult result;

        public FakeTunaWalletVerifier(TunaWalletValidationResult result)
        {
            this.result = result;
        }

        public List<char[]> PasswordsSeen { get; } = new();

        public TunaWalletVerifierAvailability GetAvailability()
            => new(true, "available", "nlink-tuna-sidecar.exe");

        public Task<TunaWalletValidationResult> ValidateAsync(string walletPath, char[] password, CancellationToken ct)
        {
            PasswordsSeen.Add(password.ToArray());
            return Task.FromResult(result);
        }
    }
}
