# ✨ Minimal SQL Server API

A dynamic, environment-aware SQL Server API for Internet Information Services. Now with CRUD-support!

Lightweight and tuilt on .NET (ASP.NET Core), featuring secure bearer token authentication, endpoint mapping via JSON configuration files, and Serilog-powered logging. Solely supports SQL Server and enables flexible query generation using OData syntax.

![Screenshot of Swagger UI](https://raw.githubusercontent.com/hawkinslabdev/minimalsqlreader/refs/heads/main/Source/example.png)

## 🚀 Features
- Environment-aware SQL database routing
- Secure authentication, with optional Azure Key Vault support
- JSON-configured endpoints
- SQL execution for HTTP methods with granular control (GET/POST/MERGE/DELETE)
- Stored procedure support for data manipulation
- Automatic Swagger documentation for endpoints
- Serilog-powered logging with daily rotation

## 🗺️ Roadmap
- [ ] Nothing at this moment.

Feel free to commit a pull request with your proposed features!

## 📦 Requirements
- [.NET 8+ ASP.NET Core Runtime](https://dotnet.microsoft.com/en-us/download)
- Internet Information Services
- SQL Server database access
- Local write access to local folder

---

## 🛠️ Setup

The setup process is lean. Download the latest release, prepare your database connection, change the configuration files and set-up the app on your webserver.

### 1. Download the release
Download the latest release from the releases section and extract it to your desired location.

### 2. Create required folders
These folders will be automatically created when the application runs, but you can create them manually if needed:
```bash
mkdir log
mkdir tokens
mkdir environments
mkdir endpoints
```

The application will automatically generate necessary structures on first run, including the authentication database and token files.

### 3. Add a settings file
**`config/environments/settings.json`**
```json
{
  "ServerName": "localhost",
  "ConnectionString": "Server=localhost;Database=AdventureWorks;Trusted_Connection=True;TrustServerCertificate=true;"
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

Configure the application as a website in Internet Information Services, and you're done! Make sure to bind the application pool to an user with the right permissions, if you're not using a connection string that's bound to a specific user.

---

## 👮 Secure authentication
- On first run, a SQLite database `auth.db` will be created with an enhanced security model
- The system automatically generates a token bound to the machine name:
  ```text
  🗝️ Generated token for SERVER-1: <your-token>
  💾 Token saved to: /tokens/SERVER-1.txt
  ```
- Tokens are securely stored in the database using unique user names with PBKDF2 hashing with SHA256.

- Include the token in requests as:
  ```http
  Authorization: Bearer YOUR_TOKEN
  ```
- Each user's token is saved to a dedicated file at `/tokens/<username>.txt` for easy distribution

### Managing Tokens
The system automatically creates tokens during initialization, but you can also:
- Check the `/tokens/` directory for plain text token files
- Access tokens in the application logs during generation
- Bind tokens to specific usernames for better security

## 🗝️ Azure Key Vault Integration
The application supports Azure Key Vault for secure secret management. Set the KEYVAULT_URI environment variable to your Key Vault's URI, and create secrets following the {environment}-ConnectionString and {environment}-ServerName naming convention.

### Quick Setup
1. Set the Key Vault URI environment variable:
   ```powershell
   $env:KEYVAULT_URI = "https://your-keyvault-name.vault.azure.net/"
   ```
   Or persistently:
   ```powershell
   [Environment]::SetEnvironmentVariable("KEYVAULT_URI", "https://your-keyvault-name.vault.azure.net/", "Machine")
   ```

2. Create secrets with naming convention:
   ```
   {environment}-ConnectionString
   {environment}-ServerName
   ```
If Key Vault is unavailable, the application automatically falls back to local `settings.json`.

---

## 🔄 API Usage
**Pattern:**
```
/api/{environment}/{endpoint}/{odata-query}
```
**Example:**
```http
GET /api/AdventureWorks/items?$filter=Assortment eq 'Books'
```
Will execute the following SQL query (based on `items.json`):
```sql
SELECT ItemCode, Description, Assortment, sysguid
FROM dbo.Items
WHERE Assortment = 'Books'
```

## 📅 Logging
- Logs are stored in the `/log` folder and rotate daily.
- Console output includes timestamps.
- EF Core database commands are logged at `Warning` level to avoid verbosity.
- Authentication events are logged for auditing purposes.

## 📺 Project Structure
| Path                          | Description                                      |
|-------------------------------|--------------------------------------------------|
| `/log`                        | Rolling file logs                                |
| `/auth.db`                    | SQLite database storing secure token hashes      |
| `/tokens`                     | Plain text token files for distribution          |
| `/config/environments`        | Environment configurations                       |
| `/config/endpoints`           | Endpoint-specific database mappings              |

---

## 🔒 Security Model
The authentication system implements industry best practices:
- No plaintext tokens stored in the database
- Cryptographically secure hashing with PBKDF2/SHA256
- Username binding for token ownership and auditing
- File-based token distribution for token distribution

---

## ✨ Credits
Built with ❤️ using:
- [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/)
- [DynamicODataToSQL](https://github.com/your-org/dynamicodata-to-sql)
- [Serilog](https://serilog.net/)
- [SQLite](https://www.sqlite.org/index.html)

Feel free to submit a PR if you'd like to contribute.
