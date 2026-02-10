#nullable enable
using NArk.Abstractions;
using NBitcoin;
using NBitcoin.Payment;

namespace BTCPayServer.Plugins.Arkade.Payouts.Ark
{
    public class ArkUriClaimDestination : IArkClaimDestination
    {
        private readonly BitcoinUrlBuilder _bitcoinUrl;

        public ArkUriClaimDestination(BitcoinUrlBuilder bitcoinUrl) 
        {
            ArgumentNullException.ThrowIfNull(bitcoinUrl);
            if (bitcoinUrl.Address is null)
                throw new ArgumentException(nameof(bitcoinUrl));
            _bitcoinUrl = bitcoinUrl;
        }
        public BitcoinUrlBuilder BitcoinUrl => _bitcoinUrl;
        public override string ToString()
        {
            return _bitcoinUrl.ToString();
        }

        public string Id => Address.ToString(_bitcoinUrl.Network.ChainName == ChainName.Mainnet);
        public decimal? Amount => _bitcoinUrl.Amount?.ToDecimal(MoneyUnit.BTC);

        public ArkAddress Address => ArkAddress.Parse(_bitcoinUrl.UnknownParameters["ark"]);
    }
}
