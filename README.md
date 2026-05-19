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

## Configuration

Configure the following environment variables:
- `ConnectionStrings__Default` - PostgreSQL connection string
- `Hangfire__DashboardUser` - Hangfire dashboard username
- `Hangfire__DashboardPass` - Hangfire dashboard password
