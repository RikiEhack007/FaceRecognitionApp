using System.Windows;
using FaceRecApp.Core.Data;
using FaceRecApp.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FaceRecApp.WPF;

/// <summary>
/// Application entry point.
/// Configures Dependency Injection, database connection, and all services.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Global service provider — access from anywhere via App.Services.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Load configuration ──
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // ── Configure DI container ──
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);

        // ── Database ──
        var connectionString = configuration.GetConnectionString("FaceRecognitionDb");
        services.AddDbContextFactory<FaceDbContext>(options =>
        {
            options.UseSqlServer(connectionString!, sqlOptions =>
            {
                sqlOptions.UseVectorSearch();
                sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorNumbersToAdd: null);
            });
        });

        // ── Core Services ──
        // Singleton: created once, lives for entire app lifetime
        // These hold ONNX models in memory (~50 MB total)
        services.AddSingleton<FaceDetectionService>();
        services.AddSingleton<FaceRecognitionService>();
        services.AddSingleton<LivenessService>();
        services.AddSingleton<CameraService>();

        // Transient: FaceRepository creates short-lived DbContext per operation
        services.AddTransient<FaceRepository>();

        // Transient: BenchmarkService for performance testing
        services.AddTransient<BenchmarkService>();

        // Singleton: RecognitionPipeline orchestrates all services
        services.AddSingleton<RecognitionPipeline>(sp =>
            new RecognitionPipeline(
                sp.GetRequiredService<FaceDetectionService>(),
                sp.GetRequiredService<FaceRecognitionService>(),
                sp.GetRequiredService<LivenessService>(),
                sp.GetRequiredService<FaceRepository>()));

        Services = services.BuildServiceProvider();

        // ── Ensure database exists ──
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        try
        {
            using var scope = Services.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FaceDbContext>>();
            using var db = dbFactory.CreateDbContext();
            db.Database.Migrate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to connect to SQL Server 2025.\n\n" +
                $"Please ensure:\n" +
                $"1. SQL Server 2025 Express is installed and running\n" +
                $"2. The instance name in appsettings.json is correct\n" +
                $"3. Windows Authentication is enabled\n\n" +
                $"Error: {ex.Message}",
                "Database Connection Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Current.Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose services (releases ONNX models, webcam, DB connections)
        if (Services is IDisposable disposable)
            disposable.Dispose();

        base.OnExit(e);
    }
}
