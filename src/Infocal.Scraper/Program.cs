// ── Гейт выборочного запуска джобов: ScrapeSchedule:OnlyJobs (пусто = все джобы) ──
// Значение читается в ConfigureServices, используется и при регистрации джобов, и при стартовом триггере
string[] onlyJobs = [];

bool JobEnabled(string key) =>
    onlyJobs.Length == 0 || onlyJobs.Contains(key, StringComparer.OrdinalIgnoreCase);

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        var cfg = ctx.Configuration;

        onlyJobs = (cfg["ScrapeSchedule:OnlyJobs"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // ── HTTP clients ──
        services.AddHttpClient("CalendarApi", client =>
        {
            client.BaseAddress = new Uri(cfg["CalendarApi:BaseUrl"] ?? "http://localhost:5223");
            client.DefaultRequestHeaders.Add("X-Api-Key", cfg["INFOCAL_API_KEY"] ?? "dev-key-change-me");
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

        // ── VK + Vision services ──
        services.AddHttpClient<VkScraperService>(client =>
        {
            client.BaseAddress = new Uri("https://api.vk.com");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<VisionService>(client =>
        {
            client.BaseAddress = new Uri("https://api.deepseek.com");
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        // Разбор текстовых постов VK: своего HttpClient нет, ходит через VkScraperService
        services.AddTransient<UmaparkQuizScraperService>();

        services.AddHttpClient<MozgoBoynyaScraperService>(client =>
        {
            // BaseAddress is overridden per-request (uses the city URL from config)
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<SmuziQuizScraperService>(client =>
        {
            // Адреса страницы и API берутся из конфига SmuziQuiz
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Infocal.Scraper/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // ── Quartz ──
        var aggressiveCron = cfg["ScrapeSchedule:AggressiveCron"] ?? "0 */30 * ? * SAT,SUN,MON";
        var lazyCron = cfg["ScrapeSchedule:LazyCron"] ?? "0 0 10 ? * TUE,WED,THU,FRI";

        services.AddQuartz(q =>
        {
            // ── Gomel Mass Skating ──
            if (JobEnabled("GomelMassSkatingJob"))
            {
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
            }

            // ── WowQuiz ──
            if (JobEnabled("WowQuizJob"))
            {
                var quizKey = new JobKey("WowQuizJob");
                q.AddJob<WowQuizJob>(opts => opts.WithIdentity(quizKey));

                q.AddTrigger(opts => opts
                    .ForJob(quizKey)
                    .WithIdentity("WowQuizDailyTrigger")
                    .WithCronSchedule("0 0 9 * * ?")
                    .WithDescription("Ежедневно в 9:00 — проверка расписания квизов"));
            }

            // ── Смузи ──
            if (JobEnabled("SmuziQuizJob"))
            {
                var smuziKey = new JobKey("SmuziQuizJob");
                q.AddJob<SmuziQuizJob>(opts => opts.WithIdentity(smuziKey));

                q.AddTrigger(opts => opts
                    .ForJob(smuziKey)
                    .WithIdentity("SmuziQuizDailyTrigger")
                    .WithCronSchedule("0 0 9 * * ?")
                    .WithDescription("Ежедневно в 9:00 — проверка расписания квизов Смузи"));
            }

            // ── УмаПарк ──
            if (JobEnabled("UmaparkQuizJob"))
            {
                var umaparkKey = new JobKey("UmaparkQuizJob");
                q.AddJob<UmaparkQuizJob>(opts => opts.WithIdentity(umaparkKey));

                q.AddTrigger(opts => opts
                    .ForJob(umaparkKey)
                    .WithIdentity("UmaparkQuizDailyTrigger")
                    .WithCronSchedule("0 0 9 * * ?")
                    .WithDescription("Ежедневно в 9:00 — проверка расписания квизов УмаПарк"));
            }

            // ── MozgoBoynya ──
            if (JobEnabled("MozgoBoynyaJob"))
            {
                var mzgbKey = new JobKey("MozgoBoynyaJob");
                q.AddJob<MozgoBoynyaJob>(opts => opts.WithIdentity(mzgbKey));

                q.AddTrigger(opts => opts
                    .ForJob(mzgbKey)
                    .WithIdentity("MozgoBoynyaDailyTrigger")
                    .WithCronSchedule("0 0 9 * * ?")
                    .WithDescription("Ежедневно в 9:00 — проверка расписания Мозгобойни"));
            }

            // ── VK image-based schedules parsed via DeepSeek Vision ──
            if (JobEnabled("VkImageScheduleJob"))
            {
                var vkScheduleKey = new JobKey("VkImageScheduleJob");
                q.AddJob<VkImageScheduleJob>(opts => opts.WithIdentity(vkScheduleKey));

                q.AddTrigger(opts => opts
                    .ForJob(vkScheduleKey)
                    .WithIdentity("VkImageScheduleTrigger")
                    .WithCronSchedule("0 0 9 * * ?")
                    .WithDescription("Ежедневно в 9:00 — сканирование VK-расписаний через DeepSeek"));
            }
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
if (JobEnabled("GomelMassSkatingJob"))
    await scheduler.TriggerJob(new JobKey("GomelMassSkatingJob"));
if (JobEnabled("WowQuizJob"))
    await scheduler.TriggerJob(new JobKey("WowQuizJob"));
if (JobEnabled("SmuziQuizJob"))
    await scheduler.TriggerJob(new JobKey("SmuziQuizJob"));
if (JobEnabled("MozgoBoynyaJob"))
    await scheduler.TriggerJob(new JobKey("MozgoBoynyaJob"));
if (JobEnabled("UmaparkQuizJob"))
    await scheduler.TriggerJob(new JobKey("UmaparkQuizJob"));
if (JobEnabled("VkImageScheduleJob"))
    await scheduler.TriggerJob(new JobKey("VkImageScheduleJob"));

await host.RunAsync();
