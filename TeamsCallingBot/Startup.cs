namespace TeamsCallingBot
{
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Graph.Communications.Common.Telemetry;
    using TeamsCallingBot.Bot;
    using TeamsCallingBot.Common;
    using TeamsCallingBot.Config;

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            var logger = new GraphLogger(typeof(Startup).Assembly.GetName().Name);
            var observer = new SampleObserver(logger);

            services.AddSingleton(observer);
            services.AddSingleton<IGraphLogger>(logger);

            // TODO: fill in BotOptions from appsettings.json / environment variables once VM values are known.
            var options = BotOptions.LoadFromConfig();
            services.AddSingleton(options);

            services.AddSingleton(sp => new TeamsCallingBot.Bot.Bot(sp.GetRequiredService<BotOptions>(), sp.GetRequiredService<IGraphLogger>()));

            services.AddMvc();
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseMvc();
        }
    }
}
