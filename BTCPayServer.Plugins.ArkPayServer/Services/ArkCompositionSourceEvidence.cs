using BTCPayServer.Abstractions.Contracts;
using NArk.Abstractions.Blockchain;
using NArk.Core.Transport;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed record ArkCompositionSourceOutput(string TransactionId, uint OutputIndex, long AmountSats);
public sealed record ArkCompositionSourceFunding(string StoreId, Guid RouteId, string PaymentHash,
    string Destination, ArkCompositionSourceOutput[] Outputs);

public sealed class ArkCompositionSourceEvidence(ISettingsRepository settings, IBitcoinBlockchain blockchain,
    IClientTransport transport)
{
    private const int MaxEvidenceOutputs = 100;
    private const int MaxScanOutputs = 16_384;
    private const int MaxSearchCandidates = 4_096;
    private const int MaxSubsetStates = 10_000;
    private const int MaxSubsetTransitions = 250_000;

    public async Task CaptureAsync(string storeId, Guid routeId, string paymentHash, string customerDestination,
        long expectedAmountSats, CancellationToken cancellationToken)
    {
        if (await ReadAsync(storeId, routeId, paymentHash, customerDestination, expectedAmountSats) is not null) return;
        var outputs = await blockchain.GetUtxosAsync(customerDestination, cancellationToken);
        var selected = SelectExact(outputs, expectedAmountSats);
        if (selected.Length > 0)
        {
            await CaptureKnownAsync(storeId, routeId, paymentHash, customerDestination, expectedAmountSats,
                selected.Select(u => new OutPoint(uint256.Parse(u.Txid), u.Vout)).ToArray(), cancellationToken);
        }
    }

    public async Task<ArkCompositionSourceFunding?> ReadOrCaptureAsync(string storeId, Guid routeId,
        string paymentHash, string customerDestination, long expectedAmountSats, CancellationToken cancellationToken)
    {
        await CaptureAsync(storeId, routeId, paymentHash, customerDestination, expectedAmountSats, cancellationToken);
        return await ReadAsync(storeId, routeId, paymentHash, customerDestination, expectedAmountSats);
    }

    public async Task CaptureKnownAsync(string storeId, Guid routeId, string paymentHash, string customerDestination,
        long expectedAmountSats, IReadOnlyList<OutPoint> outpoints, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(paymentHash) || string.IsNullOrWhiteSpace(customerDestination)
            || outpoints.Count == 0 || outpoints.Count > MaxEvidenceOutputs
            || outpoints.Distinct().Count() != outpoints.Count)
            throw new InvalidOperationException("Exact onchain source funding evidence is required.");
        var server = await transport.GetServerInfoAsync(cancellationToken);
        var script = BitcoinAddress.Create(customerDestination, server.Network).ScriptPubKey;
        var verified = new List<ArkCompositionSourceOutput>();
        foreach (var point in outpoints)
        {
            var transaction = await blockchain.GetRawTransactionAsync(point.Hash, cancellationToken);
            if (transaction is null || transaction.GetHash() != point.Hash || point.N >= transaction.Outputs.Count
                || transaction.Outputs[point.N].ScriptPubKey != script || transaction.Outputs[point.N].Value.Satoshi <= 0)
                throw new InvalidOperationException("Onchain funding evidence does not pay the route's customer destination.");
            verified.Add(new ArkCompositionSourceOutput(point.Hash.ToString(), point.N, transaction.Outputs[point.N].Value.Satoshi));
        }
        if (verified.Sum(v => v.AmountSats) != expectedAmountSats)
            throw new InvalidOperationException("Onchain funding evidence does not have the route's exact amount.");
        var funding = new ArkCompositionSourceFunding(storeId, routeId, paymentHash,
            customerDestination, verified.OrderBy(v => v.TransactionId).ThenBy(v => v.OutputIndex).ToArray());
        var existing = await ReadAsync(storeId, routeId, paymentHash, customerDestination, expectedAmountSats);
        if (existing is not null)
        {
            if (!existing.Outputs.SequenceEqual(funding.Outputs))
                throw new InvalidOperationException("Onchain source funding evidence is immutable.");
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await settings.UpdateSetting(funding, Name(routeId));
    }

    public async Task<ArkCompositionSourceFunding?> ReadAsync(string storeId, Guid routeId, string paymentHash,
        string customerDestination, long expectedAmountSats)
    {
        var result = await settings.GetSettingAsync<ArkCompositionSourceFunding>(Name(routeId));
        if (result is not null && (result.StoreId != storeId || result.RouteId != routeId
            || result.PaymentHash != paymentHash || result.Destination != customerDestination
            || result.Outputs.Length == 0 || result.Outputs.Any(o => o.AmountSats <= 0)
            || result.Outputs.Sum(o => o.AmountSats) != expectedAmountSats))
            throw new InvalidOperationException("Onchain source evidence differs from its route.");
        return result;
    }

    private static string Name(Guid routeId) => $"ArkCompositionSourceFunding-{routeId:N}";

    private static BoardingUtxo[] SelectExact(IReadOnlyList<BoardingUtxo> outputs, long expected)
    {
        if (expected <= 0 || outputs.Count > MaxScanOutputs) return [];
        var candidatesByOutpoint = new Dictionary<(string Txid, uint Vout), BoardingUtxo>();
        BoardingUtxo? single = null;
        var exceededCandidateLimit = false;
        foreach (var output in outputs)
        {
            if (output.Amount == (ulong)expected && (single is null
                || StringComparer.Ordinal.Compare(output.Txid, single.Txid) < 0
                || output.Txid == single.Txid && output.Vout < single.Vout))
                single = output;
            if (output.Amount == 0 || output.Amount > (ulong)expected) continue;
            var key = (output.Txid, output.Vout);
            if (candidatesByOutpoint.TryGetValue(key, out var duplicate))
            {
                if (duplicate.Amount != output.Amount) return [];
                continue;
            }
            if (candidatesByOutpoint.Count == MaxSearchCandidates)
            {
                exceededCandidateLimit = true;
                continue;
            }
            candidatesByOutpoint.Add(key, output);
        }
        if (single is not null) return [single];
        if (exceededCandidateLimit) return [];

        var candidates = candidatesByOutpoint.Values
            .OrderByDescending(output => output.Amount)
            .ThenBy(output => output.Txid, StringComparer.Ordinal)
            .ThenBy(output => output.Vout)
            .ToArray();
        foreach (var transaction in candidates.GroupBy(output => output.Txid, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var grouped = transaction.ToArray();
            if (grouped.Length > MaxEvidenceOutputs) continue;
            long total = 0;
            foreach (var output in grouped)
            {
                if ((long)output.Amount > expected - total)
                {
                    total = -1;
                    break;
                }
                total += (long)output.Amount;
            }
            if (total == expected) return grouped;
        }

        return FindBoundedSubset(candidates, expected);
    }

    // These bounds cap adversarial UTXO scans; exceeding either search budget deliberately fails closed.
    private static BoardingUtxo[] FindBoundedSubset(IReadOnlyList<BoardingUtxo> candidates, long expected)
    {
        var sums = new Dictionary<long, Selection?> { [0] = null };
        var transitions = 0;
        foreach (var candidate in candidates)
        {
            foreach (var partial in sums.ToArray())
            {
                if (++transitions > MaxSubsetTransitions) return [];
                var count = (partial.Value?.Count ?? 0) + 1;
                if (count > MaxEvidenceOutputs || (long)candidate.Amount > expected - partial.Key) continue;
                var total = partial.Key + (long)candidate.Amount;
                if (sums.TryGetValue(total, out var previous) && previous is not null && previous.Count <= count)
                    continue;
                var selected = new Selection(candidate, partial.Value, count);
                if (total == expected) return selected.ToArray();
                if (!sums.ContainsKey(total) && sums.Count >= MaxSubsetStates) return [];
                sums[total] = selected;
            }
        }
        return [];
    }

    private sealed record Selection(BoardingUtxo Output, Selection? Previous, int Count)
    {
        public BoardingUtxo[] ToArray()
        {
            var result = new BoardingUtxo[Count];
            Selection? current = this;
            for (var index = Count - 1; index >= 0; index--)
            {
                result[index] = current!.Output;
                current = current.Previous;
            }
            return result;
        }
    }
}
