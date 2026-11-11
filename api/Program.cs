using System.Globalization;
using System.Text.RegularExpressions;
using AgriTrack.Api;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
var listenPort = Environment.GetEnvironmentVariable("PORT");
builder.WebHost.UseUrls($"http://{(listenPort is null ? "localhost" : "0.0.0.0")}:{listenPort ?? "3000"}");

var cfg = builder.Configuration.GetSection("Database");
var db = new Db(
    cfg["Host"] ?? "localhost",
    uint.TryParse(cfg["Port"], out var port) ? port : 3306,
    cfg["User"] ?? "root",
    cfg["Password"] ?? "",
    cfg["Name"] ?? "agritrack");
var sessions = new SessionStore();

await db.InitAsync();

var app = builder.Build();
const string CookieName = "agritrack_sid";
var indexPath = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "index.html"));

string[] invCats = { "Seeds", "Seedlings", "Material", "Equipment", "Produce" };
string[] expCats = { "Input", "Operations", "Maintenance", "Others" };
string[] payMethods = { "Cash", "GCash", "Bank Transfer", "Check", "Credit" };
string[] listCats = { "Vegetables", "Fruits", "Grains", "Others" };

long? CurrentUid(HttpContext ctx) => sessions.Get(ctx.Request.Cookies[CookieName]);
IResult NotSignedIn() => Results.Json(new { error = "Not signed in." }, statusCode: 401);
IResult DbDown() => Results.Json(new { error = "Database not connected. Put your MySQL password in api/appsettings.json and restart the server." }, statusCode: 503);

app.MapGet("/", () => Results.File(indexPath, "text/html; charset=utf-8"));
app.MapGet("/index.html", () => Results.File(indexPath, "text/html; charset=utf-8"));
app.MapGet("/api/health", () =>
{
    var host = cfg["Host"] ?? "";
    var user = cfg["User"] ?? "";
    return Results.Ok(new
    {
        ok = true,
        database = db.Ready,
        error = db.Error,
        detail = db.Detail,
        target = new
        {
            hostSet = host.Length > 0,
            hostSuffix = host.Length > 3 ? host[^Math.Min(host.Length, 28)..] : host,
            port = cfg["Port"],
            userSet = user.Length > 0,
            userSuffix = user.Length > 6 ? user[^6..] : user,
            name = cfg["Name"],
        },
    });
});

/* ---------- Auth ---------- */

app.MapPost("/api/login", async (HttpContext ctx, LoginBody body) =>
{
    if (!db.Ready) return DbDown();
    var username = (body.Username ?? "").Trim().ToLowerInvariant();
    if (username.Length == 0 || string.IsNullOrEmpty(body.Password))
        return Results.Json(new { error = "Incorrect username or password." }, statusCode: 400);

    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT user_id, password_hash FROM users WHERE username = @u LIMIT 1";
    cmd.Parameters.AddWithValue("@u", username);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync())
        return Results.Json(new { error = "Incorrect username or password." }, statusCode: 400);
    var id = r.GetInt64(0);
    var hash = r.GetString(1);
    await r.DisposeAsync();

    if (!Passwords.Verify(body.Password!, hash))
        return Results.Json(new { error = "Incorrect username or password." }, statusCode: 400);

    var token = sessions.Create(id);
    ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = TimeSpan.FromDays(7),
    });
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/signup", async (HttpContext ctx, SignupBody body) =>
{
    if (!db.Ready) return DbDown();
    var name = (body.Name ?? "").Trim();
    var username = (body.Username ?? "").Trim().ToLowerInvariant();
    var password = body.Password ?? "";
    var role = body.Role == "buyer" ? "buyer" : "farmer";

    if (name.Length == 0) return Results.Json(new { error = "Enter your full name." }, statusCode: 400);
    if (!Regex.IsMatch(username, "^[a-z0-9._@-]{3,50}$"))
        return Results.Json(new { error = "Username must be 3-50 characters: letters, numbers, . _ @ -" }, statusCode: 400);
    if (password.Length < 6)
        return Results.Json(new { error = "Password must be at least 6 characters." }, statusCode: 400);

    await using var conn = await db.OpenAsync();
    await using (var exists = conn.CreateCommand())
    {
        exists.CommandText = "SELECT COUNT(*) FROM users WHERE username = @u";
        exists.Parameters.AddWithValue("@u", username);
        if (Convert.ToInt64(await exists.ExecuteScalarAsync()) > 0)
            return Results.Json(new { error = "That username is already taken. Try another one." }, statusCode: 409);
    }

    await using (var insert = conn.CreateCommand())
    {
        insert.CommandText = """
            INSERT INTO users (username, password_hash, full_name, role, farm_name, location, phone)
            VALUES (@u, @p, @n, @r, @farm, 'Bongabong, Oriental Mindoro', '')
            """;
        insert.Parameters.AddWithValue("@u", username);
        insert.Parameters.AddWithValue("@p", Passwords.Hash(password));
        insert.Parameters.AddWithValue("@n", name);
        insert.Parameters.AddWithValue("@r", role);
        insert.Parameters.AddWithValue("@farm", role == "farmer" ? (object)$"{name} Farm" : DBNull.Value);
        await insert.ExecuteNonQueryAsync();
    }

    var newId = await Db.LastIdAsync(conn);
    var token = sessions.Create(newId);
    ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = TimeSpan.FromDays(7),
    });
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/logout", (HttpContext ctx) =>
{
    sessions.Remove(ctx.Request.Cookies[CookieName]);
    ctx.Response.Cookies.Delete(CookieName);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/session", async (HttpContext ctx) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    await using var conn = await db.OpenAsync();
    var user = await LoadUserAsync(conn, uid.Value);
    return user is null ? NotSignedIn() : Results.Ok(new { user });
});

/* ---------- Bootstrap ---------- */

