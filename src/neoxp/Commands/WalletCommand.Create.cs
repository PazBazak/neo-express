using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO.Abstractions;
using McMaster.Extensions.CommandLineUtils;
using Neo.BlockchainToolkit;
using Neo.BlockchainToolkit.Models;
using NeoExpress.Models;
using Newtonsoft.Json;
using Nito.Disposables;

namespace NeoExpress.Commands
{
    partial class WalletCommand
    {
        [Command("create", Description = "Create neo-express wallet")]
        internal class Create
        {
            readonly IFileSystem fileSystem;

            public Create(IFileSystem fileSystem)
            {
                this.fileSystem = fileSystem;
            }

            [Argument(0, Description = "Wallet name")]
            [Required]
            internal string Name { get; init; } = string.Empty;

            [Option(Description = "Overwrite existing data")]
            internal bool Force { get; }

            [Option(Description = "Output as JSON")]
            internal bool Json { get; init; } = false;

            [Option(Description = "Path to neo-express data file")]
            internal string Input { get; init; } = string.Empty;

            internal ExpressWallet Execute()
            {
                var (chain, chainPath) = fileSystem.LoadExpressChain(Input);

                if (chain.IsReservedName(Name))
                {
                    throw new Exception($"{Name} is a reserved name. Choose a different wallet name.");
                }

                var existingWallet = chain.GetWallet(Name);
                if (existingWallet != null)
                {
                    if (!Force)
                    {
                        throw new Exception($"{Name} dev wallet already exists. Use --force to overwrite.");
                    }

                    chain.Wallets.Remove(existingWallet);
                }

                var wallet = new DevWallet(chain.GetProtocolSettings(), Name);
                var account = wallet.CreateAccount();
                account.IsDefault = true;

                var expressWallet = wallet.ToExpressWallet();
                chain.Wallets ??= new List<ExpressWallet>(1);
                chain.Wallets.Add(expressWallet);
                fileSystem.SaveChain(chain, chainPath);
                return expressWallet;
            }

            internal int OnExecute(CommandLineApplication app, IConsole console)
            {
                try
                {
                    var wallet = Execute();
                    if (Json)
                    {
                        using var writer = new JsonTextWriter(console.Out) { Formatting = Formatting.Indented };
                        using var _ = writer.WriteStartObjectAuto();
                        writer.WriteWallet(wallet);
                    }
                    else
                    {
                        console.Out.WriteWallet(wallet);
                        console.WriteLine("Note: The private keys for the accounts in this wallet are *not* encrypted.");
                        console.WriteLine("      Do not use these accounts on MainNet or in any other system where security is a concern.");
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    app.WriteException(ex);
                    return 1;
                }
            }


        }
    }
}
