using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// 1. Database (EF Core + SQL Server)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

// 2. Identity API Endpoints (Bearer Token Authentication)
builder.Services.AddAuthorization();
builder.Services.AddIdentityApiEndpoints<IdentityUser>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.Password.RequiredLength = 6;
})
.AddEntityFrameworkStores<AppDbContext>();

// 3. CORS & Clean Swagger Defaults
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// 4. Auto-create database tables on startup (Zero-migration setup for MonsterASP.NET)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// 5. Global JSON Error Handler
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var message = feature?.Error.Message ?? "An unexpected server error occurred.";
        await context.Response.WriteAsJsonAsync(new { message });
    });
});

app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

// 6. Map Auth Endpoints (/register, /login, /refresh)
app.MapIdentityApi<IdentityUser>().WithTags("Auth");

// Helper to get logged-in UserId
static string? GetUserId(ClaimsPrincipal user) =>
    user.FindFirstValue(ClaimTypes.NameIdentifier);

// 7. POST /expenses — Create a new expense
app.MapPost("/expenses", async (CreateExpenseRequest req, ClaimsPrincipal user, AppDbContext db) =>
{
    var userId = GetUserId(user);
    if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

    if (req.Amount <= 0)
        return Results.BadRequest(new { message = "Amount must be greater than zero." });

    var validCategories = new[] { "food", "transport", "shopping", "bills", "entertainment", "other" };
    var normalizedCategory = (req.Category ?? "other").Trim().ToLowerInvariant();
    if (!validCategories.Contains(normalizedCategory))
        normalizedCategory = "other";

    var expense = new Expense
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Amount = req.Amount,
        Currency = string.IsNullOrWhiteSpace(req.Currency) ? "EGP" : req.Currency.ToUpperInvariant(),
        Category = normalizedCategory,
        Note = req.Note,
        Date = req.Date?.ToUniversalTime() ?? DateTime.UtcNow
    };

    db.Expenses.Add(expense);
    await db.SaveChangesAsync();

    return Results.Created($"/expenses/{expense.Id}", expense.ToDto());
})
.RequireAuthorization()
.WithTags("Expenses");

// 8. GET /expenses?from=&to= — List user's expenses
app.MapGet("/expenses", async (DateTime? from, DateTime? to, ClaimsPrincipal user, AppDbContext db) =>
{
    var userId = GetUserId(user);
    if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

    var query = db.Expenses.AsNoTracking().Where(e => e.UserId == userId);

    if (from.HasValue)
        query = query.Where(e => e.Date >= from.Value.ToUniversalTime());
    if (to.HasValue)
        query = query.Where(e => e.Date <= to.Value.ToUniversalTime());

    var expenses = await query
        .OrderByDescending(e => e.Date)
        .Select(e => e.ToDto())
        .ToListAsync();

    return Results.Ok(expenses);
})
.RequireAuthorization()
.WithTags("Expenses");

// 9. DELETE /expenses/{id} — Delete an expense made by mistake
app.MapDelete("/expenses/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
{
    var userId = GetUserId(user);
    if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

    var expense = await db.Expenses.FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);
    if (expense is null)
        return Results.NotFound(new { message = "Expense not found." });

    db.Expenses.Remove(expense);
    await db.SaveChangesAsync();

    return Results.NoContent();
})
.RequireAuthorization()
.WithTags("Expenses");

