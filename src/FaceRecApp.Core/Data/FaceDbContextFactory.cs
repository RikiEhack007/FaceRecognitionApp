using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FaceRecApp.Core.Data;

/// <summary>
/// Factory used by EF Core tools (dotnet ef migrations, dotnet ef database update).
/// This is ONLY used at design time — not at runtime.
/// 
/// Usage:
///   cd FaceRecognitionApp
///   dotnet ef migrations add InitialCreate -p src/FaceRecApp.Core -s src/FaceRecApp.WPF
///   dotnet ef database update -p src/FaceRecApp.Core -s src/FaceRecApp.WPF
/// 
/// If you get connection errors:
///   1. Make sure SQL Server 2025 Express is running
///   2. Open SQL Server Configuration Manager → ensure TCP/IP is enabled
///   3. Check the instance name (default: SQLEXPRESS)
///   4. Update the connection string below
/// </summary>
public class FaceDbContextFactory : IDesignTimeDbContextFactory<FaceDbContext>
{
    public FaceDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FaceDbContext>();

        // ── Connection String for design-time (migrations) ──
        // Change this if your SQL Server instance has a different name.
        //
        // Common connection strings:
        //   Default instance:  Server=localhost;Database=FaceRecognitionDb;...
        //   Named instance:    Server=localhost\SQLEXPRESS;Database=FaceRecognitionDb;...
        //   Custom port:       Server=localhost,1433;Database=FaceRecognitionDb;...
        //
        // Trusted_Connection=true → uses Windows Authentication (no password needed)
        // TrustServerCertificate=true → skip SSL validation (OK for local dev)
        var connectionString =
            "Server=localhost,60240;" +
            "Database=FaceRecognitionDb;" +
            "Trusted_Connection=true;" +
            "TrustServerCertificate=true;" +
            "MultipleActiveResultSets=true;";

        optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
        {
            // Enable the vector search plugin for EF Core 9
            // This registers the VECTOR(n) type mapping and VectorDistance() function
            sqlOptions.UseVectorSearch();
        });

        return new FaceDbContext(optionsBuilder.Options);
    }
}
