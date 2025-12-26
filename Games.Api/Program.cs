using Dapper;
using Npgsql;
using Prometheus;


var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------
// CORS (colocar antes de "Build()")
// -----------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// -----------------------------------------------------------
// 1. Criar banco se não existir
// -----------------------------------------------------------
async Task EnsureDatabaseExists()
{
    var builderWithoutDb = new NpgsqlConnectionStringBuilder(connectionString)
    {
        Database = "postgres"
    };

    using var con = new NpgsqlConnection(builderWithoutDb.ConnectionString);
    await con.OpenAsync();

    var dbName = "gamesdb";

    var exists = await con.ExecuteScalarAsync<int>(
        "SELECT 1 FROM pg_database WHERE datname = @db", new { db = dbName });

    if (exists == 0)
    {
        await con.ExecuteAsync($"CREATE DATABASE {dbName}");
        Console.WriteLine($"Banco criado: {dbName}");
    }
}

await EnsureDatabaseExists();

// -----------------------------------------------------------
// 2. Criar tabela games se não existir
// -----------------------------------------------------------
async Task EnsureTablesExist()
{
    using var con = new NpgsqlConnection(connectionString);
    await con.OpenAsync();

    var createTable = """
        CREATE TABLE IF NOT EXISTS games(
            id UUID PRIMARY KEY,
            title TEXT NOT NULL,
            genre TEXT NOT NULL,
            tags TEXT NOT NULL,
            price REAL NOT NULL
        );
    """;

    await con.ExecuteAsync(createTable);
}

await EnsureTablesExist();

// -----------------------------------------------------------
// 3. Construção do app
// -----------------------------------------------------------
var app = builder.Build();

// **CORS precisa vir imediatamente após Build()**
app.UseCors("AllowAll");

app.UseSwagger();
app.UseSwaggerUI();

// -----------------------------------------------------------
// 4. Endpoints
// -----------------------------------------------------------

// Seed
app.MapPost("/games/seed", async () =>
{
    using var con = new NpgsqlConnection(connectionString);
    await con.OpenAsync();

    await con.ExecuteAsync("DELETE FROM games");

    var seed = new[] {
        new { id = Guid.NewGuid(), title = "Elden Ring", genre = "RPG", tags = "fantasy,soulslike", price = 199.9 },
        new { id = Guid.NewGuid(), title = "Forza Horizon", genre = "Racing", tags = "cars", price = 149.9 },
        new { id = Guid.NewGuid(), title = "FC 24", genre = "Sports", tags = "soccer,futebol", price = 249.9 },
    };

    foreach (var g in seed)
        await con.ExecuteAsync("INSERT INTO games VALUES (@id,@title,@genre,@tags,@price)", g);

    return Results.Ok(new { created = seed.Length });
});

// Listar
app.MapGet("/games", async (string? query, string? genre) =>
{
    using var con = new NpgsqlConnection(connectionString);

    var sql = "SELECT id,title,genre,tags,price FROM games WHERE 1=1";
    var p = new DynamicParameters();

    if (!string.IsNullOrWhiteSpace(query))
    {
        sql += " AND (LOWER(title) LIKE LOWER(@q) OR LOWER(tags) LIKE LOWER(@q))";
        p.Add("q", $"%{query}%");
    }

    if (!string.IsNullOrWhiteSpace(genre))
    {
        sql += " AND LOWER(genre) = LOWER(@g)";
        p.Add("g", genre);
    }

    var rows = await con.QueryAsync(sql, p);
    return Results.Ok(rows);
});

app.MapGet("/games/{id}", async (Guid id) =>
{
    using var con = new NpgsqlConnection(connectionString);

    var game = await con.QueryFirstOrDefaultAsync(
        "SELECT * FROM games WHERE id = @id", new { id });

    return game is null ? Results.NotFound() : Results.Ok(game);
});

app.MapPost("/games", async (GameDto dto) =>
{
    using var con = new NpgsqlConnection(connectionString);

    var id = Guid.NewGuid();

    await con.ExecuteAsync(
        "INSERT INTO games(id,title,genre,tags,price) VALUES (@id,@title,@genre,@tags,@price)",
        new { id, dto.Title, dto.Genre, dto.Tags, dto.Price }
    );

    return Results.Created($"/games/{id}", new { id });
});

app.MapPut("/games/{id}", async (Guid id, GameDto dto) =>
{
    using var con = new NpgsqlConnection(connectionString);

    var rows = await con.ExecuteAsync(
        "UPDATE games SET title=@title, genre=@genre, tags=@tags, price=@price WHERE id=@id",
        new { id, dto.Title, dto.Genre, dto.Tags, dto.Price }
    );

    return rows == 0 ? Results.NotFound() : Results.Ok(new { updated = true });
});

app.MapDelete("/games/{id}", async (Guid id) =>
{
    using var con = new NpgsqlConnection(connectionString);

    var rows = await con.ExecuteAsync(
        "DELETE FROM games WHERE id = @id", new { id });

    return rows == 0 ? Results.NotFound() : Results.Ok(new { deleted = true });
});

// endpoint de métricas do Prometheus
app.MapMetrics();




app.Run();

record GameDto(string Title, string Genre, string Tags, double Price);