app.MapGet("/api/bootstrap", async (HttpContext ctx) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();

    await using var conn = await db.OpenAsync();
    var user = await LoadUserAsync(conn, uid.Value);
    if (user is null) return NotSignedIn();

    var data = new DataDto
    {
        Inventory = await LoadInventoryAsync(conn, uid.Value),
        Expenses = await LoadExpensesAsync(conn, uid.Value),
        Sales = await LoadSalesTotalAsync(conn, uid.Value),
        SalesLog = await LoadSalesLogAsync(conn, uid.Value),
        Activities = await LoadActivitiesAsync(conn, uid.Value),
        Seen = user.Seen,
    };

    return Results.Ok(new BootstrapDto
    {
        User = user,
        Data = data,
        Market = await LoadMarketAsync(conn),
    });
});

/* ---------- Inventory ---------- */

app.MapPost("/api/inventory", async (HttpContext ctx, ItemBody b) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    var invalid = ValidateItem(b);
    if (invalid != null) return Results.Json(new { error = invalid }, statusCode: 400);

    await using var conn = await db.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO inventory_items (user_id, name, category, quantity, unit, added_date, supplier, unit_cost, log_expense, notes)
            VALUES (@u, @n, @cat, @qty, @unit, @date, @supplier, @cost, @logexp, @notes)
            """;
        BindItem(cmd, uid.Value, b);
        await cmd.ExecuteNonQueryAsync();
    }

    var itemId = await Db.LastIdAsync(conn, tx);
    await SyncItemExpenseAsync(conn, tx, uid.Value, itemId, b);
    if (!b.Undo)
        await LogActivityAsync(conn, tx, uid.Value, "green", $"You added {Num(b.Qty)} {b.Unit} of {b.Name!.Trim()} to inventory");

    await tx.CommitAsync();
    return Results.Ok(new { id = itemId.ToString() });
});

app.MapPut("/api/inventory/{id}", async (string id, HttpContext ctx, ItemBody b) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    if (!long.TryParse(id, out var itemId)) return Results.NotFound();
    var invalid = ValidateItem(b);
    if (invalid != null) return Results.Json(new { error = invalid }, statusCode: 400);

    await using var conn = await db.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE inventory_items
            SET name = @n, category = @cat, quantity = @qty, unit = @unit, added_date = @date,
                supplier = @supplier, unit_cost = @cost, log_expense = @logexp, notes = @notes
            WHERE item_id = @item AND user_id = @u
            """;
        BindItem(cmd, uid.Value, b);
        cmd.Parameters.AddWithValue("@item", itemId);
        if (await cmd.ExecuteNonQueryAsync() == 0)
        {
            await tx.RollbackAsync();
            return Results.NotFound();
        }
    }

    await SyncItemExpenseAsync(conn, tx, uid.Value, itemId, b);
    await tx.CommitAsync();
    return Results.Ok(new { ok = true });
});

app.MapPut("/api/inventory/{id}/qty", async (string id, HttpContext ctx, QtyBody b) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    if (!long.TryParse(id, out var itemId)) return Results.NotFound();

    var qty = Math.Max(0, b.Qty);
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE inventory_items SET quantity = @q WHERE item_id = @item AND user_id = @u";
    cmd.Parameters.AddWithValue("@q", qty);
    cmd.Parameters.AddWithValue("@item", itemId);
    cmd.Parameters.AddWithValue("@u", uid.Value);
    if (await cmd.ExecuteNonQueryAsync() == 0) return Results.NotFound();
    return Results.Ok(new { qty });
});

app.MapDelete("/api/inventory/{id}", async (string id, HttpContext ctx) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    if (!long.TryParse(id, out var itemId)) return Results.NotFound();

    await using var conn = await db.OpenAsync();
    ItemDto? snapshot = null;
    await using (var sel = conn.CreateCommand())
    {
        sel.CommandText = """
            SELECT name, category, quantity, unit, added_date, supplier, unit_cost, log_expense, notes
            FROM inventory_items WHERE item_id = @item AND user_id = @u
            """;
        sel.Parameters.AddWithValue("@item", itemId);
        sel.Parameters.AddWithValue("@u", uid.Value);
        await using var r = await sel.ExecuteReaderAsync();
        if (await r.ReadAsync())
        {
            snapshot = new ItemDto
            {
                Name = r.GetString(r.GetOrdinal("name")),
                Category = r.GetString(r.GetOrdinal("category")),
                Qty = r.GetDecimal(r.GetOrdinal("quantity")),
                Unit = r.GetString(r.GetOrdinal("unit")),
                Date = r.GetDateTime(r.GetOrdinal("added_date")).ToString("yyyy-MM-dd"),
                Supplier = r.IsDBNull(r.GetOrdinal("supplier")) ? null : r.GetString(r.GetOrdinal("supplier")),
                Cost = r.GetDecimal(r.GetOrdinal("unit_cost")),
                LogExp = r.GetBoolean(r.GetOrdinal("log_expense")),
                Notes = r.IsDBNull(r.GetOrdinal("notes")) ? null : r.GetString(r.GetOrdinal("notes")),
            };
        }
        await r.DisposeAsync();
    }
    if (snapshot is null) return Results.NotFound();

    await using (var del = conn.CreateCommand())
    {
        del.CommandText = "DELETE FROM inventory_items WHERE item_id = @item AND user_id = @u";
        del.Parameters.AddWithValue("@item", itemId);
        del.Parameters.AddWithValue("@u", uid.Value);
        await del.ExecuteNonQueryAsync();
    }

    return Results.Ok(new { item = snapshot });
});

/* ---------- Expenses ---------- */

