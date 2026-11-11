using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace AgriTrack.Api;

public sealed class Db
{
    private readonly string _baseConnection;
    private readonly string _database;
    private readonly string _fullConnection;

    public bool Ready { get; private set; }
    public string? Error { get; private set; }

    public Db(string host, uint port, string user, string password, string database)
    {
        _database = database;
        var b = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = port,
            UserID = user,
            Password = password,
            Database = "",
            AllowPublicKeyRetrieval = true,
            SslMode = MySqlSslMode.Preferred,
            CharacterSet = "utf8mb4",
            ConnectionTimeout = 5,
        };
        _baseConnection = b.ConnectionString;
        b.Database = database;
        _fullConnection = b.ConnectionString;
    }

    public async Task<MySqlConnection> OpenAsync()
    {
        var conn = new MySqlConnection(_fullConnection);
        await conn.OpenAsync();
        return conn;
    }

    public async Task InitAsync()
    {
        try
        {
            if (!Regex.IsMatch(_database, "^[A-Za-z0-9_]+$"))
                throw new InvalidOperationException("Database name may only contain letters, numbers and underscores.");

            await using (var admin = new MySqlConnection(_baseConnection))
            {
                await admin.OpenAsync();
                await using var exists = admin.CreateCommand();
                exists.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @name";
                exists.Parameters.AddWithValue("@name", _database);
                if (Convert.ToInt64(await exists.ExecuteScalarAsync()) == 0)
                {
                    await using var create = admin.CreateCommand();
                    create.CommandText = $"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
                    await create.ExecuteNonQueryAsync();
                }
            }

            await using (var conn = await OpenAsync())
            {
                foreach (var ddl in Ddl)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = ddl;
                    await cmd.ExecuteNonQueryAsync();
                }
                await MigrateInventoryCategoryAsync(conn);
                await SeedIfEmptyAsync(conn);
            }

            Ready = true;
            Error = null;
        }
        catch (Exception ex)
        {
            Ready = false;
            Error = ex.Message;
        }
    }

    public static async Task<long> LastIdAsync(MySqlConnection conn, MySqlTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT LAST_INSERT_ID()";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task MigrateInventoryCategoryAsync(MySqlConnection conn)
    {
        string? columnType;
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = """
                SELECT COLUMN_TYPE FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'inventory_items' AND COLUMN_NAME = 'category'
                """;
            columnType = await check.ExecuteScalarAsync() as string;
        }
        if (columnType == null || !columnType.Contains("'Input'")) return;

        foreach (var step in new[]
        {
            "ALTER TABLE inventory_items MODIFY category ENUM('Seeds','Seedlings','Input','Material','Equipment','Produce') NOT NULL",
            "UPDATE inventory_items SET category = 'Material' WHERE category = 'Input'",
            "ALTER TABLE inventory_items MODIFY category ENUM('Seeds','Seedlings','Material','Equipment','Produce') NOT NULL",
        })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = step;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedIfEmptyAsync(MySqlConnection conn)
    {
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM users";
            if (Convert.ToInt64(await check.ExecuteScalarAsync()) > 0) return;
        }

        long juanId;
        await using (var juan = conn.CreateCommand())
        {
            juan.CommandText = """
                INSERT INTO users (username, password_hash, full_name, role, farm_name, location, phone)
                VALUES ('juan', @p, 'Juan Dela Cruz', 'farmer', 'Dela Cruz Farm', 'Bongabong, Oriental Mindoro', '0917 555 0101')
                """;
            juan.Parameters.AddWithValue("@p", Passwords.Hash("farm1234"));
            await juan.ExecuteNonQueryAsync();
            juanId = await LastIdAsync(conn);
        }

        await using (var maria = conn.CreateCommand())
        {
            maria.CommandText = """
                INSERT INTO users (username, password_hash, full_name, role, farm_name, location, phone)
                VALUES ('maria', @p, 'Maria Santos', 'buyer', NULL, 'Bongabong, Oriental Mindoro', '0918 555 0202')
                """;
            maria.Parameters.AddWithValue("@p", Passwords.Hash("buyer123"));
            await maria.ExecuteNonQueryAsync();
        }

        await SeedDemoAsync(conn, juanId, includeListings: true);
    }

    public static async Task SeedDemoAsync(MySqlConnection conn, long userId, bool includeListings, MySqlTransaction? tx = null)
    {
        var inventory = new (string Name, string Cat, decimal Qty, string Unit, string Date, string Supplier, decimal Cost, string? Notes)[]
        {
            ("Rice Seeds", "Seeds", 50, "kg", "2025-04-10", "Mindoro Seed Co.", 80, "Certified hybrid variety."),
            ("Fertilizer", "Material", 10, "bags", "2025-04-08", "Agri Supply Store", 950, "Complete 14-14-14."),
            ("Tomato Seedlings", "Seedlings", 20, "sack", "2025-04-05", "Dela Cruz Nursery", 150, null),
            ("Organic Compost", "Material", 15, "bags", "2025-04-02", "Barangay Compost Hub", 320, null),
            ("Water Pump", "Equipment", 1, "unit", "2025-03-28", "Mindoro Machinery", 8500, "2-inch gasoline pump."),
        };
        foreach (var i in inventory)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO inventory_items (user_id, name, category, quantity, unit, added_date, supplier, unit_cost, log_expense, notes)
                VALUES (@u, @n, @c, @q, @un, @d, @s, @cost, 0, @notes)
                """;
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@n", i.Name);
            cmd.Parameters.AddWithValue("@c", i.Cat);
            cmd.Parameters.AddWithValue("@q", i.Qty);
            cmd.Parameters.AddWithValue("@un", i.Unit);
            cmd.Parameters.AddWithValue("@d", i.Date);
            cmd.Parameters.AddWithValue("@s", i.Supplier);
            cmd.Parameters.AddWithValue("@cost", i.Cost);
            cmd.Parameters.AddWithValue("@notes", (object?)i.Notes ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        var expenses = new (string Date, string Desc, string Cat, decimal Amt)[]
        {
            ("2025-01-12", "Fertilizer", "Input", 3200),
            ("2025-01-20", "Fuel", "Operations", 1600),
            ("2025-02-06", "Seeds", "Input", 1800),
            ("2025-02-18", "Equipment Repair", "Maintenance", 1100),
            ("2025-03-04", "Fertilizer", "Input", 2400),
            ("2025-03-22", "Farm labor", "Operations", 1800),
            ("2025-04-01", "Miscellaneous", "Others", 300),
            ("2025-04-05", "Equipment Repair", "Maintenance", 900),
            ("2025-04-12", "Fuel", "Operations", 600),
            ("2025-04-18", "Seeds", "Input", 800),
            ("2025-04-23", "Fertilizer", "Input", 1200),
            ("2025-05-06", "Farm labor", "Operations", 3800),
            ("2025-05-14", "Fertilizer", "Input", 2400),
        };
        foreach (var e in expenses)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO expenses (user_id, expense_date, description, category, amount, payment_method)
                VALUES (@u, @d, @desc, @cat, @amt, 'Cash')
                """;
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@d", e.Date);
            cmd.Parameters.AddWithValue("@desc", e.Desc);
            cmd.Parameters.AddWithValue("@cat", e.Cat);
            cmd.Parameters.AddWithValue("@amt", e.Amt);
            await cmd.ExecuteNonQueryAsync();
        }

        var sales = new (string Date, string Crop, decimal Qty, string Unit, decimal Amt, string Buyer)[]
        {
            ("2025-01-18", "Tomato", 40, "kg", 2000, "Maria Santos"),
            ("2025-02-12", "Mango", 50, "kg", 3500, "Bayan Market"),
            ("2025-03-08", "Eggplant", 30, "kg", 1350, "Rosa Lim"),
            ("2025-03-26", "Banana", 60, "kg", 1500, "Walk-in buyer"),
            ("2025-04-14", "Okra", 25, "kg", 1000, "Maria Santos"),
            ("2025-05-03", "Tomato", 69, "kg", 3450, "Bayan Market"),
        };
        foreach (var s in sales)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sales (user_id, sale_date, crop_name, quantity, unit, amount, buyer_name, is_legacy)
                VALUES (@u, @d, @crop, @q, @unit, @amt, @buyer, 0)
                """;
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@d", s.Date);
            cmd.Parameters.AddWithValue("@crop", s.Crop);
            cmd.Parameters.AddWithValue("@q", s.Qty);
            cmd.Parameters.AddWithValue("@unit", s.Unit);
            cmd.Parameters.AddWithValue("@amt", s.Amt);
            cmd.Parameters.AddWithValue("@buyer", s.Buyer);
            await cmd.ExecuteNonQueryAsync();
        }

        var activities = new (string Type, string Text, string At)[]
        {
            ("green", "You added 10 sacks of rice to inventory", "2025-04-24 10:30:00"),
            ("orange", "Expense recorded: Fertilizer (₱1,200)", "2025-04-23 15:45:00"),
            ("purple", "New order received (2 kg. tomatoes)", "2025-04-22 13:20:00"),
            ("green", "You listed 5 kg. of eggplant in marketplace", "2025-04-21 09:15:00"),
        };
        foreach (var a in activities)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO activities (user_id, activity_type, message, created_at)
                VALUES (@u, @t, @m, @at)
                """;
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@t", a.Type);
            cmd.Parameters.AddWithValue("@m", a.Text);
            cmd.Parameters.AddWithValue("@at", a.At);
            await cmd.ExecuteNonQueryAsync();
        }

        if (!includeListings) return;

        var listings = new (string Name, string Cat, decimal Qty, string Unit, decimal Price, string? Desc)[]
        {
            ("Tomato", "Vegetables", 40, "kg", 50, "Freshly harvested, pesticide-free tomatoes."),
            ("Eggplant", "Vegetables", 25, "kg", 45, null),
            ("Rice", "Grains", 12, "sack", 30, null),
            ("Banana", "Fruits", 60, "kg", 25, null),
            ("Okra", "Vegetables", 15, "kg", 40, null),
            ("Mango", "Fruits", 30, "kg", 70, null),
        };
        foreach (var l in listings)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO listings (owner_id, crop_name, category, quantity, unit, price_per_unit,
                                      description, location, contact_phone, status)
                VALUES (@u, @n, @c, @q, @un, @price, @desc, 'Bongabong, Oriental Mindoro', '0917 555 0101', 'available')
                """;
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@n", l.Name);
            cmd.Parameters.AddWithValue("@c", l.Cat);
            cmd.Parameters.AddWithValue("@q", l.Qty);
            cmd.Parameters.AddWithValue("@un", l.Unit);
            cmd.Parameters.AddWithValue("@price", l.Price);
            cmd.Parameters.AddWithValue("@desc", (object?)l.Desc ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static readonly string[] Ddl =
    {
        """
        CREATE TABLE IF NOT EXISTS users (
          user_id INT UNSIGNED NOT NULL AUTO_INCREMENT,
          username VARCHAR(50) NOT NULL,
          password_hash VARCHAR(255) NOT NULL,
          full_name VARCHAR(100) NOT NULL,
          role ENUM('farmer','buyer') NOT NULL DEFAULT 'farmer',
          farm_name VARCHAR(100) NULL,
          location VARCHAR(150) NOT NULL DEFAULT 'Bongabong, Oriental Mindoro',
          phone VARCHAR(25) NULL,
          notifications_seen_at DATETIME NULL,
          created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
          PRIMARY KEY (user_id),
          UNIQUE KEY uq_users_username (username)
        ) ENGINE=InnoDB
        """,
        """
        CREATE TABLE IF NOT EXISTS inventory_items (
          item_id INT UNSIGNED NOT NULL AUTO_INCREMENT,
          user_id INT UNSIGNED NOT NULL,
          name VARCHAR(100) NOT NULL,
          category ENUM('Seeds','Seedlings','Material','Equipment','Produce') NOT NULL,
          quantity DECIMAL(10,2) NOT NULL DEFAULT 0,
          unit VARCHAR(20) NOT NULL DEFAULT 'kg',
          added_date DATE NOT NULL,
          supplier VARCHAR(120) NULL,
          unit_cost DECIMAL(12,2) NOT NULL DEFAULT 0,
          log_expense TINYINT(1) NOT NULL DEFAULT 1,
          notes TEXT NULL,
          created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          PRIMARY KEY (item_id),
          KEY idx_inv_user_date (user_id, added_date),
          CONSTRAINT fk_inv_user FOREIGN KEY (user_id) REFERENCES users (user_id) ON DELETE CASCADE,
          CONSTRAINT chk_inv_qty CHECK (quantity >= 0),
          CONSTRAINT chk_inv_cost CHECK (unit_cost >= 0)
        ) ENGINE=InnoDB
        """,
        """
        CREATE TABLE IF NOT EXISTS expenses (
          expense_id INT UNSIGNED NOT NULL AUTO_INCREMENT,
          user_id INT UNSIGNED NOT NULL,
          item_id INT UNSIGNED NULL,
          expense_date DATE NOT NULL,
          description VARCHAR(150) NOT NULL,
          category ENUM('Input','Operations','Maintenance','Others') NOT NULL,
          amount DECIMAL(12,2) NOT NULL,
          payment_method ENUM('Cash','GCash','Bank Transfer','Check','Credit') NOT NULL DEFAULT 'Cash',
          paid_to VARCHAR(120) NULL,
          notes TEXT NULL,
          created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          PRIMARY KEY (expense_id),
          UNIQUE KEY uq_exp_item (item_id),
          KEY idx_exp_user_date (user_id, expense_date),
          CONSTRAINT fk_exp_user FOREIGN KEY (user_id) REFERENCES users (user_id) ON DELETE CASCADE,
          CONSTRAINT fk_exp_item FOREIGN KEY (item_id) REFERENCES inventory_items (item_id) ON DELETE CASCADE,
          CONSTRAINT chk_exp_amount CHECK (amount >= 0)
        ) ENGINE=InnoDB
        """,
        """
        CREATE TABLE IF NOT EXISTS listings (
          listing_id INT UNSIGNED NOT NULL AUTO_INCREMENT,
          owner_id INT UNSIGNED NOT NULL,
          crop_name VARCHAR(100) NOT NULL,
          category ENUM('Vegetables','Fruits','Grains','Others') NOT NULL,
          quantity DECIMAL(10,2) NOT NULL,
          unit VARCHAR(20) NOT NULL DEFAULT 'kg',
          price_per_unit DECIMAL(12,2) NOT NULL,
          description TEXT NULL,
          location VARCHAR(150) NOT NULL,
          contact_phone VARCHAR(25) NULL,
          status ENUM('available','sold') NOT NULL DEFAULT 'available',
          photo MEDIUMTEXT NULL,
          created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
          PRIMARY KEY (listing_id),
          KEY idx_listing_status_cat (status, category),
          KEY idx_listing_owner (owner_id, status),
          CONSTRAINT fk_listing_owner FOREIGN KEY (owner_id) REFERENCES users (user_id) ON DELETE CASCADE,
          CONSTRAINT chk_listing_qty CHECK (quantity >= 0),
          CONSTRAINT chk_listing_price CHECK (price_per_unit >= 0)
        ) ENGINE=InnoDB
        """,
        """
        CREATE TABLE IF NOT EXISTS sales (
          sale_id INT UNSIGNED NOT NULL AUTO_INCREMENT,
          user_id INT UNSIGNED NOT NULL,
          listing_id INT UNSIGNED NULL,
          sale_date DATE NOT NULL,
          crop_name VARCHAR(100) NOT NULL,
          quantity DECIMAL(10,2) NOT NULL DEFAULT 0,
          unit VARCHAR(20) NULL,
          amount DECIMAL(12,2) NOT NULL,
          buyer_name VARCHAR(100) NULL,
          is_legacy TINYINT(1) NOT NULL DEFAULT 0,
          created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          PRIMARY KEY (sale_id),
          KEY idx_sale_user_date (user_id, sale_date),
          KEY idx_sale_listing (listing_id),
          CONSTRAINT fk_sale_user FOREIGN KEY (user_id) REFERENCES users (user_id) ON DELETE CASCADE,
          CONSTRAINT fk_sale_listing FOREIGN KEY (listing_id) REFERENCES listings (listing_id) ON DELETE SET NULL
        ) ENGINE=InnoDB
        """,
        """
        CREATE TABLE IF NOT EXISTS activities (
          activity_id INT UNSIGNED NOT NULL AUTO_INCREMENT,
          user_id INT UNSIGNED NOT NULL,
          activity_type ENUM('green','orange','purple') NOT NULL,
          message VARCHAR(255) NOT NULL,
          created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          PRIMARY KEY (activity_id),
          KEY idx_act_user_time (user_id, created_at),
          CONSTRAINT fk_act_user FOREIGN KEY (user_id) REFERENCES users (user_id) ON DELETE CASCADE
        ) ENGINE=InnoDB
        """,
    };
}

public static class Passwords
{
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
            var iterations = int.Parse(parts[1], CultureInfo.InvariantCulture);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
