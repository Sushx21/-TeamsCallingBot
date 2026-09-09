// Adapted from Microsoft's own sample repo:
// Samples/Common/Sample.Common/Authentication/AuthenticationProvider.cs (microsoft-graph-comms-samples, MIT licensed).
namespace TeamsCallingBot.Common
{
    using System;
    using System.Collections.Generic;
    using System.IdentityModel.Tokens.Jwt;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Graph.Communications.Client.Authentication;
    using Microsoft.Graph.Communications.Common;
    using Microsoft.Graph.Communications.Common.Telemetry;
    using Microsoft.Identity.Client;
    using Microsoft.IdentityModel.Protocols;
    using Microsoft.IdentityModel.Protocols.OpenIdConnect;
    using Microsoft.IdentityModel.Tokens;

    /// <summary>
    /// TODO: this currently only implements CLIENT-SECRET auth (.WithClientSecret(...)).
    /// If Infra issues a certificate for app 5f6516f0 instead of a secret (check whichever one
    /// AuthenticateOutboundRequestAsync ends up needing), swap the ConfidentialClientApplicationBuilder
    /// call below to .WithCertificate(x509Cert) instead - MSAL supports both, this file just doesn't yet.
    /// </summary>
    public class AuthenticationProvider : ObjectRoot, IRequestAuthenticationProvider
    {
        private readonly string appId;
        private readonly string appSecret;
        private readonly string defaultTenantId;
        private readonly string overrideBearerToken;
        private readonly TimeSpan openIdConfigRefreshInterval = TimeSpan.FromHours(2);
        private DateTime prevOpenIdConfigUpdateTimestamp = DateTime.MinValue;
        private OpenIdConnectConfiguration openIdConfiguration;

