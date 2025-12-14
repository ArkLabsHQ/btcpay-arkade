using NBitcoin;

namespace NArk.Contracts;

public class GenericArkContractParser(string type, Func<Dictionary<string, string>, Network, ArkContract?> parse)
    : IArkContractParser
{
    public string Type { get; } = type;

    public ArkContract? Parse(Dictionary<string, string> contractData, Network network)
    {
        return parse(contractData, network);
    }
}