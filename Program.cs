using Microsoft.Extensions.Configuration;
using MyBot.Application;
using MyBot.Core;
using MyBot.Exchange;
using MyBot.Persistence;

namespace MyBot;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            return await ExecuteAsync(args, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Canceled. A submitted request may still have executed. Use strategy-status --live for RSI orders, or list and status for transfers, before any further action.");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is InvalidOperationException or ArgumentException or WazirXApiException or KeyNotFoundException or InvalidDataException
                ? error.Message
                : "Operation stopped because of a configuration, network, or storage error. No automatic resubmission will occur.");
            Console.Error.WriteLine("If a money request was submitted, use strategy-status --live for RSI orders, or list and status for transfers. Do not delete or reset the journal.");
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }
        var command = args[0];
        var requiresId = command is "status" or "withdraw" or "cancel" or "confirm-deposit";
        var isStrategyCommand = command is "strategy" or "strategy-status" or "strategy-cancel";
        if (command is not ("preview" or "routes" or "buy" or "list" or "status" or "withdraw" or "cancel" or "confirm-deposit") && !isStrategyCommand)
            throw new ArgumentException("Unknown command. Use help.");
        var index = 1;
        string? id = null;
        if (requiresId)
        {
            if (args.Length <= index || !Guid.TryParseExact(args[index], "N", out _))
                throw new ArgumentException("Supply the 32-character operation ID printed by buy or list.");
            id = args[index++];
        }
        var settings = Path.GetFullPath("botsettings.local.json");
        var live = false;
        var once = false;
        for (; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--live" when !live:
                    live = true;
                    break;
                case "--once" when !once && command == "strategy":
                    once = true;
                    break;
                case "--settings" when index + 1 < args.Length:
                    settings = Path.GetFullPath(args[++index]);
                    break;
                default:
                    throw new ArgumentException("Invalid arguments. Use help.");
            }
        }
        var isMoneyCommand = command is "buy" or "withdraw" or "cancel" or "confirm-deposit";
        if (isMoneyCommand && !live)
            throw new InvalidOperationException("This command requires --live and interactive confirmation. Use preview for a read-only estimate.");
        if (!isMoneyCommand && live && !isStrategyCommand)
            throw new ArgumentException("--live is only accepted for money commands or selecting the live strategy.");
        if (command == "strategy-cancel" && !live)
            throw new ArgumentException("strategy-cancel requires --live and interactive confirmation.");

        var directory = Path.GetDirectoryName(settings)!;
        var configuration = new ConfigurationBuilder()
            .SetBasePath(directory)
            .AddJsonFile(Path.GetFileName(settings), optional: false, reloadOnChange: false)
            .AddUserSecrets(typeof(Program).Assembly, optional: true)
            .AddEnvironmentVariables("MYBOT_")
            .Build();
        var options = configuration.GetSection("Bot").Get<BotOptions>() ?? new BotOptions();
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            throw new InvalidOperationException("A stable local application data directory is required for the journal.");
        var dataDirectory = Path.Combine(localData, "MyBot", "WazirX");
        using var processLock = ProcessLock.Acquire(Path.Combine(dataDirectory, "mybot.lock"));
        using var store = new SqliteOperationStore(Path.Combine(dataDirectory, "mybot.db"));
        if (command == "list")
        {
            foreach (var operation in store.List())
                Console.WriteLine($"{operation.Id}: {operation.Stage}; created {operation.CreatedAt:O}; network={operation.Network}");
            return 0;
        }
        if (isMoneyCommand && store.List().Any(o => o.Strategy is { Paper: false } strategy &&
            (strategy.OwnedUsdt > 0 || strategy.Pending is not null)))
            throw new InvalidOperationException("The live RSI strategy has holdings or an unresolved order. Use strategy-status --live before other money commands.");

        var apiKey = configuration["WazirX:ApiKey"];
        var apiSecret = configuration["WazirX:ApiSecret"];
        if (isStrategyCommand && !live)
        {
            apiKey = "paper-market-data-only";
            apiSecret = "paper-market-data-only";
        }
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            throw new InvalidOperationException("Set WazirX:ApiKey and WazirX:ApiSecret with User Secrets or MYBOT_ environment variables. Do not put keys in the settings file.");
        using var http = WazirXClient.CreateHttpClient();
        var client = new WazirXClient(http, apiKey, apiSecret);
        if (isStrategyCommand)
        {
            var strategy = new RsiStrategyWorkflow(options, client, client, store, Console.Out, Confirm);
            switch (command)
            {
                case "strategy": await strategy.RunAsync(live, once, cancellationToken); break;
                case "strategy-status": await strategy.StatusAsync(live, cancellationToken); break;
                case "strategy-cancel": await strategy.CancelAsync(live, cancellationToken); break;
            }
            return 0;
        }
        if (command == "routes")
        {
            Console.WriteLine("Reading WazirX USDT withdrawal network information...");
            var networks = await client.GetNetworksAsync(cancellationToken);
            foreach (var network in networks)
                Console.WriteLine($"Network={network.Network}; withdrawals={network.WithdrawEnabled}; feeUSDT={network.Fee}; minimumUSDT={network.Minimum}; maximumUSDT={network.Maximum}; step={network.IntegerMultiple}; memoRequired={network.MemoRequired}");
            Console.WriteLine("Reading the WazirX USDT withdrawal address book...");
            var addresses = await client.GetAddressesAsync(cancellationToken);
            foreach (var address in addresses)
                Console.WriteLine($"AddressBookId={address.Id}; network={address.Network}; address={address.Address}; allowed={address.WithdrawAllowed}; universal={address.IsUniversal}; allowedAfter={address.AllowedAfter:O}");
            Console.WriteLine("Read-only discovery. Confirm receiving-network support and deposit minimum at the receiving exchange before configuring a route.");
            return 0;
        }
        var workflow = new BotWorkflow(options, client, store, Console.Out, Confirm);
        switch (command)
        {
            case "preview": await workflow.PreviewAsync(cancellationToken); break;
            case "buy": await workflow.BuyAsync(cancellationToken); break;
            case "status": await workflow.StatusAsync(id!, cancellationToken); break;
            case "withdraw": await workflow.WithdrawAsync(id!, cancellationToken); break;
            case "cancel": await workflow.CancelAsync(id!, cancellationToken); break;
            case "confirm-deposit": workflow.ConfirmDeposit(id!); break;
        }
        return 0;
    }

    private static bool Confirm(string phrase)
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("Live commands require an interactive terminal. Redirected confirmation is refused.");
        Console.WriteLine($"Type exactly: {phrase}");
        return string.Equals(Console.ReadLine(), phrase, StringComparison.Ordinal);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("MyBot: WazirX USDT purchase and transfer, one operation at a time.");
        Console.WriteLine("preview                         Read-only account/price/fee checks; never sends an order.");
        Console.WriteLine("routes                          Read-only network fees and saved address-book IDs.");
        Console.WriteLine("buy --live                      Confirm and submit one capped limit buy.");
        Console.WriteLine("list                            Show the local journal (no API request).");
        Console.WriteLine("status <id>                     Reconcile the saved operation using read-only API calls.");
        Console.WriteLine("withdraw <id> --live             Confirm one withdrawal after fills are reconciled.");
        Console.WriteLine("cancel <id> --live               Confirm cancellation of an open buy; no automatic sell.");
        Console.WriteLine("confirm-deposit <id> --live      Record your manual receiving-exchange deposit verification.");
        Console.WriteLine("strategy [--once]               Paper trade 30-minute RSI(10): buy below 30, sell above 70.");
        Console.WriteLine("strategy --live [--once]        Confirm automated spot trading; also requires EnableLiveTrading=true.");
        Console.WriteLine("strategy-status [--live]        Show/reconcile the paper or live strategy journal.");
        Console.WriteLine("strategy-cancel --live          Confirm cancellation of the strategy's pending limit order.");
        Console.WriteLine("All commands accept --settings <path>; default: ./botsettings.local.json.");
        Console.WriteLine("Journal: <LocalApplicationData>/MyBot/WazirX/mybot.db. Keep it intact across restarts.");
    }
}