app.MapPost("/api/expenses", async (HttpContext ctx, ExpenseBody b) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    var invalid = ValidateExpense(b);
    if (invalid != null) return Results.Json(new { error = invalid }, statusCode: 400);

    long? itemId = long.TryParse(b.ItemId, out var parsed) ? parsed : null;
    var pay = string.IsNullOrWhiteSpace(b.Pay) ? "Cash" : b.Pay;

    await using var conn = await db.OpenAsync();
    if (itemId != null)
    {
        await using var own = conn.CreateCommand();
        own.CommandText = "SELECT COUNT(*) FROM inventory_items WHERE item_id = @i AND user_id = @u";
        own.Parameters.AddWithValue("@i", itemId.Value);
        own.Parameters.AddWithValue("@u", uid.Value);
        if (Convert.ToInt64(await own.ExecuteScalarAsync()) == 0)
            return Results.Json(new { error = "That inventory item was not found." }, statusCode: 400);
    }

    try
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO expenses (user_id, item_id, expense_date, description, category, amount, payment_method, paid_to, notes)
            VALUES (@u, @item, @d, @desc, @cat, @amt, @pay, @paid, @notes)
            """;
        cmd.Parameters.AddWithValue("@u", uid.Value);
        cmd.Parameters.AddWithValue("@item", itemId == null ? DBNull.Value : itemId.Value);
        cmd.Parameters.AddWithValue("@d", b.Date);
        cmd.Parameters.AddWithValue("@desc", b.Desc!.Trim());
        cmd.Parameters.AddWithValue("@cat", b.Cat);
        cmd.Parameters.AddWithValue("@amt", b.Amt);
        cmd.Parameters.AddWithValue("@pay", pay);
        cmd.Parameters.AddWithValue("@paid", string.IsNullOrWhiteSpace(b.PaidTo) ? DBNull.Value : b.PaidTo.Trim());
        cmd.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(b.Notes) ? DBNull.Value : b.Notes.Trim());
        await cmd.ExecuteNonQueryAsync();
        var id = await Db.LastIdAsync(conn);
        if (!b.Undo)
            await LogActivityAsync(conn, null, uid.Value, "orange", $"Expense recorded: {b.Desc.Trim()} ({Peso(b.Amt)})");
        return Results.Ok(new { id = id.ToString() });
    }
    catch (MySqlException ex) when (ex.Number == 1062)
    {
        return Results.Json(new { error = "An expense for that inventory item already exists." }, statusCode: 409);
    }
});

app.MapPut("/api/expenses/{id}", async (string id, HttpContext ctx, ExpenseBody b) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    if (!long.TryParse(id, out var expenseId)) return Results.NotFound();
    var invalid = ValidateExpense(b);
    if (invalid != null) return Results.Json(new { error = invalid }, statusCode: 400);

    var pay = string.IsNullOrWhiteSpace(b.Pay) ? "Cash" : b.Pay;
    await using var conn = await db.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        UPDATE expenses
        SET expense_date = @d, description = @desc, category = @cat, amount = @amt,
            payment_method = @pay, paid_to = @paid, notes = @notes
        WHERE expense_id = @id AND user_id = @u
        """;
    cmd.Parameters.AddWithValue("@d", b.Date);
    cmd.Parameters.AddWithValue("@desc", b.Desc!.Trim());
    cmd.Parameters.AddWithValue("@cat", b.Cat);
    cmd.Parameters.AddWithValue("@amt", b.Amt);
    cmd.Parameters.AddWithValue("@pay", pay);
    cmd.Parameters.AddWithValue("@paid", string.IsNullOrWhiteSpace(b.PaidTo) ? DBNull.Value : b.PaidTo.Trim());
    cmd.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(b.Notes) ? DBNull.Value : b.Notes.Trim());
    cmd.Parameters.AddWithValue("@id", expenseId);
    cmd.Parameters.AddWithValue("@u", uid.Value);
    if (await cmd.ExecuteNonQueryAsync() == 0) return Results.NotFound();
    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/expenses/{id}", async (string id, HttpContext ctx) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    if (!long.TryParse(id, out var expenseId)) return Results.NotFound();

    await using var conn = await db.OpenAsync();
    ExpenseDto? snapshot = null;
    await using (var sel = conn.CreateCommand())
    {
        sel.CommandText = """
            SELECT expense_id, item_id, expense_date, description, category, amount, payment_method, paid_to, notes
            FROM expenses WHERE expense_id = @id AND user_id = @u
            """;
        sel.Parameters.AddWithValue("@id", expenseId);
        sel.Parameters.AddWithValue("@u", uid.Value);
        await using var r = await sel.ExecuteReaderAsync();
        if (await r.ReadAsync())
        {
            snapshot = new ExpenseDto
            {
                Id = r.GetInt64(r.GetOrdinal("expense_id")).ToString(),
                ItemId = r.IsDBNull(r.GetOrdinal("item_id")) ? null : r.GetInt64(r.GetOrdinal("item_id")).ToString(),
                Date = r.GetDateTime(r.GetOrdinal("expense_date")).ToString("yyyy-MM-dd"),
                Desc = r.GetString(r.GetOrdinal("description")),
                Cat = r.GetString(r.GetOrdinal("category")),
                Amt = r.GetDecimal(r.GetOrdinal("amount")),
                Pay = r.GetString(r.GetOrdinal("payment_method")),
                PaidTo = r.IsDBNull(r.GetOrdinal("paid_to")) ? "" : r.GetString(r.GetOrdinal("paid_to")),
                Notes = r.IsDBNull(r.GetOrdinal("notes")) ? "" : r.GetString(r.GetOrdinal("notes")),
            };
        }
        await r.DisposeAsync();
    }
    if (snapshot is null) return Results.NotFound();

    await using (var del = conn.CreateCommand())
    {
        del.CommandText = "DELETE FROM expenses WHERE expense_id = @id AND user_id = @u";
        del.Parameters.AddWithValue("@id", expenseId);
        del.Parameters.AddWithValue("@u", uid.Value);
        await del.ExecuteNonQueryAsync();
    }

    return Results.Ok(new { expense = snapshot });
});

/* ---------- Listings ---------- */