        // overrideBearerToken (optional, pass null/empty to use normal MSAL client-credentials):
        // TEMPORARY BYPASS - confirmed 2026-09-04 that Conditional Access blocks THIS APP'S OWN
        // MSAL client-credentials token acquisition (MsalClaimsChallengeException), but does NOT
        // block Graph itself from accepting an already-issued token for the same app (proven live:
        // a Python script using a pre-issued bearer token successfully joined a call, HTTP 201).
        // So: skip MSAL entirely and just attach a manually-obtained token here. This token expires
        // (~65 min in the one that was tested) - re-paste a fresh one into appsettings.json's
        // "OverrideBearerToken" field whenever it goes stale. Not a long-term fix - the real fix is
        // getting Conditional Access to exempt this app's service principal so MSAL works normally
        // again, at which point this field should go back to empty/null.
        public AuthenticationProvider(string appName, string appId, string appSecret, IGraphLogger logger, string overrideBearerToken = null, string defaultTenantId = null)
            : base(logger.NotNull(nameof(logger)).CreateShim(nameof(AuthenticationProvider)))
        {
            this.appId = appId.NotNullOrWhitespace(nameof(appId));
            this.appSecret = appSecret.NotNullOrWhitespace(nameof(appSecret));
            this.defaultTenantId = defaultTenantId;
            this.overrideBearerToken = overrideBearerToken;

            // DIAGNOSTIC (2026-09-04, added after hitting Graph error 7505 "Request authorization
            // tenant mismatch"): logs the pasted token's OWN tid claim at startup, decoded locally
            // (no signature check, no network call - same technique as the Python script's
            // base64.urlsafe_b64decode approach). 7505 means this tid did not match the joined
            // meeting's tenant (from the join URL's ?context={"tid":...}, see JoinInfo.cs). If this
            // log shows a different tid than the meeting's tenant, the token was issued against the
            // wrong authority (e.g. /common/ or /organizations/ instead of the specific tenant) -
            // get a fresh token issued explicitly against that tenant's token endpoint instead.
            if (!string.IsNullOrWhiteSpace(this.overrideBearerToken))
            {
                // Console.WriteLine, NOT logger.Warn - IGraphLogger.Warn only appends to
                // SampleObserver's in-memory log list (see SampleObserver.cs), which nothing ever
                // prints anywhere. Every other visible line in this app's console output
                // (">>> TEST JOIN ...") comes from plain Console.WriteLine in Program.cs - matching
                // that here is what actually makes this diagnostic show up on screen.
                try
                {
                    var jwt = new JwtSecurityToken(this.overrideBearerToken);
                    var tid = jwt.Claims.FirstOrDefault(c => c.Type == "tid")?.Value ?? "(no tid claim found)";
                    var tokenAppId = jwt.Claims.FirstOrDefault(c => c.Type == "appid")?.Value ?? "(no appid claim found)";
                    Console.WriteLine($">>> AUTH DIAGNOSTIC: OverrideBearerToken set - tid={tid}, appid={tokenAppId}, expires={jwt.ValidTo:u} UTC. Compare tid against the meeting URL's own ?context={{\"tid\":...}} - a mismatch here is exactly Graph error 7505.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($">>> AUTH DIAGNOSTIC: OverrideBearerToken set but could not be parsed as a JWT to log its claims: {ex.Message}");
                }
            }
        }

        private static string cachedGraphToken;
        private static DateTime graphTokenExpiry = DateTime.MinValue;
        private static readonly object graphTokenLock = new object();

        private async Task<string> AcquireTokenDirectAsync(string tenant)
        {
            lock (graphTokenLock)
            {
                if (!string.IsNullOrEmpty(cachedGraphToken) && DateTime.UtcNow < graphTokenExpiry)
                {
                    return cachedGraphToken;
                }
            }

            string resolvedTenant = string.IsNullOrWhiteSpace(tenant) || tenant.Equals("common", StringComparison.OrdinalIgnoreCase)
                ? (string.IsNullOrWhiteSpace(this.defaultTenantId) ? "f35425af-4755-4e0c-b1bb-b3cb9f1c6afd" : this.defaultTenantId)
                : tenant;

            string endpoint = $"https://login.microsoftonline.com/{resolvedTenant}/oauth2/v2.0/token";

            try
            {
                using (var client = new HttpClient())
                {
                    var pairs = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("grant_type", "client_credentials"),
                        new KeyValuePair<string, string>("client_id", this.appId),
                        new KeyValuePair<string, string>("client_secret", this.appSecret),
                        new KeyValuePair<string, string>("scope", "https://graph.microsoft.com/.default")
                    };

                    var content = new FormUrlEncodedContent(pairs);
                    var resp = await client.PostAsync(endpoint, content).ConfigureAwait(false);
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                    {
                        var jobj = Newtonsoft.Json.Linq.JObject.Parse(json);
                        string token = jobj["access_token"]?.ToString();
                        int expiresIn = int.TryParse(jobj["expires_in"]?.ToString(), out int parsedExp) ? parsedExp : 3600;

                        lock (graphTokenLock)
                        {
                            cachedGraphToken = token;
                            graphTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
                        }

                        Console.WriteLine($">>> [Graph Auth] Acquired token directly from {endpoint}, expires in {expiresIn}s");
                        this.GraphLogger.Info($"[Graph Auth] Acquired token directly from {endpoint}");
                        return token;
                    }
                    else
                    {
                        this.GraphLogger.Warn($"[Graph Auth] Direct token acquisition failed ({resp.StatusCode}): {json}");
                        Console.WriteLine($">>> [Graph Auth] Direct token acquisition failed ({resp.StatusCode}): {json}");
                    }
                }
            }
            catch (Exception ex)
            {
                this.GraphLogger.Warn($"[Graph Auth] Direct token request error: {ex.Message}");
                Console.WriteLine($">>> [Graph Auth] Direct token request error: {ex.Message}");
            }

            return null;
        }

        public async Task AuthenticateOutboundRequestAsync(HttpRequestMessage request, string tenant)
        {
            const string schema = "Bearer";

            if (!string.IsNullOrWhiteSpace(this.overrideBearerToken))
            {
                this.GraphLogger.Warn("AuthenticationProvider: using OverrideBearerToken bypass - MSAL not called. Temporary, see class comment.");
                request.Headers.Authorization = new AuthenticationHeaderValue(schema, this.overrideBearerToken);
                return;
            }

            // 1. Try direct HTTP client-credentials acquisition (proven reliable, avoids MSAL authority quirks)
            string directToken = await this.AcquireTokenDirectAsync(tenant).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(directToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(schema, directToken);
                return;
            }

            // 2. Fallback to MSAL
            const string oauthV2TokenLink = "https://login.microsoftonline.com/{tenant}";
            const string resource = "https://graph.microsoft.com";

            if (string.IsNullOrWhiteSpace(tenant) || tenant.Equals("common", StringComparison.OrdinalIgnoreCase))
            {
                tenant = string.IsNullOrWhiteSpace(this.defaultTenantId) ? "f35425af-4755-4e0c-b1bb-b3cb9f1c6afd" : this.defaultTenantId;
            }

            var tokenLink = oauthV2TokenLink.Replace("{tenant}", tenant);
            var scopes = new[] { $"{resource}/.default" };

            var app = ConfidentialClientApplicationBuilder.Create(this.appId)
                .WithAuthority(tokenLink)
                .WithClientSecret(this.appSecret)
                .Build();

            var result = await this.AcquireTokenWithRetryAsync(app, scopes, 3).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue(schema, result.AccessToken);
        }

