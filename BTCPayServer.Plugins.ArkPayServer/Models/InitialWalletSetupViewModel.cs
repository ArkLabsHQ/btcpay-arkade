namespace BTCPayServer.Plugins.ArkPayServer.Models;

/// <summary>
/// How the value posted in <see cref="InitialWalletSetupViewModel.Wallet"/>
/// should be interpreted by the initial-setup controller.
/// </summary>
public enum WalletSetupMode
{
    /// <summary>
    /// Existing behaviour: the controller inspects <see cref="InitialWalletSetupViewModel.Wallet"/>
    /// and routes by content — empty triggers a freshly generated HD wallet,
    /// <c>nsec...</c> creates a SingleKey wallet, a BIP-39 mnemonic creates an
    /// HD wallet, an Arkade address creates a transitory auto-sweep wallet,
    /// and an existing wallet id reuses that wallet. Default for back-compat.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Treat <see cref="InitialWalletSetupViewModel.Wallet"/> as an account
    /// descriptor (<c>tr([fp/86'/0'/0']xpub.../0/*)</c> or <c>tr(pubkey)</c>)
    /// and create a watch-only wallet — no signing material is stored.
    /// Signing-dependent operations (batch participation, unilateral exits)
    /// require a remote signer registered via <c>IBTCPayAppDeviceProxy</c>;
    /// without one the wallet remains usable in read-only mode.
    /// </summary>
    WatchOnly = 1
}

public class InitialWalletSetupViewModel
{
    public string? Wallet { get; set; }

    /// <summary>
    /// Interpretation mode for <see cref="Wallet"/>. Defaults to
    /// <see cref="WalletSetupMode.Auto"/> so existing form posts that don't
    /// know about this field keep working unchanged.
    /// </summary>
    public WalletSetupMode Mode { get; set; } = WalletSetupMode.Auto;
}