app.MapPost("/api/listings", async (HttpContext ctx, ListingBody b) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    var invalid = ValidateListing(b);
    if (invalid != null) return Results.Json(new { error = invalid }, statusCode: 400);

    await using var conn = await db.OpenAsync();
    await using (var role = conn.CreateCommand())
    {
        role.CommandText = "SELECT role FROM users WHERE user_id = @u";
        role.Parameters.AddWithValue("@u", uid.Value);
        var value = await role.ExecuteScalarAsync();
        if (value?.ToString() == "buyer")
            return Results.Json(new { error = "Only farmers can create listings." }, statusCode: 403);
    }

    var status = b.Status == "sold" ? "sold" : "available";
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = """
            INSERT INTO listings (owner_id, crop_name, category, quantity, unit, price_per_unit,
                                  description, location, contact_phone, status, photo)
            VALUES (@u, @n, @cat, @q, @unit, @price, @desc, @loc, @phone, @status, @photo)
            """;
        cmd.Parameters.AddWithValue("@u", uid.Value);
        cmd.Parameters.AddWithValue("@n", b.Name!.Trim());
        cmd.Parameters.AddWithValue("@cat", b.Category);
        cmd.Parameters.AddWithValue("@q", b.Qty);
        cmd.Parameters.AddWithValue("@unit", b.Unit);
        cmd.Parameters.AddWithValue("@price", b.Price);
        cmd.Parameters.AddWithValue("@desc", string.IsNullOrWhiteSpace(b.Desc) ? DBNull.Value : b.Desc.Trim());
        cmd.Parameters.AddWithValue("@loc", b.Loc!.Trim());
        cmd.Parameters.AddWithValue("@phone", string.IsNullOrWhiteSpace(b.Phone) ? DBNull.Value : b.Phone.Trim());
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@photo", string.IsNullOrEmpty(b.Photo) ? DBNull.Value : b.Photo);
        await cmd.ExecuteNonQueryAsync();
    }

    var id = await Db.LastIdAsync(conn);
    if (!b.Undo)
        await LogActivityAsync(conn, null, uid.Value, "purple",
            $"You listed {Num(b.Qty)} {b.Unit} of {b.Name.Trim()} in marketplace");

    return Results.Ok(new { id = id.ToString() });
});

app.MapDelete("/api/listings/{id}", async (string id, HttpContext ctx) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    if (!long.TryParse(id, out var listingId)) return Results.NotFound();

    await using var conn = await db.OpenAsync();
    ListingDto? snapshot = null;
    await using (var sel = conn.CreateCommand())
    {
        sel.CommandText = """
            SELECT crop_name, category, quantity, unit, price_per_unit, description, location, contact_phone, status, photo
            FROM listings WHERE listing_id = @id AND owner_id = @u
            """;
        sel.Parameters.AddWithValue("@id", listingId);
        sel.Parameters.AddWithValue("@u", uid.Value);
        await using var r = await sel.ExecuteReaderAsync();
        if (await r.ReadAsync())
        {
            snapshot = new ListingDto
            {
                Id = listingId.ToString(),
                Name = r.GetString(r.GetOrdinal("crop_name")),
                Category = r.GetString(r.GetOrdinal("category")),
                Qty = r.GetDecimal(r.GetOrdinal("quantity")),
                Unit = r.GetString(r.GetOrdinal("unit")),
                Price = r.GetDecimal(r.GetOrdinal("price_per_unit")),
                Desc = r.IsDBNull(r.GetOrdinal("description")) ? "" : r.GetString(r.GetOrdinal("description")),
                Loc = r.GetString(r.GetOrdinal("location")),
                Phone = r.IsDBNull(r.GetOrdinal("contact_phone")) ? "" : r.GetString(r.GetOrdinal("contact_phone")),
                Status = r.GetString(r.GetOrdinal("status")),
                Photo = r.IsDBNull(r.GetOrdinal("photo")) ? "" : r.GetString(r.GetOrdinal("photo")),
            };
        }
        await r.DisposeAsync();
    }
    if (snapshot is null) return Results.NotFound();

    await using (var del = conn.CreateCommand())
    {
        del.CommandText = "DELETE FROM listings WHERE listing_id = @id AND owner_id = @u";
        del.Parameters.AddWithValue("@id", listingId);
        del.Parameters.AddWithValue("@u", uid.Value);
        await del.ExecuteNonQueryAsync();
    }

    return Results.Ok(new { listing = snapshot });
});

app.MapPost("/api/listings/{id}/sold", async (string id, HttpContext ctx) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    if (!long.TryParse(id, out var listingId)) return Results.NotFound();

    await using var conn = await db.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    decimal qty, price;
    string crop, unit;
    await using (var sel = conn.CreateCommand())
    {
        sel.Transaction = tx;
        sel.CommandText = """
            SELECT quantity, price_per_unit, crop_name, unit FROM listings
            WHERE listing_id = @id AND owner_id = @u FOR UPDATE
            """;
        sel.Parameters.AddWithValue("@id", listingId);
        sel.Parameters.AddWithValue("@u", uid.Value);
        await using var r = await sel.ExecuteReaderAsync();
        if (!await r.ReadAsync()) { await tx.RollbackAsync(); return Results.NotFound(); }
        qty = r.GetDecimal(r.GetOrdinal("quantity"));
        price = r.GetDecimal(r.GetOrdinal("price_per_unit"));
        crop = r.GetString(r.GetOrdinal("crop_name"));
        unit = r.GetString(r.GetOrdinal("unit"));
        await r.DisposeAsync();
    }

    await using (var upd = conn.CreateCommand())
    {
        upd.Transaction = tx;
        upd.CommandText = "UPDATE listings SET status = 'sold' WHERE listing_id = @id";
        upd.Parameters.AddWithValue("@id", listingId);
        await upd.ExecuteNonQueryAsync();
    }

    var total = qty * price;
    await InsertSaleAsync(conn, tx, uid.Value, listingId, crop, qty, unit, total, "Walk-in buyer");
    await LogActivityAsync(conn, tx, uid.Value, "purple", $"Sold {Num(qty)} {unit} of {crop} ({Peso(total)})");

    await tx.CommitAsync();
    return Results.Ok(new { total });
});

