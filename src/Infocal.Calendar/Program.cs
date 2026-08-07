var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=calendar.db";

builder.Services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(connectionString));
builder.Services.AddScoped<EventStore>();
builder.Services.AddSingleton<CalendarService>();

var app = builder.Build();

var apiKey = builder.Configuration["INFOCAL_API_KEY"] ?? "dev-key-change-me";

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DbSeed.InitializeAsync(db);
}

Endpoints.Map(app, apiKey);

app.Run();
