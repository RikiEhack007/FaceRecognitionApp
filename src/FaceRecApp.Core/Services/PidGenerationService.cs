using FaceRecApp.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Generates unique Patient IDs (PIDs) in the format {SiteCode}{5digits}, e.g. "R00001".
/// </summary>
public class PidGenerationService
{
    private readonly IDbContextFactory<FaceDbContext> _dbFactory;

    /// <summary>
    /// Site code prefix for generated PIDs (e.g., "R").
    /// Set from appsettings.json via App.xaml.cs.
    /// </summary>
    public string SiteCode { get; set; } = "R";

    public PidGenerationService(IDbContextFactory<FaceDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<string> GenerateNextPidAsync()
    {
        return await GenerateNextPidAsync(SiteCode);
    }

    public async Task<string> GenerateNextPidAsync(string siteCode)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var maxIdCard = await db.Patients
            .Where(p => p.Site == siteCode && p.IDCard.StartsWith(siteCode))
            .Select(p => p.IDCard)
            .OrderByDescending(id => id)
            .FirstOrDefaultAsync();

        int nextNumber = 1;
        if (maxIdCard != null && maxIdCard.Length > siteCode.Length)
        {
            var numericPart = maxIdCard.Substring(siteCode.Length);
            if (int.TryParse(numericPart, out int currentMax))
            {
                nextNumber = currentMax + 1;
            }
        }

        return $"{siteCode}{nextNumber:D5}";
    }
}