app.MapPost("/api/listings/{id}/order", async (string id, HttpContext ctx, OrderBody b) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();
    if (!long.TryParse(id, out var listingId)) return Results.NotFound();

    await using var conn = await db.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    long ownerId;
    decimal qty, price;
    string crop, unit, status;
    await using (var sel = conn.CreateCommand())
    {
        sel.Transaction = tx;
        sel.CommandText = """
            SELECT owner_id, crop_name, quantity, unit, price_per_unit, status
            FROM listings WHERE listing_id = @id FOR UPDATE
            """;
        sel.Parameters.AddWithValue("@id", listingId);
        await using var r = await sel.ExecuteReaderAsync();
        if (!await r.ReadAsync()) { await tx.RollbackAsync(); return Results.NotFound(); }
        ownerId = r.GetInt64(r.GetOrdinal("owner_id"));
        crop = r.GetString(r.GetOrdinal("crop_name"));
        qty = r.GetDecimal(r.GetOrdinal("quantity"));
        unit = r.GetString(r.GetOrdinal("unit"));
        price = r.GetDecimal(r.GetOrdinal("price_per_unit"));
        status = r.GetString(r.GetOrdinal("status"));
        await r.DisposeAsync();
    }

    if (status != "available")
    {
        await tx.RollbackAsync();
        return Results.Json(new { error = "This listing is no longer available." }, statusCode: 400);
    }
    if (b.Qty <= 0 || b.Qty > qty)
    {
        await tx.RollbackAsync();
        return Results.Json(new { error = "Enter a quantity that is available for this listing." }, statusCode: 400);
    }

    var remaining = qty - b.Qty;
    var total = b.Qty * price;

    await using (var upd = conn.CreateCommand())
    {
        upd.Transaction = tx;
        upd.CommandText = "UPDATE listings SET quantity = @q, status = @s WHERE listing_id = @id";
        upd.Parameters.AddWithValue("@q", remaining);
        upd.Parameters.AddWithValue("@s", remaining <= 0 ? "sold" : "available");
        upd.Parameters.AddWithValue("@id", listingId);
        await upd.ExecuteNonQueryAsync();
    }

    if (ownerId == uid.Value)
    {
        await InsertSaleAsync(conn, tx, uid.Value, listingId, crop, b.Qty, unit, total, "Walk-in buyer");
        await LogActivityAsync(conn, tx, uid.Value, "purple",
            $"New order received ({Num(b.Qty)} {unit} {crop})");
    }
    else
    {
        var buyerName = await LoadUserNameAsync(conn, uid.Value, tx);
        await InsertSaleAsync(conn, tx, ownerId, listingId, crop, b.Qty, unit, total, buyerName);
        await LogActivityAsync(conn, tx, ownerId, "purple",
            $"New order from {buyerName}: {Num(b.Qty)} {unit} of {crop} ({Peso(total)})");
    }

    await tx.CommitAsync();
    return Results.Ok(new { total, remaining, sold = remaining <= 0 });
});

/* ---------- Profile, reset, notifications ---------- */

app.MapPut("/api/profile", async (HttpContext ctx, ProfileBody b) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();

    var name = (b.Name ?? "").Trim();
    if (name.Length == 0) return Results.Json(new { error = "Name is required." }, statusCode: 400);
    var farm = (b.Farm ?? "").Trim();
    var phone = (b.Phone ?? "").Trim();
    var loc = (b.Loc ?? "").Trim();

    await using var conn = await db.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE users
            SET full_name = @n, farm_name = @farm, phone = @phone,
                location = CASE WHEN @loc = '' THEN location ELSE @loc END
            WHERE user_id = @u
            """;
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@farm", farm.Length == 0 ? DBNull.Value : farm);
        cmd.Parameters.AddWithValue("@phone", phone);
        cmd.Parameters.AddWithValue("@loc", loc);
        cmd.Parameters.AddWithValue("@u", uid.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE listings
            SET contact_phone = @phone,
                location = CASE WHEN @loc = '' THEN location ELSE @loc END
            WHERE owner_id = @u
            """;
        cmd.Parameters.AddWithValue("@phone", phone);
        cmd.Parameters.AddWithValue("@loc", loc);
        cmd.Parameters.AddWithValue("@u", uid.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/reset", async (HttpContext ctx) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();

    await using var conn = await db.OpenAsync();
    string username;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT username FROM users WHERE user_id = @u";
        cmd.Parameters.AddWithValue("@u", uid.Value);
        username = (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
    }

    await using var tx = await conn.BeginTransactionAsync();
    foreach (var sql in new[]
    {
        "DELETE FROM expenses WHERE user_id = @u",
        "DELETE FROM inventory_items WHERE user_id = @u",
        "DELETE FROM sales WHERE user_id = @u",
        "DELETE FROM listings WHERE owner_id = @u",
        "DELETE FROM activities WHERE user_id = @u",
    })
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@u", uid.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    if (username == "juan")
        await Db.SeedDemoAsync(conn, uid.Value, includeListings: true, tx);

    await tx.CommitAsync();
    return Results.Ok(new { ok = true });
});

app.MapPut("/api/notifications/seen", async (HttpContext ctx) =>
{
    if (!db.Ready) return DbDown();
    var uid = CurrentUid(ctx);
    if (uid is null) return NotSignedIn();

    await using var conn = await db.OpenAsync();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "UPDATE users SET notifications_seen_at = NOW() WHERE user_id = @u";
        cmd.Parameters.AddWithValue("@u", uid.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    string? seen = null;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT notifications_seen_at FROM users WHERE user_id = @u";
        cmd.Parameters.AddWithValue("@u", uid.Value);
        var value = await cmd.ExecuteScalarAsync();
        if (value != null && value != DBNull.Value)
            seen = ((DateTime)value).ToString("yyyy-MM-ddTHH:mm:ss");
    }
    return Results.Ok(new { seen });
});

/* ---------- Helpers ---------- */

static string Num(decimal n) => n.ToString("#,##0.##", CultureInfo.InvariantCulture);
static string Peso(decimal n) => "₱" + Num(n);

static bool InSet(string? value, string[] set) => value != null && Array.IndexOf(set, value) >= 0;
static bool DateOk(string? value) =>
    value != null && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

static string? ValidateItem(ItemBody b)
{
    if (string.IsNullOrWhiteSpace(b.Name)) return "Item name is required.";
    if (b.Name.Trim().Length > 100) return "Item name is too long.";
    if (!InSet(b.Category, new[] { "Seeds", "Seedlings", "Material", "Equipment", "Produce" })) return "Choose a valid category.";
    if (b.Qty < 0) return "Quantity cannot be negative.";
    if (string.IsNullOrWhiteSpace(b.Unit) || b.Unit.Length > 20) return "Choose a valid unit.";
    if (!DateOk(b.Date)) return "Choose a valid date.";
    if (b.Cost < 0) return "Unit cost cannot be negative.";
    return null;
}

