FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY TennisBooking.slnx ./
COPY src/TennisBooking.Domain/TennisBooking.Domain.csproj src/TennisBooking.Domain/
COPY src/TennisBooking.Application/TennisBooking.Application.csproj src/TennisBooking.Application/
COPY src/TennisBooking.Infrastructure/TennisBooking.Infrastructure.csproj src/TennisBooking.Infrastructure/
COPY src/TennisBooking/TennisBooking.csproj src/TennisBooking/

RUN dotnet restore TennisBooking.slnx --disable-parallel

COPY . .
RUN dotnet publish src/TennisBooking/TennisBooking.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5000
ENTRYPOINT ["dotnet", "TennisBooking.dll"]
