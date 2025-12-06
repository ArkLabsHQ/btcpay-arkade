using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Services.Batches;

/// <summary>
/// Tree signer session implementation using IArkadeWalletSigner
/// </summary>
public class TreeSignerSession
{
    private readonly IArkadeWalletSigner _signer;
    private Dictionary<uint256, (MusigPrivNonce secNonce, MusigPubNonce pubNonce)>? _myNonces;
    private Dictionary<uint256, MusigContext>? _musigContexts;
    private readonly TxTree _graph;
    private readonly uint256? _tapsciptMerkleRoot;
    private readonly Money _rootSharedOutputAmount;

    private readonly Task<ECPubKey> _myPublicKey;

    public TreeSignerSession(IArkadeWalletSigner signer, TxTree tree, uint256? tapsciptMerkleRoot, Money rootInputAmount)
    {
        _myPublicKey = signer.GetPublicKey();
        _signer = signer;
        _graph = tree;
        _tapsciptMerkleRoot = tapsciptMerkleRoot;
        _rootSharedOutputAmount = rootInputAmount;
    }


    private async Task CreateMusigContexts(CancellationToken cancellationToken = default)
    {
        if(_musigContexts != null)
            throw new InvalidOperationException("musig contexts already created");
        _musigContexts = new Dictionary<uint256, MusigContext>();
        var myPubKey = await _signer.GetPublicKey(cancellationToken);
        foreach (var g in _graph)
        {
            var tx = g.Root.GetGlobalTransaction();
            
            // Extract cosigner keys for this transaction
            var cosignerKeys = g.Root.Inputs[0].GetArkFieldsCosigners()
                .OrderBy(data => data.Index)
                .Select(data => data.Key)
                .ToArray();

            if (cosignerKeys.All(key => key != myPubKey))
            {
                continue;
            }

            // Get prevout information and calculate sighash for this transaction
            var (prevoutAmount, prevoutScript) = GetPrevOutput(g, _graph);
            var execData = new TaprootExecutionData(0) { SigHash = TaprootSigHash.Default };
            var prevoutArray = new[] { new TxOut(Money.Satoshis(prevoutAmount), prevoutScript) };
            var sighash = tx.GetSignatureHashTaproot(prevoutArray, execData);

            // Create MUSIG context with the actual sighash that will be signed
            var musigContext = new MusigContext(cosignerKeys, sighash.ToBytes(), myPubKey);
            
            if (_tapsciptMerkleRoot is not null)
            {
                using var sha = new NBitcoin.Secp256k1.SHA256();
                sha.InitializeTagged("TapTweak");
                sha.Write(musigContext.AggregatePubKey.ToXOnlyPubKey().ToBytes());
                sha.Write(_tapsciptMerkleRoot.ToBytes());
                var taprootHash = sha.GetHash();
                musigContext.Tweak(taprootHash);
            }
            
            _musigContexts[tx.GetHash()] = musigContext;
        }
    }
    
    