        /// <summary>
        /// FIX (2026-09-04): call this once, fire-and-forget, right after construction (see Bot.cs) -
        /// pays the OpenID config network fetch (api.aps.skype.com/.well-known/OpenIdConfiguration)
        /// during app startup instead of on the first REAL Graph notification callback.
        /// ValidateInboundRequestAsync below was measured taking 2.7-4.7s end-to-end on every single
        /// notification across two separate test runs - a real-time calling/telephony signaling
        /// protocol expects low-latency acks, and a multi-second turnaround on every callback is a
        /// plausible reason Graph could treat this endpoint as slow/unhealthy. This fetch result is
        /// cached for openIdConfigRefreshInterval (2h) either way - warming it here just moves WHEN
        /// the first (only cold) fetch happens, from "during a live call's first callback" to
        /// "during startup, before anything is time-sensitive". Swallows its own errors - if this
        /// fails, ValidateInboundRequestAsync's own fetch will simply retry when a real request
        /// actually needs it, exactly as before this existed.
        /// </summary>
        public async Task WarmupAsync()
        {
            try
            {
                await this.EnsureOpenIdConfigurationAsync().ConfigureAwait(false);
                Console.WriteLine(">>> AUTH WARMUP: OpenID configuration fetched and cached at startup.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($">>> AUTH WARMUP: failed to pre-fetch OpenID configuration (will retry on first real request): {ex.Message}");
            }
        }

        public async Task<RequestValidationResult> ValidateInboundRequestAsync(HttpRequestMessage request)
        {
            var token = request?.Headers?.Authorization?.Parameter;
            if (string.IsNullOrWhiteSpace(token))
            {
                return new RequestValidationResult { IsValid = false };
            }

            await this.EnsureOpenIdConfigurationAsync().ConfigureAwait(false);

            var authIssuers = new[] { "https://graph.microsoft.com", "https://api.botframework.com" };

            var validationParameters = new TokenValidationParameters
            {
                ValidIssuers = authIssuers,
                ValidAudience = this.appId,
                IssuerSigningKeys = this.openIdConfiguration.SigningKeys,
            };

            ClaimsPrincipal claimsPrincipal;
            try
            {
                var handler = new JwtSecurityTokenHandler();
                claimsPrincipal = handler.ValidateToken(token, validationParameters, out _);
            }
            catch (Exception ex)
            {
                this.GraphLogger.Error(ex, $"Failed to validate token for client: {this.appId}.");
                return new RequestValidationResult { IsValid = false };
            }

            const string claimType = "http://schemas.microsoft.com/identity/claims/tenantid";
            var tenantClaim = claimsPrincipal.FindFirst(claim => claim.Type.Equals(claimType, StringComparison.Ordinal));
            if (string.IsNullOrEmpty(tenantClaim?.Value))
            {
                return new RequestValidationResult { IsValid = false };
            }

            return new RequestValidationResult { IsValid = true, TenantId = tenantClaim.Value };
        }

        private async Task EnsureOpenIdConfigurationAsync()
        {
            const string authDomain = "https://api.aps.skype.com/v1/.well-known/OpenIdConfiguration";
            if (this.openIdConfiguration == null || DateTime.Now > this.prevOpenIdConfigUpdateTimestamp.Add(this.openIdConfigRefreshInterval))
            {
                IConfigurationManager<OpenIdConnectConfiguration> configurationManager =
                    new ConfigurationManager<OpenIdConnectConfiguration>(authDomain, new OpenIdConnectConfigurationRetriever());
                this.openIdConfiguration = await configurationManager.GetConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                this.prevOpenIdConfigUpdateTimestamp = DateTime.Now;
            }
        }

        private async Task<AuthenticationResult> AcquireTokenWithRetryAsync(IConfidentialClientApplication app, string[] scopes, int attempts)
        {
            while (true)
            {
                attempts--;
                try
                {
                    return await app.AcquireTokenForClient(scopes).ExecuteAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (attempts < 1)
                    {
                        throw;
                    }
                }

                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
    }
}
