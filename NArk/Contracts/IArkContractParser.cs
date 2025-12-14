using NBitcoin;

namespace NArk.Contracts;

public interface IArkContractParser
{
    string Type { get; }
    ArkContract? Parse( Dictionary<string, string> contractData, Network network);

    public static  Dictionary<string, string> GetContractData(string contract)
    {
            var parts = contract.Split('&');
        var data = new Dictionary<string, string>();
        foreach (var part in parts)
        {
            var kvp = part.Split('=');
            if (kvp.Length == 2)
            {
                data[kvp[0]] = kvp[1];
            }
        }
        return data;
    }
}