// 10. GET /summary?period=today|week|month&days=7&topCategories=3 — Flexible Summary Endpoint
app.MapGet("/summary", async (
    string? period,
    int? days,
    int? topCategories,
    ClaimsPrincipal user,
    AppDbContext db) =>
{
    var userId = GetUserId(user);
    if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

    var now = DateTime.UtcNow;
    DateTime startDate;
    string resolvedPeriod;

    if (days.HasValue && days.Value > 0)
    {
        startDate = now.Date.AddDays(-(days.Value - 1));
        resolvedPeriod = $"{days.Value}d";
    }
    else
    {
        resolvedPeriod = (period ?? "month").Trim().ToLowerInvariant();
        startDate = resolvedPeriod switch
        {
            "today" or "day" => now.Date,
            "week" => now.Date.AddDays(-6),
            _ => new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    var takeCount = topCategories ?? 3;

    var userExpenses = await db.Expenses
        .AsNoTracking()
        .Where(e => e.UserId == userId && e.Date >= startDate && e.Date <= now)
        .ToListAsync();

    var total = userExpenses.Sum(e => e.Amount);

    var byCategory = takeCount <= 0
        ? new List<CategorySummaryDto>()
        : userExpenses
            .GroupBy(e => e.Category)
            .Select(g => new CategorySummaryDto(g.Key, g.Sum(x => x.Amount)))
            .OrderByDescending(c => c.Total)
            .Take(takeCount)
            .ToList();

    return Results.Ok(new SummaryResponseDto(
        Period: resolvedPeriod,
        Days: days,
        Total: total,
        Currency: "EGP",
        ByCategory: byCategory
    ));
})
.RequireAuthorization()
.WithTags("Summary");

// 11. POST /seed-demo — Populate 32 realistic EGP expenses
app.MapPost("/seed-demo", async (ClaimsPrincipal user, AppDbContext db) =>
{
    var userId = GetUserId(user);
    if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

    var existing = await db.Expenses.Where(e => e.UserId == userId).ToListAsync();
    if (existing.Count > 0)
    {
        db.Expenses.RemoveRange(existing);
        await db.SaveChangesAsync();
    }

    var now = DateTime.UtcNow;
    Expense Item(decimal amount, string category, string note, double hoursAgo) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Amount = amount,
        Currency = "EGP",
        Category = category,
        Note = note,
        Date = now.AddHours(-hoursAgo)
    };

    var sampleExpenses = new List<Expense>
    {
        Item(210m,  "food",          "Koshary Abou Tarek & fresh cane juice", 1.0),
        Item(115m,  "transport",     "Uber Comfort to workshop venue",        2.5),
        Item(95m,   "food",          "Morning iced Spanish latte & croissant", 4.0),
        Item(350m,  "shopping",      "Apple USB-C braided cable",             5.5),
        Item(60m,   "other",         "Parking & valet tip",                   6.0),

        Item(480m,  "food",          "Grilled chicken dinner with friends",   22),
        Item(140m,  "transport",     "InDrive ride home from New Cairo",      25),
        Item(290m,  "entertainment", "VOX Cinemas IMAX ticket & popcorn",     28),
        Item(85m,   "food",          "Late night shawarma wrap",              30),

        Item(850m,  "bills",         "WE Home Fiber 300GB internet renewal",  50),
        Item(620m,  "food",          "Gourmet Egypt weekly grocery run",      54),
        Item(450m,  "transport",     "95 Octane fuel tank top-up",            72),
        Item(1200m, "shopping",      "Zara cotton overshirt",                 76),
        Item(195m,  "entertainment", "Spotify Duo & iCloud+ 200GB monthly",   96),
        Item(165m,  "food",          "Foul & falafel family breakfast",       102),
        Item(320m,  "bills",         "Vodafone Red mobile bill",              120),
        Item(110m,  "transport",     "Cairo Metro & microbus cards",          125),
        Item(240m,  "other",         "Pharmacy vitamins & painkiller",        140),
        Item(390m,  "food",          "Sushi lunch box delivery (Talabat)",    148),

        Item(1450m, "bills",         "Monthly electricity smart card recharge", 175),
        Item(2100m, "shopping",      "Nike running shoes on sale",              195),
        Item(540m,  "food",          "Seafood dinner in Maadi",                 220),
        Item(310m,  "entertainment", "Padel court booking (2 hours)",           245),
        Item(420m,  "transport",     "Car wash & interior detailing",           265),
        Item(780m,  "food",          "Carrefour household & kitchen supplies",  290),
        Item(340m,  "bills",         "Natural gas & water utility bill",        315),
        Item(650m,  "shopping",      "Hardcover software architecture book",    340),
        Item(150m,  "other",         "Haircut & barber shop",                   365),
        Item(275m,  "entertainment", "PlayStation Plus monthly tournament",     390),
        Item(190m,  "transport",     "Uber to Cairo Festival City",             415),
        Item(490m,  "food",          "Friday family brunch & Kunafa dessert",   440),
        Item(220m,  "other",         "Birthday gift wrap & card for colleague", 470)
    };

    db.Expenses.AddRange(sampleExpenses);
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        message = "Seeded 32 realistic EGP expenses across Today, This Week, and This Month!",
        count = sampleExpenses.Count,
        totalEgp = sampleExpenses.Sum(x => x.Amount)
    });
})
.RequireAuthorization()
.WithTags("Demo Setup");

app.Run();

// ==========================================
// ENTITIES, DTOS & DBCONTEXT
// ==========================================

public class Expense
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EGP";
    public string Category { get; set; } = "other";
    public string? Note { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;

    public ExpenseDto ToDto() => new(Id, Amount, Currency, Category, Note, Date);
}

public record CreateExpenseRequest(
    decimal Amount,
    string? Currency,
    string Category,
    string? Note,
    DateTime? Date);

public record ExpenseDto(
    Guid Id,
    decimal Amount,
    string Currency,
    string Category,
    string? Note,
    DateTime Date);

public record CategorySummaryDto(string Category, decimal Total);

public record SummaryResponseDto(
    string Period,
    int? Days,
    decimal Total,
    string Currency,
    List<CategorySummaryDto> ByCategory);

public class AppDbContext : IdentityDbContext<IdentityUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Expense> Expenses => Set<Expense>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Expense>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Currency).HasMaxLength(10);
            entity.Property(e => e.Category).HasMaxLength(32);
            entity.HasIndex(e => new { e.UserId, e.Date });
        });
    }
}