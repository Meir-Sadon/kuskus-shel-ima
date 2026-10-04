using Kuskus.Api.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kuskus.Api.Data;

public static class DatabaseInitializer
{
    /// <summary>Applies pending migrations and seeds the admin password hash from configuration.</summary>
    public static async Task InitializeAsync(IServiceProvider services, bool migrate)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = scope.ServiceProvider.GetRequiredService<IOptions<AdminOptions>>().Value;
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer));

        if (migrate)
            await db.Database.MigrateAsync();

        await SeedAdminPasswordAsync(db, admin.PasswordHash, logger);

        if (migrate)
            await SeedDrinksAsync(db, logger);
    }

    public const string DrinksCategoryName = "שתיה";
    public const decimal DrinkPrice = 12m;

    // Name and picture file (frontend/public/drinks/<slug>.svg, served from the site root).
    private static readonly (string Name, string Slug)[] Drinks =
    [
        ("קוקה קולה", "cola"),
        ("קוקה קולה זירו", "cola-zero"),
        ("ספרייט", "sprite"),
        ("פאנטה", "fanta"),
        ("שוופס", "schweppes"),
        ("פיוז טי אפרסק", "fuze-peach"),
        ("פיוז טי לימון", "fuze-lemon"),
        ("מים מינרליים", "water"),
        ("סודה", "soda"),
        ("מיץ תפוזים", "orange-juice"),
        ("מיץ ענבים", "grape-juice"),
    ];

    /// <summary>
    /// Adds the popular drinks to the drinks category, creating the category if it is missing.
    /// A drink that already exists in the category (removed ones included) is left alone,
    /// so this is safe to run on every start and never undoes an admin's changes.
    /// </summary>
    public static async Task SeedDrinksAsync(AppDbContext db, ILogger logger)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Name == DrinksCategoryName);
        if (category is null)
        {
            var nextOrder = await db.Categories.AnyAsync() ? await db.Categories.MaxAsync(c => c.DisplayOrder) + 1 : 0;
            category = new Entities.Category { Name = DrinksCategoryName, DisplayOrder = nextOrder };
            db.Categories.Add(category);
        }

        var existing = category.Id == 0
            ? []
            : await db.Dishes.Where(d => d.CategoryId == category.Id).Select(d => d.Name).ToListAsync();

        var added = 0;
        foreach (var (name, slug) in Drinks.Where(d => !existing.Contains(d.Name)))
        {
            category.Dishes.Add(new Entities.Dish
            {
                Name = name,
                SellBy = Entities.SellBy.Units,
                ChoiceMode = Entities.ChoiceMode.Free,
                MinAmount = 1,
                MaxAmount = 10,
                AmountStep = 1,
                UnitPrice = DrinkPrice,
                Images = [new Entities.DishImage { Url = $"/drinks/{slug}.svg", PublicId = $"static/drinks/{slug}", DisplayOrder = 0 }],
            });
            added++;
        }

        if (added == 0)
            return;

        await db.SaveChangesAsync();
        logger.LogInformation("Seeded {Count} drinks into the '{Category}' category.", added, DrinksCategoryName);
    }

    public static async Task SeedAdminPasswordAsync(AppDbContext db, string? configuredHash, ILogger logger)
    {
        var settings = await db.Settings.SingleAsync(s => s.Id == Entities.Settings.SingletonId);
        if (!string.IsNullOrEmpty(settings.AdminPasswordHash))
            return;

        if (string.IsNullOrEmpty(configuredHash))
        {
            logger.LogWarning("No admin password is set. Set Admin__PasswordHash to enable admin login.");
            return;
        }

        settings.AdminPasswordHash = configuredHash;
        await db.SaveChangesAsync();
        logger.LogInformation("Admin password hash seeded from configuration.");
    }
}
