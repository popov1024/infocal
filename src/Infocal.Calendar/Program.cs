using Infocal.Calendar;
using Infocal.Calendar.Data;
using Infocal.Calendar.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── SQLite ──
var conn = builder.Configuration.GetConnectionString("Default")
           ?? "Data Source=calendar.db";
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseSqlite(conn));

// ── Business services ──
builder.Services.AddScoped<EventStore>();
builder.Services.AddSingleton<CalendarService>();

var app = builder.Build();

// ── API key (only required for write endpoints) ──
var apiKey = builder.Configuration["ApiKey"] ?? "dev-key-change-me";

// ── Auto-create DB + seed ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DbSeed.InitializeAsync(db);
}

Endpoints.Map(app, apiKey);

app.Run();
