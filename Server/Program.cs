using Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Server.Database;
using System.Text;
using Server.Interfaces;
using System.Threading.RateLimiting;
using Server.Entities.PreKeys;
using Microsoft.Data.Sqlite;
using Server.Options;
using Server.Logging;
using Server.Hosting;
using Microsoft.Extensions.Logging.Console;

namespace Server
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // appsettings.json next to the executable is the only configuration file, wherever the
            // server is started from (double click, service, terminal in another folder).
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });

            ServerText.Configure(builder.Configuration["Language"]);
            ServerSettings settings = ServerSettings.Load(builder.Configuration);
            settings.Apply(builder.Configuration);
            if (settings.UsesConfiguredEndpoints)
                builder.WebHost.ConfigureKestrel(settings.ConfigureKestrel);

            builder.Logging.AddConsole(options => options.FormatterName = CompactConsoleFormatter.FormatterName)
                .AddConsoleFormatter<CompactConsoleFormatter, ConsoleFormatterOptions>();
            builder.Logging.AddFilter<ConsoleLoggerProvider>("Microsoft", LogLevel.Warning);
            if (settings.LogToFile)
                builder.Logging.AddProvider(new FileLoggerProvider(settings.LogDirectory, settings.LogRetentionDays));

            // Services in the container:
            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

            builder.Services.AddControllers(options => options.Filters.Add<ActivityLogFilter>());
            builder.Services.AddOptions<TransferOptions>()
                .Bind(builder.Configuration.GetSection(TransferOptions.SectionName))
                .Validate(options => options.MaximumCiphertextLength > 0,
                    "Transfers:MaximumCiphertextLength must be greater than zero.")
                .ValidateOnStart();
            builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
            builder.Services.AddSingleton<ITransferStorageService, TransferStorageService>();
            builder.Services.AddHostedService<TransferCleanupService>();

            builder.Services
                .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
                {
                    options.User.RequireUniqueEmail = false;

                    options.Password.RequiredLength = 12;
                    options.Password.RequireDigit = true;
                    options.Password.RequireLowercase = true;
                    options.Password.RequireUppercase = true;
                    options.Password.RequireNonAlphanumeric = true;

                    options.Lockout.AllowedForNewUsers = true;
                    options.Lockout.MaxFailedAccessAttempts = 5;
                    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
                })
                .AddEntityFrameworkStores<AppDbContext>()
                .AddDefaultTokenProviders();

            // --------------- Rate limiting configuration ------------------
            builder.Services.AddRateLimiter(options =>
            {
                options.AddPolicy("LoginRateLimit", context =>
                {
                    string partitionKey = context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown";

                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = settings.LoginAttemptsPerMinute, // login attempts per minute per IP address
                            Window = TimeSpan.FromMinutes(1),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0
                        });
                });

                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy("UsernameLookupLimit", context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.UsernameChecksPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });

            // --------------- Jwt configuration ------------------
            var jwtIssuer = builder.Configuration["Jwt:Issuer"];
            var jwtAudience = builder.Configuration["Jwt:Audience"];
            var jwtSecretKey = builder.Configuration["Jwt:SecretKey"];

            if (string.IsNullOrWhiteSpace(jwtIssuer))
                throw new InvalidOperationException("Jwt:Issuer is not configured.");

            if (string.IsNullOrWhiteSpace(jwtAudience))
                throw new InvalidOperationException("Jwt:Audience is not configured.");

            if (string.IsNullOrWhiteSpace(jwtSecretKey))
                throw new InvalidOperationException("Jwt:SecretKey is not configured.");

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = jwtIssuer,

                        ValidateAudience = true,
                        ValidAudience = jwtAudience,

                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(jwtSecretKey)),

                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromMinutes(1)
                    };
                });
            // ---------------- Jwt configuration end ----------------

            builder.Services.AddAuthorization();

            builder.Services.AddOpenApi();

            var app = builder.Build();

            using (IServiceScope scope = app.Services.CreateScope())
            {
                AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                EnsureSqliteDirectoryExists(dbContext.Database.GetConnectionString());
                dbContext.Database.Migrate();
            }

            // Configure the HTTP request pipeline.
            // First: one log line for every request, also those refused or not found further on.
            app.UseMiddleware<RequestLogMiddleware>(settings.ShowInboxPolling);

            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            // Only when this server has its own HTTPS endpoint (on Azure TLS is handled in front of it).
            if (settings.UsesConfiguredEndpoints && settings.HttpsPort > 0)
                app.UseHttpsRedirection();
            app.UseRateLimiter();

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();

            string configurationFile = Path.Combine(app.Environment.ContentRootPath, "appsettings.json");
            app.Lifetime.ApplicationStarted.Register(() =>
                ServerConsole.ShowBanner(app.Urls, settings, configurationFile));

            app.Run();
        }

        private static void EnsureSqliteDirectoryExists(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return;

            var builder = new SqliteConnectionStringBuilder(connectionString);
            string dataSource = builder.DataSource;

            if (string.IsNullOrWhiteSpace(dataSource))
                return;

            string? directory = Path.GetDirectoryName(dataSource);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }
}
