using System;
using System.ComponentModel.DataAnnotations;
using System.IO.Abstractions;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using static Neo.SmartContract.Helper;

namespace NeoExpress.Commands
{
    partial class ContractCommand
    {
        [Command(Name = "hash", Description = "Get contract hash for contract path and deployment account")]
        internal class Hash
        {
            readonly IExpressChain chain;

            public Hash(IExpressChain chain)
            {
                this.chain = chain;
            }

            public Hash(CommandLineApplication app)
            {
                this.chain = app.GetExpressFile();
            }

            [Argument(0, Description = "Path to contract .nef file")]
            [Required]
            internal string Contract { get; init; } = string.Empty;

            [Argument(1, Description = "Account that would deploy the contract")]
            [Required]
            internal string Account { get; init; } = string.Empty;

            internal Task<int> OnExecuteAsync(CommandLineApplication app)
                => app.ExecuteAsync(this.ExecuteAsync);

            internal async Task ExecuteAsync(IFileSystem fileSystem, IConsole console)
            {
                var accountHash = chain.ResolveAccountHash(Account);
                var (nefFile, manifest) = await fileSystem.LoadContractAsync(Contract).ConfigureAwait(false);

                var contractHash = GetContractHash(accountHash, nefFile.CheckSum, manifest.Name);
                await console.Out.WriteLineAsync($"{contractHash}").ConfigureAwait(false);
            }
        }
    }
}
