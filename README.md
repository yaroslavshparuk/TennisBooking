# TennisBooking

Application for automated tennis court booking in Skedda with Telegram notifications.

## Features

- Automated tennis court booking via Skedda API
- Telegram notifications for booking status

## Requirements

- .NET 10.0
- PostgreSQL database
- Docker (for deployment)

## Quick Start

1. Clone the repository
2. Update `appsettings.json`
3. Run the application:
   ```bash
   dotnet run --project src/TennisBooking
   ```

## Deployment

Build and run the container image from the repository root:
```bash
docker build -t tennisbooking .
docker run -d --name tennisbooking -p 5000:5000 \
  -e ConnectionStrings__Default=... \
  tennisbooking
```

For Dokploy, set the build context/root directory to the repository root. The
Dockerfile depends on `Directory.Packages.props` and the sibling projects under
`src/`, so `src/TennisBooking` cannot be used as the Docker build context.

The application will be available at `http://localhost:5000` (or port 80 when deployed).
Use `/health` for container liveness checks. Use `/health/ready` for the deeper
readiness check that verifies PostgreSQL and booking preparation.

## Configuration

Configure the following environment variables:
- `ConnectionStrings__Default` - PostgreSQL connection string
- `Hangfire__DashboardUser` - Hangfire dashboard username
- `Hangfire__DashboardPass` - Hangfire dashboard password
- `Telegram__BotToken` - Telegram bot token
- `Telegram__ChatId` - Telegram chat id
- `Pyroscope__Enabled` - enable/disable profiling (`true`/`false`)
- `Pyroscope__ServerAddress` - Pyroscope server URL (required when enabled)
- `Pyroscope__ApplicationName` - profiler application label (optional, defaults to `TennisBooking`)

The Docker image now includes Pyroscope native profiler binaries and sets:
`CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER`, `CORECLR_PROFILER_PATH`,
`LD_PRELOAD`, and `LD_LIBRARY_PATH` automatically.

For local development, you can use user secrets:
```bash
dotnet user-secrets --project src/TennisBooking set "Telegram:BotToken" "<token>"
dotnet user-secrets --project src/TennisBooking set "Telegram:ChatId" "<chat-id>"
```
