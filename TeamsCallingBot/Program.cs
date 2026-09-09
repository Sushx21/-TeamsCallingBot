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

            // PERSISTENT AUTO-JOIN & SCHEDULED MEETING ENGINE:
            // Checks every 15 seconds for:
            // 1. Initial TestMeetingJoinUrl / TestMeetingJoinUrls
            // 2. Scheduled meetings in "Bot:ScheduledMeetings" matching current UTC time window
            // 3. Allows manual chat commands (!join, #joincall) to execute concurrently at any time
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var bot = host.Services.GetRequiredService<TeamsCallingBot.Bot.Bot>();
                var joinedUrls = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                while (true)
                {
                    try
                    {
                        var config = host.Services.GetRequiredService<IConfiguration>();
                        var urlsToJoin = new System.Collections.Generic.List<string>();

                        var testUrl = config["Bot:TestMeetingJoinUrl"];
                        if (!string.IsNullOrWhiteSpace(testUrl))
                        {
                            var split = testUrl.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                               .Select(u => u.Trim())
                                               .Where(u => !string.IsNullOrWhiteSpace(u));
                            urlsToJoin.AddRange(split);
                        }

                        var multiSection = config.GetSection("Bot:TestMeetingJoinUrls").GetChildren();
                        foreach (var child in multiSection)
                        {
                            if (!string.IsNullOrWhiteSpace(child.Value))
                            {
                                urlsToJoin.Add(child.Value.Trim());
                            }
                        }

                        // Scheduled meetings section: Bot:ScheduledMeetings: [ { MeetingJoinUrl, ScheduledStartTimeUtc } ]
                        var scheduledSection = config.GetSection("Bot:ScheduledMeetings").GetChildren();
                        foreach (var item in scheduledSection)
                        {
                            var sUrl = item["MeetingJoinUrl"]?.Trim();
                            var sTimeStr = item["ScheduledStartTimeUtc"]?.Trim();

                            if (!string.IsNullOrWhiteSpace(sUrl))
                            {
                                if (DateTime.TryParse(sTimeStr, out var sTimeUtc))
                                {
                                    // If within 2 minutes before scheduled start time or up to 60 minutes after
                                    var diff = DateTime.UtcNow - sTimeUtc.ToUniversalTime();
                                    if (diff.TotalMinutes >= -2.0 && diff.TotalMinutes <= 60.0)
                                    {
                                        if (!joinedUrls.Contains(sUrl))
                                        {
                                            Console.WriteLine($">>> [Scheduler] Scheduled meeting trigger matched! Scheduled: {sTimeUtc:u}, Now: {DateTime.UtcNow:u}. Auto-joining: {sUrl}");
                                            urlsToJoin.Add(sUrl);
                                        }
                                    }
                                }
                                else
                                {
                                    urlsToJoin.Add(sUrl);
                                }
                            }
                        }

                        urlsToJoin = urlsToJoin.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                        foreach (var url in urlsToJoin)
                        {
                            if (joinedUrls.Contains(url))
                            {
                                continue;
                            }

                            joinedUrls.Add(url);
                            _ = Task.Run(async () =>
                            {
                                Console.WriteLine($">>> AUTO/SCHEDULED JOIN starting for: {url}");
                                try
                                {
                                    var call = await bot.JoinCallAsync(url).ConfigureAwait(false);
                                    Console.WriteLine($">>> AUTO/SCHEDULED JOIN accepted. Call id: {call.Id}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($">>> AUTO/SCHEDULED JOIN FAILED for {url}: {ex.GetType().Name}: {ex.Message}");
                                    joinedUrls.Remove(url); // Allow retry
                                }
                            });
                        }
                    }
                    catch (Exception loopEx)
                    {
                        Console.WriteLine($">>> [Scheduler] Exception in scheduler loop: {loopEx.Message}");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
            });

            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try
                {
                    var bot = host.Services.GetService<TeamsCallingBot.Bot.Bot>();
                    bot?.Dispose();
                }
                catch { }
            };
            Console.CancelKeyPress += (s, e) =>
            {
                try
                {
                    var bot = host.Services.GetService<TeamsCallingBot.Bot.Bot>();
                    bot?.Dispose();
                }
                catch { }
            };

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
