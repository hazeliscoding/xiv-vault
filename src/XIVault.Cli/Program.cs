using System.Text;
using XIVault.Cli;

Console.OutputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Let the running operation stop cleanly; backups and restores clean up after themselves.
    e.Cancel = true;
    cancellation.Cancel();
};

return await XivaultCli.RunAsync(args, cancellation.Token);
