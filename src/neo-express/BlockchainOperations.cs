﻿using Neo;
using NeoExpress.Models;
using NeoExpress.Node;
using NeoExpress.Persistence;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace NeoExpress
{
    internal static class BlockchainOperations
    {
        public static ExpressChain CreateBlockchain(int count)
        {
            var wallets = new List<(DevWallet wallet, Neo.Wallets.WalletAccount account)>(count);

            try
            {
                for (int i = 1; i <= count; i++)
                {
                    var wallet = new DevWallet($"node{i}");
                    var account = wallet.CreateAccount();
                    account.IsDefault = true;
                    wallets.Add((wallet, account));
                }

                var keys = wallets.Select(t => t.account.GetKey().PublicKey).ToArray();

                var contract = Neo.SmartContract.Contract.CreateMultiSigContract((keys.Length * 2 / 3) + 1, keys);

                foreach (var (wallet, account) in wallets)
                {
                    var multiSigContractAccount = wallet.CreateAccount(contract, account.GetKey());
                    multiSigContractAccount.Label = "MultiSigContract";
                }

                // 49152 is the first port in the "Dynamic and/or Private" range as specified by IANA
                // http://www.iana.org/assignments/port-numbers
                ushort port = 49152;
                return new ExpressChain()
                {
                    Magic = ExpressChain.GenerateMagicValue(),
                    ConsensusNodes = wallets.Select(t => new ExpressConsensusNode()
                    {
                        TcpPort = port++,
                        WebSocketPort = port++,
                        RpcPort = port++,
                        Wallet = t.wallet.ToExpressWallet()
                    }).ToList()
                };
            }
            finally
            {
                foreach (var (wallet, _) in wallets)
                {
                    wallet.Dispose();
                }
            }
        }

        public static void ExportBlockchain(ExpressChain chain, string folder, string password, Action<string> writeConsole)
        {
            void WriteNodeConfigJson(ExpressConsensusNode _node, string walletPath)
            {
                using (var stream = File.Open(Path.Combine(folder, $"{_node.Wallet.Name}.config.json"), FileMode.Create, FileAccess.Write))
                using (var writer = new JsonTextWriter(new StreamWriter(stream)) { Formatting = Formatting.Indented })
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("ApplicationConfiguration");
                    writer.WriteStartObject();

                    writer.WritePropertyName("Paths");
                    writer.WriteStartObject();
                    writer.WritePropertyName("Chain");
                    writer.WriteValue("Chain_{0}");
                    writer.WriteEndObject();

                    writer.WritePropertyName("P2P");
                    writer.WriteStartObject();
                    writer.WritePropertyName("Port");
                    writer.WriteValue(_node.TcpPort);
                    writer.WritePropertyName("WsPort");
                    writer.WriteValue(_node.WebSocketPort);
                    writer.WriteEndObject();

                    writer.WritePropertyName("RPC");
                    writer.WriteStartObject();
                    writer.WritePropertyName("BindAddress");
                    writer.WriteValue("127.0.0.1");
                    writer.WritePropertyName("Port");
                    writer.WriteValue(_node.RpcPort);
                    writer.WritePropertyName("SslCert");
                    writer.WriteValue("");
                    writer.WritePropertyName("SslCertPassword");
                    writer.WriteValue("");
                    writer.WriteEndObject();

                    writer.WritePropertyName("UnlockWallet");
                    writer.WriteStartObject();
                    writer.WritePropertyName("Path");
                    writer.WriteValue(walletPath);
                    writer.WritePropertyName("Password");
                    writer.WriteValue(password);
                    writer.WritePropertyName("StartConsensus");
                    writer.WriteValue(true);
                    writer.WritePropertyName("IsActive");
                    writer.WriteValue(true);
                    writer.WriteEndObject();

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }

            void WriteProtocolJson()
            {
                using (var stream = File.Open(Path.Combine(folder, "protocol.json"), FileMode.Create, FileAccess.Write))
                using (var writer = new JsonTextWriter(new StreamWriter(stream)) { Formatting = Formatting.Indented })
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("ProtocolConfiguration");
                    writer.WriteStartObject();

                    writer.WritePropertyName("Magic");
                    writer.WriteValue(chain.Magic);
                    writer.WritePropertyName("AddressVersion");
                    writer.WriteValue(23);
                    writer.WritePropertyName("SecondsPerBlock");
                    writer.WriteValue(15);

                    writer.WritePropertyName("StandbyValidators");
                    writer.WriteStartArray();
                    for (int i = 0; i < chain.ConsensusNodes.Count; i++)
                    {
                        var account = DevWalletAccount.FromExpressWalletAccount(chain.ConsensusNodes[i].Wallet.DefaultAccount);
                        writer.WriteValue(account.GetKey().PublicKey.EncodePoint(true).ToHexString());
                    }
                    writer.WriteEndArray();

                    writer.WritePropertyName("SeedList");
                    writer.WriteStartArray();
                    foreach (var node in chain.ConsensusNodes)
                    {
                        writer.WriteValue($"{System.Net.IPAddress.Loopback}:{node.TcpPort}");
                    }
                    writer.WriteEndArray();

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }

            for (var i = 0; i < chain.ConsensusNodes.Count; i++)
            {
                var node = chain.ConsensusNodes[i];
                writeConsole($"Exporting {node.Wallet.Name} Conensus Node wallet");

                var walletPath = Path.Combine(folder, $"{node.Wallet.Name}.wallet.json");
                if (File.Exists(walletPath))
                {
                    File.Delete(walletPath);
                }

                ExportWallet(node.Wallet, walletPath, password);
                WriteNodeConfigJson(node, walletPath);
            }

            WriteProtocolJson();
        }

        public static ExpressWallet CreateWallet(string name)
        {
            using (var wallet = new DevWallet(name))
            {
                var account = wallet.CreateAccount();
                account.IsDefault = true;
                return wallet.ToExpressWallet();
            }
        }

        public static void ExportWallet(ExpressWallet wallet, string filename, string password)
        {
            var devWallet = DevWallet.FromExpressWallet(wallet);
            devWallet.Export(filename, password);
        }

        public static (byte[] signature, byte[] publicKey) Sign(ExpressWalletAccount account, byte[] data)
        {
            var devAccount = DevWalletAccount.FromExpressWalletAccount(account);

            var key = devAccount.GetKey();
            var publicKey = key.PublicKey.EncodePoint(false).AsSpan().Slice(1).ToArray();
            var signature = Neo.Cryptography.Crypto.Default.Sign(data, key.PrivateKey, publicKey);
            return (signature, key.PublicKey.EncodePoint(true));
        }

        public static CancellationTokenSource RunBlockchain(string directory, ExpressChain chain, int index, uint secondsPerBlock, TextWriter writer, ushort startingPort = 0)
        {
            if (startingPort != 0)
            {
                foreach (var consensusNode in chain.ConsensusNodes)
                {

                    consensusNode.TcpPort = startingPort++;
                    consensusNode.WebSocketPort = startingPort++;
                    consensusNode.RpcPort = startingPort++;
                }
            }

            chain.InitializeProtocolSettings(secondsPerBlock);

            var node = chain.ConsensusNodes[index];

#pragma warning disable IDE0067 // Dispose objects before losing scope
            // NodeUtility.Run disposes the store when it's done
            return NodeUtility.Run(new RocksDbStore(directory), node, writer);
#pragma warning restore IDE0067 // Dispose objects before losing scope
        }
    }
}
