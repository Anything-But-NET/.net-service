var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapGet("/api/documents/{id}", (string id, HttpContext context) =>
{
    var userId = context.Request.Headers["X-User-Id"].ToString();

    var document = new
    {
        Id = "Doc",
        Content = "Top secret",
        AllowedUsers = new[] { "UserA" },
    };

    if(id != document.Id)
    {
        return Results.NotFound("Document not found!");
    }

    if(!document.AllowedUsers.Contains(userId))
    {
        return Results.Problem("Access denied", statusCode: 403);
    }

    return Results.Ok(new { document.Id, document.Content });
});

app.Run();
