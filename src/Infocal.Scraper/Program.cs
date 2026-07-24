using Infocal.Scraper;
using Infocal.Scraper.Services;
using Microsoft.Extensions.Hosting;
using Quartz;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        var cfg = ctx.Configuration;

        // ── HTTP clients ──
        services.AddHttpClient("CalendarApi", client =>
        {
            client.BaseAddress = new Uri(cfg["CalendarApi:BaseUrl"] ?? "http://localhost:5223");
            client.DefaultRequestHeaders.Add("X-Api-Key", cfg["CalendarApi:ApiKey"] ?? "dev-key-change-me");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<GomelMassSkatingScraperService>(client =>
        {
            client.BaseAddress = new Uri("https://gomel.hockey.by");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Infocal.Scraper/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // ── Quartz: two triggers for one job ──
        var aggressiveCron = cfg["ScrapeSchedule:AggressiveCron"] ?? "0 */30 * ? * SAT,SUN,MON";
        var lazyCron       = cfg["ScrapeSchedule:LazyCron"]       ?? "0 0 10 ? * TUE,WED,THU,FRI";

        services.AddQuartz(q =>
        {
            var jobKey = new JobKey("GomelMassSkatingJob");
            q.AddJob<GomelMassSkatingJob>(opts => opts.WithIdentity(jobKey));

            // Aggressive: every 30 min Sat–Mon (waiting for new schedule)
            q.AddTrigger(opts => opts
                .ForJob(jobKey)
                .WithIdentity("GomelAggressiveTrigger")
                .WithCronSchedule(aggressiveCron)
                .WithDescription("Сб–Пн: каждые 30 мин — ждём новое расписание"));

            // Lazy: once daily Tue–Fri (check for corrections)
            q.AddTrigger(opts => opts
                .ForJob(jobKey)
                .WithIdentity("GomelLazyTrigger")
                .WithCronSchedule(lazyCron)
                .WithDescription("Вт–Пт: раз в день — проверка на случай изменений"));
        });

        services.AddQuartzHostedService(opts =>
        {
            opts.WaitForJobsToComplete = true;
            opts.AwaitApplicationStarted = true;
        });
    })
    .Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var config = host.Services.GetRequiredService<IConfiguration>();

logger.LogInformation("🚀 Infocal.Scraper Worker запущен");
logger.LogInformation("   🔄 Агрессивный режим: {Cron}", config["ScrapeSchedule:AggressiveCron"]);
logger.LogInformation("   💤 Обычный режим:     {Cron}", config["ScrapeSchedule:LazyCron"]);

// ── Первый запуск сразу при старте ──
var schedulerFactory = host.Services.GetRequiredService<ISchedulerFactory>();
var scheduler = await schedulerFactory.GetScheduler();
await scheduler.TriggerJob(new JobKey("GomelMassSkatingJob"));

await host.RunAsync();
