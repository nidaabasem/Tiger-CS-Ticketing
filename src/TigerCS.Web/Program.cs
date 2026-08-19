var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

// Phase 1 foundation placeholder (S-01). UI pages and static assets are
// added when the pilot screens (MVP-UI-Wireframes.md) are implemented.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
