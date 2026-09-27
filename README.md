# CRPT Workbench

Blazor Server workbench for True API, National Catalog and SUZ. Safe mock mode remains the default. When explicitly enabled through server configuration, True API can authenticate with a CryptoPro-backed certificate and execute the read-only production `POST /cises/info`; the National Catalog can read an own product card and its permit documents by one GTIN with the protected profile Bearer token. SUZ remains a local mock.

The UI includes light and dark themes, a collapsible navigation panel, back navigation, and an API console with user-friendly and full technical response views. True API includes a local implementation of `POST /cises/info`: paste up to 1000 normalized marking or package codes, one per line, to see product details, status, dates, shelf life, permit documents, and immediate package relationships. The field hint contains deterministic UNIT, GROUP, and BOX examples; other valid codes return a simulated 404 item. The optional `pg` query parameter is intentionally omitted.

## Run

```powershell
dotnet restore CrptWorkbench.sln -p:RestoreUseStaticGraphEvaluation=true
dotnet run --project src/CrptWorkbench.Web/CrptWorkbench.Web.csproj
```

Open `http://localhost:5065` with the default http launch profile. A demo profile and SQLite database are created on first start under `src/CrptWorkbench.Web/App_Data`.

## Logging

Serilog writes separate `server` and `client` files in `Crpt:DataDirectory/logs`. Client means Blazor interface events, recorded on the server. Configure levels through `Serilog:LevelSwitches:$server` and `$client` in appsettings.json; levels reload automatically. File paths and rotation are configured in the two Serilog file sinks and require restart. Defaults are Information, daily/20 MB rotation and 31 files per channel. Console logging is disabled.

## Windows Server production

Build a self-contained service package:

```powershell
PowerShell -ExecutionPolicy Bypass -File .\deploy\windows\Build-Package.ps1
```

The package is written to `artifacts/windows/CrptWorkbench-win-x64.zip`. See [the Windows production deployment guide](docs/deployment/windows-production.md) for CryptoPro, certificate, service-account, installation, verification and rollback steps. The service binds only to `127.0.0.1:5080`.

See [ARCHITECTURE.md](ARCHITECTURE.md) for layer boundaries and extension points.

