﻿using Akka.Actor;
using Microsoft.AspNetCore.Http;
using Neo;
using Neo.Cryptography.ECC;
using Neo.IO.Json;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract;
using Neo.VM;
using Neo.Wallets;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace NeoExpress.Node
{
    internal class ExpressNodeRpcPlugin : Plugin, IRpcPlugin, IPersistencePlugin
    {
        private readonly Store store;
        private const byte APP_LOGS_PREFIX = 0xf1;
        private const byte CONTRACT_METADATA_PREFIX = 0xf2;

        public ExpressNodeRpcPlugin(Store store)
        {
            this.store = store;
        }

        public override void Configure()
        {
        }

        private static JObject ToJson(ContractParametersContext context)
        {
            var json = new JObject();
            json["contract-context"] = context.ToJson();
            json["script-hashes"] = new JArray(context.ScriptHashes
                .Select(hash => new JString(hash.ToAddress())));
            json["hash-data"] = context.Verifiable.GetHashData().ToHexString();

            return json;
        }

        private JObject OnShowCoins(JArray @params)
        {
            var address = @params[0].AsString().ToScriptHash();

            using (var snapshot = Blockchain.Singleton.GetSnapshot())
            {
                var coins = NodeUtility.GetCoins(snapshot, ImmutableHashSet.Create(address));

                return new JArray(coins.Select(c =>
                {
                    var j = new JObject();
                    j["state"] = (byte)c.State;
                    j["state-label"] = c.State.ToString();
                    j["reference"] = c.Reference.ToJson();
                    j["output"] = c.Output.ToJson(0);
                    return j;
                }));
            }
        }

        public JObject OnGetContractStorage(JArray @params)
        {
            var scriptHash = UInt160.Parse(@params[0].AsString());

            using (var snapshot = Blockchain.Singleton.GetSnapshot())
            {
                var storages = new JArray();
                foreach (var kvp in snapshot.Storages.Find())
                {
                    if (kvp.Key.ScriptHash == scriptHash)
                    {
                        var storage = new JObject();
                        storage["key"] = kvp.Key.Key.ToHexString();
                        storage["value"] = kvp.Value.Value.ToHexString();
                        storage["constant"] = kvp.Value.IsConstant;
                        storages.Add(storage);
                    }
                }

                return storages;
            }
        }

        public JObject OnCheckpointCreate(JArray @params)
        {
            string filename = @params[0].AsString();

            if (ProtocolSettings.Default.StandbyValidators.Length > 1)
            {
                throw new Exception("Checkpoint create is only supported on single node express instances");
            }

            if (store is Persistence.RocksDbStore rocksDbStore)
            {
                var defaultAccount = System.RpcServer.Wallet.GetAccounts().Single(a => a.IsDefault);
                BlockchainOperations.CreateCheckpoint(
                    rocksDbStore,
                    filename,
                    ProtocolSettings.Default.Magic,
                    defaultAccount.ScriptHash.ToAddress());

                return filename;
            }
            else
            {
                throw new Exception("Checkpoint create is only supported for RocksDb storage implementation");
            }
        }

        public JObject OnGetApplicationLog(JArray @params)
        {
            var hash = UInt256.Parse(@params[0].AsString());
            var value = store.Get(APP_LOGS_PREFIX, hash.ToArray());

            if (value != null && value.Length > 0)
            {
                var json = Encoding.UTF8.GetString(value);
                return JObject.Parse(json);
            }

            // I'd rather be returning JObject.Null here, but Neo's RPC plugin
            // infrastructure can't distingish between null return meaning 
            // "this plugin doesn't support this method" and JObject.Null return
            // meaning "this plugin does support this method, but there was a null
            // return value". So I'm using an empty string as the null response. 

            return string.Empty;
        }

        public JObject OnGetUnspents(JArray @params)
        {
            JObject GetBalance(IEnumerable<Coin> coins, UInt256 assetId, string symbol)
            {
                var unspents = new JArray();
                var total = Fixed8.Zero;
                foreach (var coin in coins.Where(c => c.Output.AssetId == assetId))
                {
                    var unspent = new JObject();
                    unspent["txid"] = coin.Reference.PrevHash.ToString().Substring(2);
                    unspent["n"] = coin.Reference.PrevIndex;
                    unspent["value"] = (double)(decimal)coin.Output.Value;

                    total += coin.Output.Value;
                    unspents.Add(unspent);
                }

                var balance = new JObject();
                balance["asset_hash"] = assetId.ToString().Substring(2);
                balance["asset_symbol"] = balance["asset"] = symbol;
                balance["amount"] = (double)(decimal)total;
                balance["unspent"] = unspents;

                return balance;
            }

            var address = @params[0].AsString().ToScriptHash();
            string[] nativeAssetNames = { "GAS", "NEO" };
            UInt256[] nativeAssetIds = { Blockchain.UtilityToken.Hash, Blockchain.GoverningToken.Hash };

            using (var snapshot = Blockchain.Singleton.GetSnapshot())
            {
                var coins = NodeUtility.GetCoins(snapshot, ImmutableHashSet.Create(address)).Unspent();

                var neoCoins = coins.Where(c => c.Output.AssetId == Blockchain.GoverningToken.Hash);
                var gasCoins = coins.Where(c => c.Output.AssetId == Blockchain.UtilityToken.Hash);

                JObject json = new JObject();
                json["address"] = address.ToAddress();
                json["balance"] = new JArray(
                    GetBalance(coins, Blockchain.GoverningToken.Hash, "NEO"),
                    GetBalance(coins, Blockchain.UtilityToken.Hash, "GAS"));
                return json;
            }
        }

        private JObject GetUnclaimed(JArray @params)
        {
            var address = @params[0].AsString().ToScriptHash();

            using (var snapshot = Blockchain.Singleton.GetSnapshot())
            {
                var coins = NodeUtility.GetCoins(snapshot, ImmutableHashSet.Create(address));

                var unclaimedCoins = coins.Unclaimed(Blockchain.GoverningToken.Hash);
                var unspentCoins = coins.Unspent(Blockchain.GoverningToken.Hash);

                var unavailable = snapshot.CalculateBonus(
                    unspentCoins.Select(c => c.Reference),
                    snapshot.Height + 1);
                var available = snapshot.CalculateBonus(unclaimedCoins.Select(c => c.Reference));

                JObject json = new JObject();
                json["unavailable"] = (double)(decimal)unavailable;
                json["available"] = (double)(decimal)available;
                json["unclaimed"] = (double)(decimal)(available + unavailable);
                return json;
            }
        }

        private JObject GetClaimable(JArray @params)
        {
            var address = @params[0].AsString().ToScriptHash();

            using (var snapshot = Blockchain.Singleton.GetSnapshot())
            {
                var coins = NodeUtility.GetCoins(snapshot, ImmutableHashSet.Create(address));
                var unclaimedCoins = coins.Unclaimed(Blockchain.GoverningToken.Hash);

                var totalUnclaimed = Fixed8.Zero;
                var claimable = new JArray();
                foreach (var coin in unclaimedCoins)
                {
                    var spentCoinState = snapshot.SpentCoins.TryGet(coin.Reference.PrevHash);
                    var startHeight = spentCoinState.TransactionHeight;
                    var endHeight = spentCoinState.Items[coin.Reference.PrevIndex];
                    var (generated, sysFee) = NodeUtility.CalculateClaimable(snapshot, coin.Output.Value, startHeight, endHeight);
                    var unclaimed = generated + sysFee;
                    totalUnclaimed += unclaimed;

                    var utxo = new JObject();
                    utxo["txid"] = coin.Reference.PrevHash.ToString().Substring(2);
                    utxo["n"] = coin.Reference.PrevIndex;
                    utxo["start_height"] = startHeight;
                    utxo["end_height"] = endHeight;
                    utxo["generated"] = (double)(decimal)generated;
                    utxo["sys_fee"] = (double)(decimal)sysFee;
                    utxo["unclaimed"] = (double)(decimal)(unclaimed);

                    claimable.Add(utxo);
                }

                JObject json = new JObject();
                json["claimable"] = claimable;
                json["address"] = address.ToAddress();
                json["unclaimed"] = (double)(decimal)totalUnclaimed;
                return json;
            }
        }

        private JObject OnGetPopulatedBlocks(JArray @params)
        {
            using var snapshot = Blockchain.Singleton.GetSnapshot();

            var count = @params.Count >= 1 ? uint.Parse(@params[0].AsString()) : 20;
            count = count > 100 ? 100 : count;

            var start = @params.Count >= 2 ? uint.Parse(@params[1].AsString()) : snapshot.Height;
            start = start > snapshot.Height ? snapshot.Height : start;

            var populatedBlocks = new JArray();
            while (populatedBlocks.Count < count)
            {
                var block = snapshot.GetBlock(start);
                if (block.Transactions.Length > 1)
                {
                    populatedBlocks.Add(block.Index);
                }

                if (start == 0)
                {
                    break;
                }
                else
                {
                    start--;
                }
            }
            return populatedBlocks;
        }

        private JObject OnSaveContractMetadata(JArray @params)
        {
            var scriptHash = UInt160.Parse(@params[0].AsString());
            var metadata = @params[1];
            var value = Encoding.UTF8.GetBytes(metadata.ToString());
            store.Put(CONTRACT_METADATA_PREFIX, scriptHash.ToArray(), value);
            return true;
        }

        private JObject OnGetContractMetadata(JArray @params)
        {
            var scriptHash = UInt160.Parse(@params[0].AsString());
            var value = store.Get(CONTRACT_METADATA_PREFIX, scriptHash.ToArray());

            if (value != null && value.Length > 0)
            {
                var json = Encoding.UTF8.GetString(value);
                return JObject.Parse(json);
            }

            throw new Exception("Unknown Contract Metadata");
        }

        private JObject OnListContractMetadata(JArray _)
        {
            var contracts = new JArray();
            using var snapshot = Blockchain.Singleton.GetSnapshot();
            foreach (var kvp in snapshot.Contracts.Find())
            {
                var metadata = store.Get(CONTRACT_METADATA_PREFIX, kvp.Key.ToArray());
                if (metadata != null && metadata.Length > 0)
                {
                    var json = JObject.Parse(Encoding.UTF8.GetString(metadata));
                    json["type"] = "metadata";
                    contracts.Add(json);
                }
                else
                {
                    var json = kvp.Value.ToJson();
                    json["type"] = "state";
                    contracts.Add(json);
                }
            }
            return contracts;
        }

        JObject? IRpcPlugin.OnProcess(HttpContext context, string method, JArray @params)
        {
            switch (method)
            {
                // ApplicationLogs plugin compatible RPC endpoints
                case "getapplicationlog":
                    return OnGetApplicationLog(@params);

                // RpcSystemAssetTracker plugin compatible RPC endpoints
                case "getclaimable":
                    return GetClaimable(@params);
                case "getunclaimed":
                    return GetUnclaimed(@params);
                case "getunspents":
                    return OnGetUnspents(@params);

                // custom Neo-Express RPC Endpoints
                case "express-show-coins":
                    return OnShowCoins(@params);
                case "express-get-contract-storage":
                    return OnGetContractStorage(@params);
                case "express-create-checkpoint":
                    return OnCheckpointCreate(@params);
                case "express-get-populated-blocks":
                    return OnGetPopulatedBlocks(@params);
                case "express-save-contract-metadata":
                    return OnSaveContractMetadata(@params);
                case "express-get-contract-metadata":
                    return OnGetContractMetadata(@params);
                case "express-list-contract-metadata":
                    return OnListContractMetadata(@params);
            }

            return null;
        }

        void IRpcPlugin.PreProcess(HttpContext context, string method, JArray _params)
        {
        }

        void IRpcPlugin.PostProcess(HttpContext context, string method, JArray _params, JObject result)
        {
        }

        private static JObject Convert(Blockchain.ApplicationExecuted appExec)
        {
            JObject json = new JObject();
            json["txid"] = appExec.Transaction.Hash.ToString();
            json["executions"] = appExec.ExecutionResults.Select(p =>
            {
                JObject execution = new JObject();
                execution["trigger"] = p.Trigger;
                execution["contract"] = p.ScriptHash.ToString();
                execution["vmstate"] = p.VMState;
                execution["gas_consumed"] = p.GasConsumed.ToString();
                try
                {
                    execution["stack"] = p.Stack.Select(q => q.ToParameter().ToJson()).ToArray();
                }
                catch (InvalidOperationException)
                {
                    execution["stack"] = "error: recursive reference";
                }
                execution["notifications"] = p.Notifications.Select(q =>
                {
                    JObject notification = new JObject();
                    notification["contract"] = q.ScriptHash.ToString();
                    try
                    {
                        notification["state"] = q.State.ToParameter().ToJson();
                    }
                    catch (InvalidOperationException)
                    {
                        notification["state"] = "error: recursive reference";
                    }
                    return notification;
                }).ToArray();
                return execution;
            }).ToArray();
            return json;
        }

        void IPersistencePlugin.OnPersist(Snapshot snapshot, IReadOnlyList<Blockchain.ApplicationExecuted> applicationExecutedList)
        {
            foreach (var appExec in applicationExecutedList)
            {
                var json = Convert(appExec);
                var key = appExec.Transaction.Hash.ToArray();
                var value = Encoding.UTF8.GetBytes(json.ToString());
                store.Put(APP_LOGS_PREFIX, key, value);
            }
        }

        void IPersistencePlugin.OnCommit(Snapshot snapshot)
        {
        }

        bool IPersistencePlugin.ShouldThrowExceptionFromCommit(Exception ex) => false;
    }
}