    public async Task<ECPubKey> GetPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        return await _signer.GetPublicKey(cancellationToken);
    }

    public async Task<Dictionary<uint256, MusigPubNonce>> GetNoncesAsync(CancellationToken cancellationToken = default)
    {
        _myNonces ??= await GenerateNoncesAsync(cancellationToken);

        return _myNonces.ToDictionary(pair => pair.Key, pair => pair.Value.pubNonce);
    }

    public void VerifyAggregatedNonces(Dictionary<uint256, MusigPubNonce> expectedAggregateNonces,
        CancellationToken cancellationToken = default)
    {
        if (_musigContexts is null)
            throw new InvalidOperationException("musig contexts not created");

        if (_myNonces is null)
            throw new InvalidOperationException("nonces not generated");
        
        if (_musigContexts.Any(musigContext => !expectedAggregateNonces[musigContext.Key].ToBytes().SequenceEqual(musigContext.Value.AggregateNonce!.ToBytes())))
            throw new InvalidOperationException("aggregated nonces do not match");
    }

    public async Task<Dictionary<uint256, MusigPartialSignature>> SignAsync(CancellationToken cancellationToken = default)
    {
        if (_graph == null)
            throw new InvalidOperationException("missing vtxo graph");
        if (_myNonces == null)
            throw new InvalidOperationException("nonces not generated");

        var sigs = new Dictionary<uint256, MusigPartialSignature>();
        foreach (var g in _graph)
        {
            var txid = g.Root.GetGlobalTransaction().GetHash();
            var sig = await SignPartialAsync(g, cancellationToken);
            sigs[txid] = sig;
        }

        return sigs;
    }

    private async Task<Dictionary<uint256, (MusigPrivNonce secNonce, MusigPubNonce pubNonce)>> GenerateNoncesAsync(CancellationToken cancellationToken = default)
    {
        if (_musigContexts == null)
            await CreateMusigContexts(cancellationToken);
        
        if (_myNonces != null)
            throw new InvalidOperationException("nonces already generated");

        var myPubKey = await _myPublicKey.WithCancellation(cancellationToken);

        var res = new Dictionary<uint256, (MusigPrivNonce secNonce, MusigPubNonce pubNonce)>();
        foreach (var (txid, musigContext) in _musigContexts!)
        {
            // Generate nonce tied to this specific context
            var nonce = musigContext.GenerateNonce(myPubKey);
            res[txid] = (nonce, nonce.CreatePubNonce());
        }

        return res;
    }

    private async Task<MusigPartialSignature> SignPartialAsync(TxTree g, CancellationToken cancellationToken)
    {

        if (_myNonces == null || _musigContexts == null)
            throw new InvalidOperationException("session not properly initialized");

        var txid = g.Root.GetGlobalTransaction().GetHash();
        
        if (!_myNonces.TryGetValue(txid, out var myNonce))
            throw new InvalidOperationException("missing private nonce");

        if (!_musigContexts.TryGetValue(txid, out var musigContext))
            throw new InvalidOperationException("missing musig context");

        if (musigContext.AggregateNonce is null)
            throw new InvalidOperationException("missing aggregate nonce");
        
        // Use the wallet signer to create a MUSIG2 partial signature
        // The context already has the correct sighash from nonce generation
        var partialSig = await _signer.SignMusig(musigContext, myNonce.secNonce, cancellationToken);
        
        return partialSig;
    }

    /// <summary>
    /// Gets the previous output information for a transaction in the tree
    /// Matches TypeScript getPrevOutput function (lines 215-250)
    /// </summary>
    private (long amount, Script script) GetPrevOutput(TxTree g, TxTree rootGraph)
    {
        // Extract cosigner keys and aggregate with taproot tweak to get final key
        var cosignerKeys = g.Root.Inputs[0].GetArkFieldsCosigners()
            .OrderBy(data => data.Index)
            .Select(data => data.Key)
            .ToArray();
        
        // Aggregate keys with taproot tweak (matches TypeScript lines 125-127)
        var aggregatedKey = ECPubKey.MusigAggregate(cosignerKeys);
        if (_tapsciptMerkleRoot == null)
            throw new InvalidOperationException("script root not set");
        
        
        // Generate P2TR script from final key (matches TypeScript line 222)
        var taprootFinalKey =
            TaprootFullPubKey.Create(new TaprootInternalPubKey(aggregatedKey.ToXOnlyPubKey().ToBytes()), _tapsciptMerkleRoot);
        
        var txid = g.Root.GetGlobalTransaction().GetHash();
        
        // If this is the root transaction, return shared output amount (matches TypeScript lines 227-232)
        if (txid == rootGraph.Root.GetGlobalTransaction().GetHash())
        {
            return (_rootSharedOutputAmount, taprootFinalKey.ScriptPubKey);
        }
        
        // Find parent transaction (matches TypeScript lines 234-242)
        var tx = g.Root.GetGlobalTransaction();
        var parentInput = tx.Inputs[0];
        var parentTxid = parentInput.PrevOut.Hash;
        
        var parent = rootGraph.Find(parentTxid);
        if (parent == null)
            throw new InvalidOperationException($"parent tx not found: {parentTxid}");
        
        var parentOutput = parent.Root.GetGlobalTransaction().Outputs[(int)parentInput.PrevOut.N];
        if (parentOutput == null)
            throw new InvalidOperationException("parent output not found");
        
        return (parentOutput.Value.Satoshi, taprootFinalKey.ScriptPubKey);
    }

    public async Task AggregateNonces(uint256 txid, MusigPubNonce[] toArray, CancellationToken cancellationToken)
    {
        if (_musigContexts == null)
            throw new InvalidOperationException("musig contexts not created");
        
        if (!_musigContexts.TryGetValue(txid, out var musigContext))
            throw new InvalidOperationException("missing musig context");
        
        if(!_myNonces.TryGetValue(txid, out var myNonce))
            throw new InvalidOperationException("missing private nonce");

        if (!toArray.Any(nonce => nonce.ToBytes().SequenceEqual(myNonce.pubNonce.ToBytes())))
        {
            throw new InvalidOperationException("missing my nonce");
        }
        
        musigContext.ProcessNonces(toArray);
    }
}