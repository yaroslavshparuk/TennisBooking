# AGENTS.md

## Project

TennisBooking is a personal .NET 10 ASP.NET Core project for convenient tennis court booking through Skedda.

## Useful Commands

- Build: `dotnet build`
- Test: `dotnet test`
- Run locally: `dotnet run --project src/TennisBooking`

## Repository Notes

- Main app entrypoint: `src/TennisBooking/Program.cs`
- The project uses PostgreSQL for real runtime and integration behavior.
- Keep secrets out of commits. Prefer environment variables or user secrets over hardcoded values in `appsettings.json`.
- Preserve the existing project split between Domain, Application, Infrastructure, and the web app.
- Keep changes scoped to the requested behavior.
