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

                        bool autoJoin = false;
                        bool.TryParse(config["Bot:AutoJoinOnStartup"], out autoJoin);

                        if (autoJoin)
                        {
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
                        }

                        // Scheduled meetings from MeetingRegistryService (Power Automate & Calendar Forwarding)
                        var registry = host.Services.GetService<TeamsCallingBot.Common.MeetingRegistryService>();
                        if (registry != null)
                        {
                            var pending = registry.GetPendingMeetingsToJoin(DateTime.UtcNow);
                            foreach (var p in pending)
                            {
                                if (!joinedUrls.Contains(p.Id))
                                {
                                    joinedUrls.Add(p.Id);
                                    registry.MarkJoining(p.Id);
                                    _ = Task.Run(async () =>
                                    {
                                        Console.WriteLine($">>> [Scheduler] Triggering registered meeting: '{p.Subject}' (Video: {p.RecordVideo}) | {p.MeetingJoinUrl ?? p.ThreadId}");
                                        try
                                        {
                                            var call = !string.IsNullOrWhiteSpace(p.MeetingJoinUrl)
                                                ? await bot.JoinCallAsync(p.MeetingJoinUrl, p.RecordVideo).ConfigureAwait(false)
                                                : await bot.JoinCallByCoordinatesAsync(p.ThreadId, recordVideo: p.RecordVideo).ConfigureAwait(false);

                                            if (call != null)
                                            {
                                                registry.MarkJoined(p.Id, call.Id);
                                                Console.WriteLine($">>> [Scheduler] Registered meeting joined: '{p.Subject}' (CallId: {call.Id})");
                                            }
                                            else
                                            {
                                                Console.WriteLine($">>> [Scheduler] Registered meeting skipped (already locked by another instance): '{p.Subject}'");
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            registry.MarkFailed(p.Id, ex.Message);
                                            joinedUrls.Remove(p.Id);
                                            Console.WriteLine($">>> [Scheduler] Registered meeting join failed: '{p.Subject}': {ex.Message}");
                                        }
                                    });

                                    // Stagger scheduled joins
                                    await Task.Delay(2000).ConfigureAwait(false);
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
                                const int maxAttempts = 3;
                                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                                {
                                    try
                                    {
                                        var call = await bot.JoinCallAsync(url).ConfigureAwait(false);
                                        if (call != null)
                                        {
                                            Console.WriteLine($">>> AUTO/SCHEDULED JOIN accepted. Call id: {call.Id}");
                                        }
                                        else
                                        {
                                            Console.WriteLine($">>> AUTO/SCHEDULED JOIN skipped: Meeting already locked/active on another instance.");
                                        }
                                        return;
                                    }
                                    catch (Exception ex)
                                    {
                                        bool isServerError = ex.Message.Contains("500") || ex.Message.Contains("1203003") || ex.Message.Contains("Internal Server Error");
                                        if (attempt < maxAttempts && isServerError)
                                        {
                                            int backoff = attempt * 3;
                                            Console.WriteLine($">>> AUTO/SCHEDULED JOIN transient 500 error for {url} (attempt {attempt}/{maxAttempts}). Retrying in {backoff}s: {ex.Message}");
                                            await Task.Delay(TimeSpan.FromSeconds(backoff)).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            Console.WriteLine($">>> AUTO/SCHEDULED JOIN FAILED for {url}: {ex.GetType().Name}: {ex.Message}");
                                            joinedUrls.Remove(url); // Allow retry
                                            return;
                                        }
                                    }
                                }
                            });

                            // 2-second stagger between launching joins to eliminate Graph 500#1203003 SDP burst rate limit
                            await Task.Delay(2000).ConfigureAwait(false);
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