static string? ValidateExpense(ExpenseBody b)
{
    if (!DateOk(b.Date)) return "Choose a valid date.";
    if (!InSet(b.Cat, new[] { "Input", "Operations", "Maintenance", "Others" })) return "Choose a valid category.";
    if (string.IsNullOrWhiteSpace(b.Desc)) return "Description is required.";
    if (b.Desc.Trim().Length > 150) return "Description is too long.";
    if (b.Amt < 0) return "Amount cannot be negative.";
    if (!string.IsNullOrWhiteSpace(b.Pay) &&
        !InSet(b.Pay, new[] { "Cash", "GCash", "Bank Transfer", "Check", "Credit" })) return "Choose a valid payment method.";
    return null;
}

static string? ValidateListing(ListingBody b)
{
    if (string.IsNullOrWhiteSpace(b.Name)) return "Crop name is required.";
    if (b.Name.Trim().Length > 100) return "Crop name is too long.";
    if (!InSet(b.Category, new[] { "Vegetables", "Fruits", "Grains", "Others" })) return "Choose a valid category.";
    if (b.Qty <= 0) return "Quantity must be greater than zero.";
    if (string.IsNullOrWhiteSpace(b.Unit) || b.Unit.Length > 20) return "Choose a valid unit.";
    if (b.Price < 0) return "Price cannot be negative.";
    if (string.IsNullOrWhiteSpace(b.Loc)) return "Farm location is required.";
    return null;
}

static void BindItem(MySqlCommand cmd, long uid, ItemBody b)
{
    cmd.Parameters.AddWithValue("@u", uid);
    cmd.Parameters.AddWithValue("@n", b.Name!.Trim());
    cmd.Parameters.AddWithValue("@cat", b.Category);
    cmd.Parameters.AddWithValue("@qty", b.Qty);
    cmd.Parameters.AddWithValue("@unit", b.Unit);
    cmd.Parameters.AddWithValue("@date", b.Date);
    cmd.Parameters.AddWithValue("@supplier", string.IsNullOrWhiteSpace(b.Supplier) ? DBNull.Value : b.Supplier.Trim());
    cmd.Parameters.AddWithValue("@cost", b.Cost);
    cmd.Parameters.AddWithValue("@logexp", b.LogExp ? 1 : 0);
    cmd.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(b.Notes) ? DBNull.Value : b.Notes.Trim());
}

static string ExpenseCategoryFor(string itemCategory) => itemCategory switch
{
    "Equipment" => "Maintenance",
    "Produce" => "Others",
    _ => "Input",
};

