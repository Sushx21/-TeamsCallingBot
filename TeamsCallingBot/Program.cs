namespace TeamsCallingBot
{
    using System;
    using System.Linq;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using TeamsCallingBot.Bot;

    /// <summary>
    /// Entry point. Self-hosts via Kestrel - no Windows Service wrapper yet.
    /// Once this is proven working against a real meeting, wrap it as a Windows Service
    /// (Topshelf, same as Microsoft's IncidentBot sample) for the "auto-restart on crash" requirement.
    /// </summary>
    public class Program
    {
        public static void Main(string[] args)
        {
            var host = BuildWebHost(args);

            // ONE-TIME TEST TRIGGER: paste a real meeting join URL into appsettings.json's
            // "Bot:TestMeetingJoinUrl" field (see that file - it's the very next line after
            // CertificateThumbprint) and this fires it automatically 5 seconds after startup,
            // logging the outcome straight to this console window. No Postman/curl needed.
            //
            // EXPECTED RIGHT NOW, before the VM/cert/public IP exist: this will fail - most likely
            // at Bot construction itself (CertificateThumbprint is still a TODO placeholder, so
            // MediaPlatform initialization has nothing real to find in the certificate store). That
            // failure is expected and NOT a bug to chase - it will resolve once appsettings.json's
            // TODO fields are filled in with the VM's real values.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                var config = host.Services.GetRequiredService<IConfiguration>();
                var testUrl = config["Bot:TestMeetingJoinUrl"];
                if (string.IsNullOrWhiteSpace(testUrl))
                {
                    return;
                }

                Console.WriteLine($">>> TEST JOIN starting for: {testUrl}");
                try
                {
                    var bot = host.Services.GetRequiredService<TeamsCallingBot.Bot.Bot>();
                    var call = await bot.JoinCallAsync(testUrl).ConfigureAwait(false);
                    Console.WriteLine($">>> TEST JOIN accepted. Call id: {call.Id}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($">>> TEST JOIN FAILED: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine(ex.ToString());
                }
            });

            host.Run();
        }

        public static IWebHost BuildWebHost(string[] args)
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false)
                .AddEnvironmentVariables()
                .Build();

            var thumbprint = config["Bot:CertificateThumbprint"];
            var cert = LoadCertificateByThumbprint(thumbprint);

            return WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>()
                .UseKestrel(options =>
                {
                    // RESOLVED: Kestrel needs its own certificate to terminate TLS for this HTTPS
                    // listener - UseUrls("https://...") alone falls back to the untrusted ASP.NET Core
                    // dev cert, which Graph's webhook caller rejects (this was the IOException/
                    // Win32Exception "decryption operation failed" seen when Graph tried to call back).
                    // Same win-acme cert (by thumbprint) used for MediaPlatformInstanceSettings in Bot.cs
                    // is reused here so both the media handshake and this webhook listener agree.
                    // Deliberately just ConfigureHttpsDefaults (not an explicit options.Listen) so the
                    // single https://0.0.0.0:443 endpoint below (UseUrls) is the only port 443 binding -
                    // adding a second explicit Listen for the same port would throw "address in use".
                    options.ConfigureHttpsDefaults(https => https.ServerCertificate = cert);
                })
                .UseUrls("https://0.0.0.0:443") // matches appsettings.json's Bot:InstancePublicPort - keep these in sync manually
                .Build();
        }

        private static X509Certificate2 LoadCertificateByThumbprint(string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint) || thumbprint.StartsWith("TODO", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Bot:CertificateThumbprint in appsettings.json is missing/still a TODO placeholder - " +
                    "Kestrel cannot start its HTTPS listener without a real certificate. Run win-acme (see " +
                    "STEP8_WINACME.md) and paste the real thumbprint in first.");
            }

            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly);
                var match = store.Certificates
                    .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
                    .OfType<X509Certificate2>()
                    .FirstOrDefault();

                if (match == null)
                {
                    throw new InvalidOperationException(
                        $"No certificate with thumbprint '{thumbprint}' found in Cert:\\LocalMachine\\My. " +
                        "win-acme installs there by default - confirm the thumbprint was copied correctly.");
                }

                return match;
            }
        }
    }
}
