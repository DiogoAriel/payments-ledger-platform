using Ledger.Api;
using Ledger.Application;
using Ledger.Application.Webhooks;
using Ledger.Infrastructure;
using Ledger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logs. One line per event, machine readable, correlated by trace id.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

// Application and infrastructure wiring.
builder.Services.AddLedgerApplication();
builder.Services.AddLedgerInfrastructure(builder.Configuration);
builder.Services.AddSingleton(builder.Configuration.GetSection(WebhookOptions.SectionName).Get<WebhookOptions>() ?? new WebhookOptions());

// Authentication: OIDC bearer tokens when Auth:Authority is set; otherwise open (local development only).
var authority = builder.Configuration["Auth:Authority"];
var authEnabled = !string.IsNullOrWhiteSpace(authority);
if (authEnabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.Audience = builder.Configuration["Auth:Audience"];
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidateAudience = true;
        });
    builder.Services.AddAuthorization();
}

// Observability: traces and metrics over OTLP (collector, Jaeger, Grafana, any vendor).
var serviceName = builder.Configuration["Otel:ServiceName"] ?? "payments-ledger-api";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName, serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());

builder.Services.AddHealthChecks()
    .AddDbContextCheck<LedgerDbContext>("database", tags: ["ready"]);

// Enums travel as names ("Customer", "PayIn"), which is what API consumers expect to read and write.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<LedgerExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Schema management: migrations in production, EnsureCreated for the SQLite local/test profile.
if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
    if (db.Database.IsSqlite())
    {
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        await db.Database.MigrateAsync();
    }
}

app.UseSerilogRequestLogging();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });

var api = app.MapGroup("/api/v1");
if (authEnabled)
{
    api.RequireAuthorization();
}

api.MapAccountEndpoints();
api.MapTransferEndpoints();

// Webhooks authenticate with HMAC, never with user tokens, so they live outside the authorized group.
app.MapWebhookEndpoints();

await app.RunAsync();

/// <summary>Exposed so integration tests can host the application with WebApplicationFactory.</summary>
public partial class Program;