static async Task SyncItemExpenseAsync(MySqlConnection conn, MySqlTransaction tx, long uid, long itemId, ItemBody b)
{
    var amount = b.Cost * b.Qty;
    if (b.LogExp && amount > 0)
    {
        await using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = """
            UPDATE expenses
            SET expense_date = @d, description = @desc, category = @cat, amount = @amt
            WHERE item_id = @item AND user_id = @u
            """;
        upd.Parameters.AddWithValue("@d", b.Date);
        upd.Parameters.AddWithValue("@desc", $"{b.Name!.Trim()} ({Num(b.Qty)} {b.Unit})");
        upd.Parameters.AddWithValue("@cat", ExpenseCategoryFor(b.Category!));
        upd.Parameters.AddWithValue("@amt", amount);
        upd.Parameters.AddWithValue("@item", itemId);
        upd.Parameters.AddWithValue("@u", uid);
        if (await upd.ExecuteNonQueryAsync() > 0) return;

        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO expenses (user_id, item_id, expense_date, description, category, amount, payment_method, paid_to, notes)
            VALUES (@u, @item, @d, @desc, @cat, @amt, 'Cash', @paid, @notes)
            """;
        ins.Parameters.AddWithValue("@u", uid);
        ins.Parameters.AddWithValue("@item", itemId);
        ins.Parameters.AddWithValue("@d", b.Date);
        ins.Parameters.AddWithValue("@desc", $"{b.Name.Trim()} ({Num(b.Qty)} {b.Unit})");
        ins.Parameters.AddWithValue("@cat", ExpenseCategoryFor(b.Category!));
        ins.Parameters.AddWithValue("@amt", amount);
        ins.Parameters.AddWithValue("@paid", string.IsNullOrWhiteSpace(b.Supplier) ? DBNull.Value : b.Supplier.Trim());
        ins.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(b.Notes) ? DBNull.Value : b.Notes.Trim());
        await ins.ExecuteNonQueryAsync();
    }
    else
    {
        await using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM expenses WHERE item_id = @item AND user_id = @u";
        del.Parameters.AddWithValue("@item", itemId);
        del.Parameters.AddWithValue("@u", uid);
        await del.ExecuteNonQueryAsync();
    }
}

static async Task LogActivityAsync(MySqlConnection conn, MySqlTransaction? tx, long uid, string type, string text)
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = "INSERT INTO activities (user_id, activity_type, message) VALUES (@u, @t, @m)";
    cmd.Parameters.AddWithValue("@u", uid);
    cmd.Parameters.AddWithValue("@t", type);
    cmd.Parameters.AddWithValue("@m", text);
    await cmd.ExecuteNonQueryAsync();
}

static async Task InsertSaleAsync(MySqlConnection conn, MySqlTransaction tx, long uid, long listingId,
    string crop, decimal qty, string unit, decimal amount, string buyer)
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = """
        INSERT INTO sales (user_id, listing_id, sale_date, crop_name, quantity, unit, amount, buyer_name)
        VALUES (@u, @listing, CURDATE(), @crop, @qty, @unit, @amount, @buyer)
        """;
    cmd.Parameters.AddWithValue("@u", uid);
    cmd.Parameters.AddWithValue("@listing", listingId);
    cmd.Parameters.AddWithValue("@crop", crop);
    cmd.Parameters.AddWithValue("@qty", qty);
    cmd.Parameters.AddWithValue("@unit", unit);
    cmd.Parameters.AddWithValue("@amount", amount);
    cmd.Parameters.AddWithValue("@buyer", buyer);
    await cmd.ExecuteNonQueryAsync();
}

static async Task<string> LoadUserNameAsync(MySqlConnection conn, long uid, MySqlTransaction? tx = null)
{
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = "SELECT full_name FROM users WHERE user_id = @u";
    cmd.Parameters.AddWithValue("@u", uid);
    return (await cmd.ExecuteScalarAsync())?.ToString() ?? "Buyer";
}

static async Task<UserDto?> LoadUserAsync(MySqlConnection conn, long uid)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT username, full_name, role, farm_name, location, phone, notifications_seen_at
        FROM users WHERE user_id = @u
        """;
    cmd.Parameters.AddWithValue("@u", uid);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return null;
    return new UserDto
    {
        Username = r.GetString(r.GetOrdinal("username")),
        Name = r.GetString(r.GetOrdinal("full_name")),
        Role = r.GetString(r.GetOrdinal("role")),
        Farm = r.IsDBNull(r.GetOrdinal("farm_name")) ? "" : r.GetString(r.GetOrdinal("farm_name")),
        Loc = r.GetString(r.GetOrdinal("location")),
        Phone = r.IsDBNull(r.GetOrdinal("phone")) ? "" : r.GetString(r.GetOrdinal("phone")),
        Seen = r.IsDBNull(r.GetOrdinal("notifications_seen_at"))
            ? null
            : r.GetDateTime(r.GetOrdinal("notifications_seen_at")).ToString("yyyy-MM-ddTHH:mm:ss"),
    };
}

static async Task<List<InventoryDto>> LoadInventoryAsync(MySqlConnection conn, long uid)
{
    var list = new List<InventoryDto>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT item_id, name, category, quantity, unit, added_date, supplier, unit_cost, log_expense, notes
        FROM inventory_items WHERE user_id = @u ORDER BY added_date DESC, item_id DESC
        """;
    cmd.Parameters.AddWithValue("@u", uid);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        list.Add(new InventoryDto
        {
            Id = r.GetInt64(r.GetOrdinal("item_id")).ToString(),
            Name = r.GetString(r.GetOrdinal("name")),
            Category = r.GetString(r.GetOrdinal("category")),
            Qty = r.GetDecimal(r.GetOrdinal("quantity")),
            Unit = r.GetString(r.GetOrdinal("unit")),
            Date = r.GetDateTime(r.GetOrdinal("added_date")).ToString("yyyy-MM-dd"),
            Supplier = r.IsDBNull(r.GetOrdinal("supplier")) ? null : r.GetString(r.GetOrdinal("supplier")),
            Cost = r.GetDecimal(r.GetOrdinal("unit_cost")),
            LogExp = r.GetBoolean(r.GetOrdinal("log_expense")),
            Notes = r.IsDBNull(r.GetOrdinal("notes")) ? null : r.GetString(r.GetOrdinal("notes")),
        });
    }
    return list;
}

static async Task<List<ExpenseDto>> LoadExpensesAsync(MySqlConnection conn, long uid)
{
    var list = new List<ExpenseDto>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT expense_id, item_id, expense_date, description, category, amount, payment_method, paid_to, notes
        FROM expenses WHERE user_id = @u ORDER BY expense_date DESC, expense_id DESC
        """;
    cmd.Parameters.AddWithValue("@u", uid);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        list.Add(new ExpenseDto
        {
            Id = r.GetInt64(r.GetOrdinal("expense_id")).ToString(),
            ItemId = r.IsDBNull(r.GetOrdinal("item_id")) ? null : r.GetInt64(r.GetOrdinal("item_id")).ToString(),
            Date = r.GetDateTime(r.GetOrdinal("expense_date")).ToString("yyyy-MM-dd"),
            Desc = r.GetString(r.GetOrdinal("description")),
            Cat = r.GetString(r.GetOrdinal("category")),
            Amt = r.GetDecimal(r.GetOrdinal("amount")),
            Pay = r.GetString(r.GetOrdinal("payment_method")),
            PaidTo = r.IsDBNull(r.GetOrdinal("paid_to")) ? "" : r.GetString(r.GetOrdinal("paid_to")),
            Notes = r.IsDBNull(r.GetOrdinal("notes")) ? "" : r.GetString(r.GetOrdinal("notes")),
        });
    }
    return list;
}

static async Task<decimal> LoadSalesTotalAsync(MySqlConnection conn, long uid)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COALESCE(SUM(amount), 0) FROM sales WHERE user_id = @u";
    cmd.Parameters.AddWithValue("@u", uid);
    return Convert.ToDecimal(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
}

static async Task<List<SaleDto>> LoadSalesLogAsync(MySqlConnection conn, long uid)
{
    var list = new List<SaleDto>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT sale_id, sale_date, crop_name, quantity, unit, amount, buyer_name, is_legacy
        FROM sales WHERE user_id = @u ORDER BY sale_date DESC, sale_id DESC LIMIT 100
        """;
    cmd.Parameters.AddWithValue("@u", uid);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        list.Add(new SaleDto
        {
            Id = r.GetInt64(r.GetOrdinal("sale_id")).ToString(),
            Date = r.GetDateTime(r.GetOrdinal("sale_date")).ToString("yyyy-MM-dd"),
            Crop = r.GetString(r.GetOrdinal("crop_name")),
            Qty = r.GetDecimal(r.GetOrdinal("quantity")),
            Unit = r.IsDBNull(r.GetOrdinal("unit")) ? "" : r.GetString(r.GetOrdinal("unit")),
            Amt = r.GetDecimal(r.GetOrdinal("amount")),
            Buyer = r.IsDBNull(r.GetOrdinal("buyer_name")) ? "" : r.GetString(r.GetOrdinal("buyer_name")),
            Legacy = r.GetBoolean(r.GetOrdinal("is_legacy")),
        });
    }
    return list;
}

static async Task<List<ActivityDto>> LoadActivitiesAsync(MySqlConnection conn, long uid)
{
    var list = new List<ActivityDto>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT activity_id, activity_type, message, created_at
        FROM activities WHERE user_id = @u ORDER BY created_at DESC, activity_id DESC LIMIT 20
        """;
    cmd.Parameters.AddWithValue("@u", uid);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        list.Add(new ActivityDto
        {
            Id = r.GetInt64(r.GetOrdinal("activity_id")).ToString(),
            Type = r.GetString(r.GetOrdinal("activity_type")),
            Text = r.GetString(r.GetOrdinal("message")),
            At = r.GetDateTime(r.GetOrdinal("created_at")).ToString("yyyy-MM-ddTHH:mm:ss"),
        });
    }
    return list;
}

