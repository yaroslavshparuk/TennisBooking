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
ARG PYROSCOPE_VERSION=0.15.0
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl libgssapi-krb5-2 \
    && mkdir -p /dotnet \
    && curl -sSL "https://github.com/grafana/pyroscope-dotnet/releases/download/v${PYROSCOPE_VERSION}-pyroscope/pyroscope.${PYROSCOPE_VERSION}-glibc-x86_64.tar.gz" \
      | tar xz -C /dotnet \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production \
    CORECLR_ENABLE_PROFILING=1 \
    CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A} \
    CORECLR_PROFILER_PATH=/dotnet/Pyroscope.Profiler.Native.so \
    LD_PRELOAD=/dotnet/Pyroscope.Linux.ApiWrapper.x64.so \
    LD_LIBRARY_PATH=/dotnet
EXPOSE 5000
ENTRYPOINT ["dotnet", "TennisBooking.dll"]
