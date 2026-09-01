using DbExplorer.Services;

namespace DbExplorer;

/// <summary>
/// Tiny offline helper commands, run instead of the web host when the first argument
/// matches. Used to mint the secrets that go into <c>appsettings.json</c> so an admin
/// never has to hand-edit <c>Program.cs</c> to do it.
///
///   dotnet run --project DbExplorer -- hash "YourPassword"
///   dotnet run --project DbExplorer -- totp alice [issuer]
/// </summary>
public static class Cli
{
    /// <summary>
    /// Returns true when it handled the arguments and the caller should exit without
    /// starting the web host.
    /// </summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0) return false;

        switch (args[0].ToLowerInvariant())
        {
            case "hash":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("usage: dotnet run --project DbExplorer -- hash \"<password>\"");
                    Environment.ExitCode = 2;
                    return true;
                }
                // Note: BCryptHelper is a legacy name; it produces PBKDF2-SHA256 hashes ("pbkdf2:...").
                Console.WriteLine(BCryptHelper.Hash(args[1]));
                return true;

            case "totp":
            {
                var account = args.Length >= 2 ? args[1] : "user";
                var issuer = args.Length >= 3 ? args[2] : "DbExplorer";
                var secret = TotpHelper.GenerateSecret();
                Console.WriteLine();
                Console.WriteLine($"  TotpSecret : {secret}");
                Console.WriteLine($"  otpauth    : {TotpHelper.BuildOtpAuthUri(secret, account, issuer)}");
                Console.WriteLine($"  current    : {TotpHelper.CurrentCode(secret)}  (changes every 30s — for a quick self-check)");
                Console.WriteLine();
                Console.WriteLine("  Paste TotpSecret into the user's entry under DbExplorer:Users, then add the");
                Console.WriteLine("  otpauth URI to an authenticator app (most apps take it as a QR or manual key).");
                Console.WriteLine();
                return true;
            }

            default:
                return false;
        }
    }
}
