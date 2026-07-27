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

        services.AddHttpClient<WowQuizScraperService>(client =>
        {
            client.BaseAddress = new Uri("https://api.etowow.ru");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Infocal.Scraper/1.0");
            client.DefaultRequestHeaders.Referrer = new Uri("https://gomel.wowquiz.ru/schedule");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // ── Quartz ──
        var aggressiveCron = cfg["ScrapeSchedule:AggressiveCron"] ?? "0 */30 * ? * SAT,SUN,MON";
        var lazyCron       = cfg["ScrapeSchedule:LazyCron"]       ?? "0 0 10 ? * TUE,WED,THU,FRI";

        services.AddQuartz(q =>
        {
            // ── Gomel Mass Skating ──
            var skatingKey = new JobKey("GomelMassSkatingJob");
            q.AddJob<GomelMassSkatingJob>(opts => opts.WithIdentity(skatingKey));

            q.AddTrigger(opts => opts
                .ForJob(skatingKey)
                .WithIdentity("GomelAggressiveTrigger")
                .WithCronSchedule(aggressiveCron)
                .WithDescription("Сб–Пн: каждые 30 мин — ждём новое расписание"));

            q.AddTrigger(opts => opts
                .ForJob(skatingKey)
                .WithIdentity("GomelLazyTrigger")
                .WithCronSchedule(lazyCron)
                .WithDescription("Вт–Пт: раз в день — проверка на случай изменений"));

            // ── WowQuiz ──
            var quizKey = new JobKey("WowQuizJob");
            q.AddJob<WowQuizJob>(opts => opts.WithIdentity(quizKey));

            // Once daily at 9:00 — quiz schedule doesn't change often
            q.AddTrigger(opts => opts
                .ForJob(quizKey)
                .WithIdentity("WowQuizDailyTrigger")
                .WithCronSchedule("0 0 9 * * ?")
                .WithDescription("Ежедневно в 9:00 — проверка расписания квизов"));
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
await scheduler.TriggerJob(new JobKey("WowQuizJob"));

await host.RunAsync();
