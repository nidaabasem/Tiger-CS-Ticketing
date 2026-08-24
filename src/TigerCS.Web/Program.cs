var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// Phase 1 pilot screens (MVP-UI-Wireframes.md): Login, Tickets, Ticket
// Details. Server-rendered with mock data pending API integration.
app.MapRazorPages();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
