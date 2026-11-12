using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

namespace CryptoPaymentEngine.Api.OperationsApi.Services;

/// <summary>
/// <c>CryptoPaymentEngine.Api.OperationsApi.dll --bootstrap-admin &lt;username&gt;</c> — creates the first staff
/// account on an empty database, prints its one-time password to the console, writes an audit entry, and EXITS
/// without starting the web server. Refused once any staff account exists (§ <see cref="StaffBootstrapService"/>).
/// The password is printed to stdout only — never logged — so it does not end up in the log files.
/// </summary>
public static class BootstrapAdminCommand
{
    public const string Flag = "--bootstrap-admin";

    /// <summary>Returns null when the flag is absent (start the web host as normal), otherwise the exit code.</summary>
    public static async Task<int?> TryRunAsync(WebApplication app, string[] args)
    {
        var at = Array.IndexOf(args, Flag);
        if (at < 0)
            return null;

        if (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Usage: {Flag} <username>");
            return 2;
        }

        var username = args[at + 1].Trim();

        await using var scope = app.Services.CreateAsyncScope();
        var bootstrap = scope.ServiceProvider.GetRequiredService<IStaffBootstrapService>();
        var result = await bootstrap.BootstrapFirstAdminAsync(username);
        if (result.IsFailure)
        {
            Console.Error.WriteLine($"Refused: {result.Error!.Message} ({result.Error.Code})");
            return 1;
        }

        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        await audit.LogAsync(new LogAuditEntryCommand(
            result.Value.StaffUserId, result.Value.Username, "account.bootstrapped", "StaffUser",
            result.Value.StaffUserId.ToString(),
            $"first admin created from the server console on {Environment.MachineName}", IpAddress: null));

        Console.WriteLine();
        Console.WriteLine("First admin account created.");
        Console.WriteLine($"  Username: {result.Value.Username}");
        Console.WriteLine($"  Password: {result.Value.Password}");
        Console.WriteLine();
        Console.WriteLine("This password is shown ONCE and is not stored anywhere in clear. Sign in to the back");
        Console.WriteLine("office now: you will be asked to set up an authenticator app before anything else.");
        return 0;
    }
}
