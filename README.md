# TennisBooking

Application for automated tennis court booking in Skedda with Telegram notifications.

## Features

- Automated tennis court booking via Skedda API
- Telegram notifications for booking status

## Requirements

- .NET 9.0
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

Use the provided deployment script:
```bash
./deploy.sh
```

The application will be available at `http://localhost:5000` (or port 80 when deployed).

## Configuration

Configure the following environment variables:
- `ConnectionStrings__Default` - PostgreSQL connection string
- `Hangfire__DashboardUser` - Hangfire dashboard username
- `Hangfire__DashboardPass` - Hangfire dashboard password
