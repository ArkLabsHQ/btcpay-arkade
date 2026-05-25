namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Enables the dedicated <c>ARKADE-ASSET</c> payment method on a store. Thin by
/// design: the wallet and the tracked-asset list live on the BTC-VTXO
/// <see cref="ArkadePaymentMethodConfig"/> (single source of truth). This
/// config's mere presence is what makes BTCPay offer the asset method — it is
/// written when the store has at least one enabled tracked asset and cleared
/// otherwise (see <c>ArkController.SyncAssetPaymentMethod</c>).
/// </summary>
public record ArkadeAssetPaymentMethodConfig(string WalletId);
