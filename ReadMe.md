# ✨ MinimalSQLReader API

A dynamic, environment-aware OData-to-SQL reader for .NET (ASP.NET Core), featuring bearer token authentication, endpoint mapping via JSON configuration files, and Serilog-powered logging. Supports SQL Server and enables flexible query generation using OData syntax.

---

## 🚀 Features

- ✅ Environment-aware SQL database routing
- ✨ OData-to-SQL conversion for dynamic querying
- 🔐 Bearer Token Authentication (SQLite-backed)
- 🔄 JSON-configured endpoints with column-level filtering
- 🔄 Proxy-style SQL execution for HTTP methods (GET/POST)
- 🖊️ Automatic Swagger documentation for endpoints
- 📊 Serilog-powered logging with daily rotation

---

## 📦 Requirements

- [.NET 8+ SDK](https://dotnet.microsoft.com/en-us/download)
- SQL Server database access
- Local write access to `log/`, `auth.db`, and `config/` folders

---

## 🛠️ Setup

### 1. Clone the repository

```bash
git clone https://github.com/your-org/minimalsqlreader.git
cd minimalsqlreader
```

### 2. Create required folders

```bash
mkdir log
mkdir config
mkdir config/environments
mkdir config/endpoints
```

### 3. Add a settings file

**`config/environments/settings.json`**

```json
{
  "ServerName": "localhost",
  "ConnectionString": "Server=VM2K22;Database=600;Trusted_Connection=True;TrustServerCertificate=true;"
}
```

### 4. Add an endpoint configuration

**Example: `config/endpoints/items.json`**

```json
{
  "DatabaseObjectName": "Items",
  "DatabaseSchema": "dbo",
  "AllowedColumns": [
    "ItemCode",
    "Description",
    "Assortment",
    "sysguid"
  ]
}
```

### 5. Run the application

```bash
dotnet run
```

---

## 🔐 Authentication

- On first run, a SQLite database `auth.db` will be created.
- If no tokens exist, a default token will be generated and logged:

```text
🔑 Generated token: 8f3e7b9e-4c7a-4e5c-b6c1-fc129ad6fe65
```

- Include it in requests as:

```http
Authorization: Bearer YOUR_TOKEN
```

---

## 🔄 API Usage

**Pattern:**

```
/api/{environment}/{endpoint}/{odata-query}
```

**Example:**

```http
GET /api/600/items?$filter=Assortment eq 'Books'
```

Will execute the following SQL query (based on `items.json`):

```sql
SELECT ItemCode, Description, Assortment, sysguid
FROM dbo.Items
WHERE Assortment = 'Books'
```

---

## 📅 Logging

- Logs are stored in the `/log` folder and rotate daily.
- Console output includes timestamps.
- EF Core database commands are logged at `Warning` level to avoid verbosity.

---

## 📺 Project Structure

| Path                          | Description                                      |
|-------------------------------|--------------------------------------------------|
| `/log`                        | Rolling file logs                                |
| `/auth.db`                    | SQLite database storing bearer tokens            |
| `/config/environments`        | Environment configurations                       |
| `/config/endpoints`           | Endpoint-specific database mappings              |

---

## ✨ Credits

Built with ❤️ using:

- [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/)
- [DynamicODataToSQL](https://github.com/your-org/dynamicodata-to-sql)
- [Serilog](https://serilog.net/)
- [SQLite](https://www.sqlite.org/index.html)

*Generated on 2025-03-23*