static async Task<List<ListingDto>> LoadMarketAsync(MySqlConnection conn)
{
    var list = new List<ListingDto>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT l.listing_id, l.crop_name, l.category, l.quantity, l.unit, l.price_per_unit,
               l.description, l.location, l.contact_phone, l.status, l.photo,
               u.username, u.farm_name
        FROM listings l
        JOIN users u ON u.user_id = l.owner_id
        ORDER BY l.created_at DESC, l.listing_id DESC
        """;
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        list.Add(new ListingDto
        {
            Id = r.GetInt64(r.GetOrdinal("listing_id")).ToString(),
            Name = r.GetString(r.GetOrdinal("crop_name")),
            Category = r.GetString(r.GetOrdinal("category")),
            Qty = r.GetDecimal(r.GetOrdinal("quantity")),
            Unit = r.GetString(r.GetOrdinal("unit")),
            Price = r.GetDecimal(r.GetOrdinal("price_per_unit")),
            Desc = r.IsDBNull(r.GetOrdinal("description")) ? "" : r.GetString(r.GetOrdinal("description")),
            Loc = r.GetString(r.GetOrdinal("location")),
            Phone = r.IsDBNull(r.GetOrdinal("contact_phone")) ? "" : r.GetString(r.GetOrdinal("contact_phone")),
            Owner = r.GetString(r.GetOrdinal("username")),
            Farm = r.IsDBNull(r.GetOrdinal("farm_name")) ? "" : r.GetString(r.GetOrdinal("farm_name")),
            Status = r.GetString(r.GetOrdinal("status")),
            Photo = r.IsDBNull(r.GetOrdinal("photo")) ? "" : r.GetString(r.GetOrdinal("photo")),
        });
    }
    return list;
}

app.Run();

/* ---------- Types ---------- */

public sealed class SessionStore
{
    private readonly Dictionary<string, (long UserId, DateTime Expires)> _sessions = new();
    private readonly object _lock = new();

    public string Create(long userId)
    {
        var token = Guid.NewGuid().ToString("N");
        lock (_lock) _sessions[token] = (userId, DateTime.UtcNow.AddDays(7));
        return token;
    }

    public long? Get(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(token, out var session)) return null;
            if (session.Expires < DateTime.UtcNow)
            {
                _sessions.Remove(token);
                return null;
            }
            return session.UserId;
        }
    }

    public void Remove(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        lock (_lock) _sessions.Remove(token);
    }
}

public sealed record LoginBody(string? Username, string? Password);
public sealed record SignupBody(string? Name, string? Username, string? Password, string? Role);
public sealed record ItemBody(string? Name, string? Category, decimal Qty, string? Unit, string? Date,
    string? Supplier, decimal Cost, bool LogExp, string? Notes, bool Undo = false);
public sealed record QtyBody(decimal Qty);
public sealed record ExpenseBody(string? Date, string? Cat, string? Desc, decimal Amt, string? Pay,
    string? PaidTo, string? Notes, string? ItemId, bool Undo = false);
public sealed record ListingBody(string? Name, string? Category, decimal Qty, string? Unit, decimal Price,
    string? Desc, string? Loc, string? Phone, string? Photo, string? Status, bool Undo = false);
public sealed record OrderBody(decimal Qty);
public sealed record ProfileBody(string? Name, string? Farm, string? Phone, string? Loc);

public sealed class UserDto
{
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "farmer";
    public string Farm { get; set; } = "";
    public string Loc { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Seen { get; set; }
}

public sealed class DataDto
{
    public List<InventoryDto> Inventory { get; set; } = new();
    public List<ExpenseDto> Expenses { get; set; } = new();
    public decimal Sales { get; set; }
    public List<SaleDto> SalesLog { get; set; } = new();
    public List<ActivityDto> Activities { get; set; } = new();
    public string? Seen { get; set; }
}

public sealed class BootstrapDto
{
    public UserDto User { get; set; } = new();
    public DataDto Data { get; set; } = new();
    public List<ListingDto> Market { get; set; } = new();
}

public sealed class InventoryDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Qty { get; set; }
    public string Unit { get; set; } = "";
    public string Date { get; set; } = "";
    public string? Supplier { get; set; }
    public decimal Cost { get; set; }
    public bool LogExp { get; set; }
    public string? Notes { get; set; }
}

public sealed class ItemDto
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Qty { get; set; }
    public string Unit { get; set; } = "";
    public string Date { get; set; } = "";
    public string? Supplier { get; set; }
    public decimal Cost { get; set; }
    public bool LogExp { get; set; }
    public string? Notes { get; set; }
}

public sealed class ExpenseDto
{
    public string Id { get; set; } = "";
    public string? ItemId { get; set; }
    public string Date { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Cat { get; set; } = "";
    public decimal Amt { get; set; }
    public string Pay { get; set; } = "Cash";
    public string PaidTo { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class ListingDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Qty { get; set; }
    public string Unit { get; set; } = "";
    public decimal Price { get; set; }
    public string Desc { get; set; } = "";
    public string Loc { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Farm { get; set; } = "";
    public string Status { get; set; } = "available";
    public string Photo { get; set; } = "";
}

public sealed class SaleDto
{
    public string Id { get; set; } = "";
    public string Date { get; set; } = "";
    public string Crop { get; set; } = "";
    public decimal Qty { get; set; }
    public string Unit { get; set; } = "";
    public decimal Amt { get; set; }
    public string Buyer { get; set; } = "";
    public bool Legacy { get; set; }
}

public sealed class ActivityDto
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "green";
    public string Text { get; set; } = "";
    public string At { get; set; } = "";
}
